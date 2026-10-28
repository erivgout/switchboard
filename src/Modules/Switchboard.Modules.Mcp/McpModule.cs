using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using Switchboard.Core.Agents;
using Switchboard.Core.Data;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.Modules.Mcp.Components;
using Switchboard.Modules.Mcp.Data;
using Switchboard.Modules.Mcp.OAuth;
using Switchboard.Modules.Mcp.Security;
using Switchboard.Modules.Mcp.Server;
using Switchboard.Modules.Mcp.Tools;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Switchboard.Modules.Mcp;

/// <summary>
/// The built-in MCP server (SRS §4): an OAuth 2.1 authorization server, the Streamable HTTP endpoint at
/// /mcp, every module's <see cref="IAgentTool"/> exposed under the user's permissions, human confirmation,
/// audit, kill switches, and the screens around them.
/// </summary>
public sealed class McpModule : ISwitchboardModule
{
    public const string ManagePrivilege = "mcp.manage";

    public string Name => "Mcp";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var baseUrl = configuration["Switchboard:BaseUrl"] is { Length: > 0 } b ? b : "http://localhost:8080";

        services.AddDataProtection();
        services.AddHttpContextAccessor();

        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore()
                .UseDbContext<SwitchboardDbContext>()
                .ReplaceDefaultEntities<McpOAuthApplication, McpOAuthAuthorization, McpOAuthScope, McpOAuthToken, Guid>())
            .AddServer(o =>
            {
                o.SetIssuer(new Uri(OAuthEndpoints.Issuer(baseUrl)));
                o.SetAuthorizationEndpointUris(McpPaths.Authorize.TrimStart('/'))
                    .SetTokenEndpointUris(McpPaths.Token.TrimStart('/'))
                    .SetRevocationEndpointUris(McpPaths.Revoke.TrimStart('/'));

                // MCP-12: authorization code with PKCE (S256 only); no implicit or password grants.
                o.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow();
                o.RequireProofKeyForCodeExchange();
                o.Configure(options =>
                {
                    options.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain);
                    // form_post needs an auto-submitting script, which the Content-Security-Policy blocks.
                    options.ResponseModes.Remove(ResponseModes.FormPost);
                });

                o.SetAccessTokenLifetime(McpSettings.AccessTokenLifetime);
                o.SetRefreshTokenLifetime(McpSettings.RefreshTokenIdleLifetime);
                o.SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(5));
                // Rolling refresh tokens with no reuse leeway: a used refresh token presented again revokes the grant.
                o.SetRefreshTokenReuseLeeway(null);
                o.UseReferenceRefreshTokens();

                o.RegisterScopes([.. McpScopes.All.Select(s => s.Key), Scopes.OfflineAccess]);
                // Unknown scopes are dropped on the consent screen instead of failing the request; resources
                // (/mcp and /mcp/{slug}) are checked there and at the token endpoint.
                o.DisableScopeValidation();
                o.IgnoreScopePermissions();
                o.DisableResourceValidation();
                o.IgnoreResourcePermissions();

                o.AddEventHandler(DiscoveryMetadataHandler.Descriptor);
                o.AddEventHandler(ClientMetadataDocumentHandler.Descriptor);
                o.AddEventHandler(RefreshTokenReuseHandler.Descriptor);

                var aspNetCore = o.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough();
                if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    // Plain HTTP only for local development (MCP clients allow it for localhost).
                    aspNetCore.DisableTransportSecurityRequirement();
                }
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.UseAspNetCore();
                // Revoked grants and tokens fail on the next call (INV-MCP-5).
                o.EnableAuthorizationEntryValidation();
                o.EnableTokenEntryValidation();
            });
        services.AddSingleton<IConfigureOptions<OpenIddictServerOptions>, McpKeyRing>();

        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, McpBearerHandler>(McpBearerHandler.SchemeName, null);
        services.AddRateLimiter(o => o.AddPolicy(OAuthEndpoints.RegisterRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromHours(1) })));
        services.AddTransient<IStartupFilter, McpEdgeStartupFilter>();

        services.AddSingleton<IPrivilegeScopeGuard, McpScopeGuard>();
        services.AddSingleton<McpRateLimiter>();
        services.AddSingleton<McpKillSwitch>();
        services.AddSingleton<McpSessionRegistry>();
        services.AddSingleton<IClientMetadataFetcher, ClientMetadataFetcher>();
        services.AddScoped<McpToolCatalog>();
        services.AddScoped<McpAccessPolicy>();
        services.AddScoped<McpToolDispatcher>();
        services.AddScoped<PendingActionService>();
        services.AddScoped<McpClientRegistry>();
        services.AddScoped<McpAdminService>();
        services.AddScoped<McpReauthentication>();
        services.AddScoped<TicketToolSupport>();
        services.AddSwitchboardMcpServer();

        services.AddAgentTool<WhoAmITool>();
        services.AddAgentTool<ListWorkspacesTool>();
        services.AddAgentTool<DescribeFieldsTool>();
        services.AddAgentTool<GetPendingActionTool>();
        services.AddAgentTool<SearchTicketsTool>();
        services.AddAgentTool<GetTicketTool>();
        services.AddAgentTool<ListConversationsTool>();
        services.AddAgentTool<CreateTicketTool>();
        services.AddAgentTool<UpdateTicketTool>();
        services.AddAgentTool<AssignTicketTool>();
        services.AddAgentTool<AddPrivateNoteTool>();
        services.AddAgentTool<AddPublicNoteTool>();
        services.AddAgentTool<ReplyToTicketTool>();
        services.AddAgentTool<ForwardTicketTool>();
        services.AddAgentTool<MergeTicketsTool>();
        services.AddAgentTool<DeleteTicketTool>();
        services.AddAgentTool<CreateChildTicketTool>();
        services.AddAgentTool<LinkRecordsTool>();
        services.AddAgentTool<ListTasksTool>();
        services.AddAgentTool<CreateTaskTool>();
        services.AddAgentTool<UpdateTaskTool>();
        services.AddAgentTool<SearchUsersTool>();
        services.AddAgentTool<GetUserTool>();
        services.AddAgentTool<SearchGroupsTool>();

        services.AddPrivileges(new PrivilegeDefinition(ManagePrivilege, "Manage AI agent access", "Administration",
            "Turn the MCP server on or off, choose tools and roles, register and block agent apps, and revoke connections."));
        services.AddNavItem(new NavItem(NavArea.Admin, "mcp", "AI agents (MCP)", "bi-robot", McpPaths.Admin, Order: 10,
            Privilege: ManagePrivilege, Group: "AI and agents", Description: "Let people's own AI agents work in Switchboard for them."));
        services.AddSlot<ConnectedAgentsViewComponent>("profile.sections", order: 50);
        services.AddSlot<AgentActionsIndicatorViewComponent>("topbar.end", order: 40);
        services.AddSlot<AgentActionsMenuViewComponent>("usermenu.items", order: 40);
    }

    public void ConfigureModel(ModelBuilder modelBuilder) => McpModel.Configure(modelBuilder);

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        OAuthEndpoints.Map(endpoints);
        var auth = new AuthorizeAttribute { AuthenticationSchemes = McpBearerHandler.SchemeName };
        endpoints.MapMcp(McpPaths.Mcp).RequireAuthorization(auth).DisableAntiforgery();
        endpoints.MapMcp(McpPaths.Mcp + "/{slug}").RequireAuthorization(auth).DisableAntiforgery();
    }

    public void ConfigureApp(IApplicationBuilder app) => app.UseMiddleware<McpSessionMiddleware>();

    public async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<SwitchboardDbContext>();
        if (!await db.Set<McpServerConfiguration>().AnyAsync(c => c.Slug == "", cancellationToken))
        {
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
            db.Set<McpServerConfiguration>().Add(new McpServerConfiguration { Slug = "", Name = "Default", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>
/// Binds each new Mcp-Session-Id to the caller that opened it, and turns away any other grant or endpoint
/// that presents it (MCP-4). The SDK separately rejects a different user with 403.
/// </summary>
internal sealed class McpSessionMiddleware(RequestDelegate next, McpSessionRegistry sessions)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (McpPaths.SlugOf(context.Request.Path) is null || McpCaller.FromPrincipal(context.User) is not { } caller)
        {
            await next(context);
            return;
        }

        var sessionId = context.Request.Headers["Mcp-Session-Id"].ToString();
        if (sessionId.Length == 0)
        {
            context.Response.OnStarting(() =>
            {
                if (context.Response.Headers["Mcp-Session-Id"].ToString() is { Length: > 0 } created)
                {
                    sessions.Bind(created, caller);
                }
                return Task.CompletedTask;
            });
        }
        else if (!sessions.Matches(sessionId, caller))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", error = new { code = -32001, message = "Session not found. Start a new session." } });
            return;
        }
        else if (HttpMethods.IsDelete(context.Request.Method))
        {
            context.Response.OnCompleted(() =>
            {
                sessions.Forget(sessionId);
                return Task.CompletedTask;
            });
        }

        await next(context);
    }
}
