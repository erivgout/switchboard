using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Services;
using Switchboard.Modules.Mcp.Data;
using Switchboard.Modules.Mcp.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Switchboard.Modules.Mcp.OAuth;

/// <summary>Adds Dynamic Client Registration and Client ID Metadata Document support to the AS metadata (RFC 8414).</summary>
internal sealed class DiscoveryMetadataHandler(IOptions<SwitchboardOptions> options) : IOpenIddictServerHandler<HandleConfigurationRequestContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<HandleConfigurationRequestContext>()
            .UseScopedHandler<DiscoveryMetadataHandler>()
            .SetOrder(int.MaxValue - 100_000)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public ValueTask HandleAsync(HandleConfigurationRequestContext context)
    {
        var baseUrl = options.Value.BaseUrl.TrimEnd('/');
        context.Metadata["registration_endpoint"] = $"{baseUrl}{McpPaths.Register}";
        context.Metadata["client_id_metadata_document_supported"] = true;
        context.Metadata["service_documentation"] = $"{baseUrl}/admin/mcp";
        // Public clients (most agent apps) authenticate at the token endpoint with PKCE only.
        context.TokenEndpointAuthenticationMethods.Add("none");
        context.TokenEndpointAuthenticationMethods.Remove(ClientAuthenticationMethods.PrivateKeyJwt);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Client ID Metadata Documents (MCP-11): when an authorization request's client_id is an HTTPS URL,
/// load the document (SSRF-safe) and register or refresh the client before OpenIddict validates it.
/// </summary>
internal sealed class ClientMetadataDocumentHandler(McpClientRegistry registry) : IOpenIddictServerHandler<ValidateAuthorizationRequestContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateAuthorizationRequestContext>()
            .UseScopedHandler<ClientMetadataDocumentHandler>()
            .SetOrder(int.MinValue + 100_000)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public async ValueTask HandleAsync(ValidateAuthorizationRequestContext context)
    {
        if (!McpClientRegistry.IsMetadataClientId(context.ClientId, out _))
        {
            return;
        }
        var error = await registry.EnsureMetadataClientAsync(context.ClientId!, adminApproved: false, context.CancellationToken);
        if (error is not null)
        {
            context.Reject(error: Errors.InvalidClient, description: error);
        }
    }
}

/// <summary>
/// Refresh-token reuse detection (MCP-12): presenting a refresh token that was already redeemed revokes
/// the whole grant, not just its token chain, and the user sees it as revoked.
/// </summary>
internal sealed class RefreshTokenReuseHandler(
    IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager authorizations,
    SwitchboardDbContext db,
    McpKillSwitch killSwitch,
    IAuditLog audit,
    TimeProvider time,
    ILogger<RefreshTokenReuseHandler> logger) : IOpenIddictServerHandler<ProcessAuthenticationContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<ProcessAuthenticationContext>()
            .UseScopedHandler<RefreshTokenReuseHandler>()
            .SetOrder(OpenIddictServerHandlers.ValidateRefreshToken.Descriptor.Order - 500)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public async ValueTask HandleAsync(ProcessAuthenticationContext context)
    {
        if (context.EndpointType != OpenIddictServerEndpointType.Token || !context.Request.IsRefreshTokenGrantType()
            || string.IsNullOrEmpty(context.RefreshToken))
        {
            return;
        }

        // Refresh tokens are reference tokens (see McpModule), so the entry can be found before validation.
        var token = await tokens.FindByReferenceIdAsync(context.RefreshToken, context.CancellationToken);
        if (token is not null)
        {
            await RevokeIfRedeemedAsync(token, context.CancellationToken);
        }
    }

    internal async Task RevokeIfRedeemedAsync(object token, CancellationToken ct)
    {
        if (!await tokens.HasStatusAsync(token, Statuses.Redeemed, ct))
        {
            return;
        }
        var authorizationId = await tokens.GetAuthorizationIdAsync(token, ct);
        if (authorizationId is null || !Guid.TryParse(authorizationId, out var grantId))
        {
            return;
        }
        logger.LogWarning("A redeemed refresh token was presented again; revoking grant {Grant}", grantId);
        if (await authorizations.FindByIdAsync(authorizationId, ct) is { } authorization)
        {
            await authorizations.TryRevokeAsync(authorization, ct);
        }
        var grant = await db.Set<McpGrant>().FirstOrDefaultAsync(g => g.Id == grantId, ct);
        if (grant is not null && grant.RevokedAt is null)
        {
            grant.RevokedAt = time.GetUtcNow();
            grant.RevokedReason = "refresh_reuse";
            audit.Record("mcp.grant.revoked", "mcp_grant", grantId.ToString(), new { reason = "refresh_token_reuse", grant.ClientId, grant.UserId });
            await db.SaveChangesAsync(ct);
        }
        killSwitch.RevokeGrant(grantId);
    }
}

internal static class OAuthJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
