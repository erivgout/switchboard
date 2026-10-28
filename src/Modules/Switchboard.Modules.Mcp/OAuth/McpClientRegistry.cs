using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Mcp.Data;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Switchboard.Modules.Mcp.OAuth;

public sealed record ClientRegistration(
    string Name,
    IReadOnlyList<string> RedirectUris,
    string? ClientUri,
    bool Confidential,
    McpClientSource Source,
    string? MetadataUrl = null);

public sealed record RegisteredClient(McpClient Client, string? ClientSecret);

/// <summary>Fetches a Client ID Metadata Document over the SSRF-safe client.</summary>
public interface IClientMetadataFetcher
{
    Task<string> FetchAsync(Uri url, CancellationToken ct);
}

internal sealed class ClientMetadataFetcher : IClientMetadataFetcher, IDisposable
{
    private readonly HttpClient _http = SafeHttp.CreateClient();

    public async Task<string> FetchAsync(Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"The metadata document returned {(int)response.StatusCode}.");
        }
        return await SafeHttp.ReadCappedAsync(response, SafeHttp.MaxDocumentBytes, ct);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Every way a client becomes known (MCP-11): Dynamic Client Registration, Client ID Metadata Documents,
/// and admin pre-registration. Keeps the OpenIddict application and the <see cref="McpClient"/> row in step.
/// </summary>
public sealed class McpClientRegistry(
    SwitchboardDbContext db,
    IOpenIddictApplicationManager applications,
    ISettingsService settings,
    IClientMetadataFetcher fetcher,
    IAuditLog audit,
    IActorAccessor actors,
    TimeProvider time,
    ILogger<McpClientRegistry> logger)
{
    private static readonly TimeSpan MetadataRefresh = TimeSpan.FromHours(24);

    public async Task<RegisteredClient> RegisterAsync(ClientRegistration registration, string? fromIp, CancellationToken ct)
    {
        var name = (registration.Name ?? "").Trim();
        if (name.Length is 0 or > 200)
        {
            throw new ValidationException("client_name", "Give the client a name of 1 to 200 characters.");
        }
        var redirects = ValidateRedirectUris(registration.RedirectUris);
        var clientUri = ValidateClientUri(registration.ClientUri);

        var clientId = registration.Source == McpClientSource.MetadataDocument ? registration.MetadataUrl! : $"mcp_{RandomToken(16)}";
        var secret = registration.Confidential ? RandomToken(32) : null;

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = secret,
            ClientType = registration.Confidential ? ClientTypes.Confidential : ClientTypes.Public,
            ApplicationType = ApplicationTypes.Native,
            ConsentType = ConsentTypes.Explicit,
            DisplayName = name,
        };
        foreach (var uri in redirects)
        {
            descriptor.RedirectUris.Add(new Uri(uri));
        }
        descriptor.Permissions.UnionWith(
        [
            Permissions.Endpoints.Authorization,
            Permissions.Endpoints.Token,
            Permissions.Endpoints.Revocation,
            Permissions.GrantTypes.AuthorizationCode,
            Permissions.GrantTypes.RefreshToken,
            Permissions.ResponseTypes.Code,
        ]);
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);

        await applications.CreateAsync(descriptor, ct);

        var client = new McpClient
        {
            ClientId = clientId,
            Name = name,
            Source = registration.Source,
            MetadataUrl = registration.MetadataUrl,
            ClientUri = clientUri,
            RedirectUris = redirects,
            IsConfidential = registration.Confidential,
            CreatedAt = time.GetUtcNow(),
            CreatedById = actors.Current.UserId,
            RegisteredFromIp = fromIp,
            MetadataFetchedAt = registration.Source == McpClientSource.MetadataDocument ? time.GetUtcNow() : null,
        };
        db.Set<McpClient>().Add(client);
        audit.Record("mcp.client.registered", "mcp_client", clientId, new { name, source = registration.Source.ToString(), redirectUris = redirects, confidential = registration.Confidential, ip = fromIp });
        await db.SaveChangesAsync(ct);
        return new RegisteredClient(client, secret);
    }

    /// <summary>
    /// For a client_id that is an HTTPS URL: fetch and validate its metadata document and keep the client
    /// registered (refreshed daily). Returns an error message when the client can't be used.
    /// </summary>
    public async Task<string?> EnsureMetadataClientAsync(string clientId, bool adminApproved, CancellationToken ct)
    {
        if (!IsMetadataClientId(clientId, out var url))
        {
            return "That client_id isn't a valid metadata document URL.";
        }

        var existing = await db.Set<McpClient>().FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
        if (existing is not null && existing.MetadataFetchedAt > time.GetUtcNow() - MetadataRefresh)
        {
            return null;
        }
        if (existing is null && !adminApproved && !await settings.GetAsync(McpSettings.OpenRegistration, true, ct: ct))
        {
            return "This Switchboard only accepts agents your administrator has approved.";
        }

        JsonObject document;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SafeHttp.Timeout);
            document = JsonNode.Parse(await fetcher.FetchAsync(url, timeout.Token)) as JsonObject
                ?? throw new JsonException("Not a JSON object.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or IOException)
        {
            logger.LogInformation(ex, "Couldn't load client metadata document {Url}", clientId);
            return existing is null ? "The client's metadata document couldn't be loaded." : null;
        }

        if (document["client_id"]?.GetValue<string>() != clientId)
        {
            return "The client's metadata document doesn't name itself as this client.";
        }
        var authMethod = document["token_endpoint_auth_method"]?.GetValue<string>() ?? "none";
        if (authMethod != "none")
        {
            return "Metadata-document clients must be public clients (token_endpoint_auth_method none).";
        }
        var redirects = (document["redirect_uris"] as JsonArray)?.Select(r => r?.GetValue<string>() ?? "").ToList() ?? [];
        var name = document["client_name"]?.GetValue<string>() is { Length: > 0 } n ? n : url.Host;
        var clientUri = document["client_uri"]?.GetValue<string>();

        try
        {
            if (existing is null)
            {
                await RegisterAsync(new ClientRegistration(name, redirects, clientUri, false, McpClientSource.MetadataDocument, clientId), null, ct);
                return null;
            }

            var validRedirects = ValidateRedirectUris(redirects);
            var application = await applications.FindByClientIdAsync(clientId, ct);
            if (application is not null)
            {
                var descriptor = new OpenIddictApplicationDescriptor();
                await applications.PopulateAsync(descriptor, application, ct);
                descriptor.DisplayName = Limit(name, 200);
                descriptor.RedirectUris.Clear();
                foreach (var uri in validRedirects)
                {
                    descriptor.RedirectUris.Add(new Uri(uri));
                }
                await applications.UpdateAsync(application, descriptor, ct);
            }
            existing.Name = Limit(name, 200);
            existing.RedirectUris = validRedirects;
            existing.ClientUri = ValidateClientUri(clientUri);
            existing.MetadataFetchedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (ValidationException ex)
        {
            return ex.Message;
        }
    }

    public async Task BlockAsync(string clientId, bool blocked, CancellationToken ct)
    {
        var client = await db.Set<McpClient>().FirstOrDefaultAsync(c => c.ClientId == clientId, ct) ?? throw new NotFoundException();
        client.BlockedAt = blocked ? time.GetUtcNow() : null;
        client.BlockedById = blocked ? actors.Current.UserId : null;
        audit.Record(blocked ? "mcp.client.blocked" : "mcp.client.unblocked", "mcp_client", clientId, new { client.Name });
        await db.SaveChangesAsync(ct);
    }

    public static bool IsMetadataClientId(string? clientId, out Uri url)
    {
        url = null!;
        if (string.IsNullOrEmpty(clientId) || clientId.Length > 400
            || !Uri.TryCreate(clientId, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps || parsed.AbsolutePath is "" or "/"
            || !string.IsNullOrEmpty(parsed.Fragment) || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }
        url = parsed;
        return true;
    }

    /// <summary>
    /// Redirect URIs must be HTTPS, HTTP on a loopback address (native apps, RFC 8252), or a private-use
    /// scheme such as <c>cursor://</c>. No fragments, no wildcards; matching is exact.
    /// </summary>
    public static List<string> ValidateRedirectUris(IEnumerable<string>? uris)
    {
        var list = (uris ?? []).Select(u => (u ?? "").Trim()).Where(u => u.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0)
        {
            throw new ValidationException("redirect_uris", "Add at least one redirect URI.");
        }
        if (list.Count > 10)
        {
            throw new ValidationException("redirect_uris", "Use at most 10 redirect URIs.");
        }
        foreach (var uri in list)
        {
            if (uri.Length > 400 || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !string.IsNullOrEmpty(parsed.Fragment)
                || uri.Contains('*') || !string.IsNullOrEmpty(parsed.UserInfo))
            {
                throw new ValidationException("redirect_uris", "Each redirect URI must be an absolute URI without a fragment or wildcard.");
            }
            var ok = parsed.Scheme switch
            {
                "https" => true,
                "http" => parsed.IsLoopback,
                "javascript" or "data" or "file" or "vbscript" or "about" or "blob" or "ftp" or "ws" or "wss" => false,
                _ => parsed.Scheme.Length > 1,
            };
            if (!ok)
            {
                throw new ValidationException("redirect_uris", "Redirect URIs must use HTTPS, HTTP on localhost, or an app's own scheme.");
            }
        }
        return list;
    }

    private static string? ValidateClientUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }
        return Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http" && uri.Length <= 400
            ? parsed.AbsoluteUri
            : null;
    }

    private static string RandomToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];
}
