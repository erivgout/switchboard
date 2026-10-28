using System.Collections.Immutable;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Mcp.Components;
using Switchboard.Modules.Mcp.Data;
using Switchboard.Modules.Mcp.Security;
using Switchboard.Modules.Mcp.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Switchboard.Modules.Mcp.Areas.Mcp.Pages.OAuth;

/// <summary>
/// The consent screen (MCP-13): who is asking, where they come from, where the browser returns,
/// what they may do in plain language, which workspaces, and for how long. The user can narrow it.
/// </summary>
[AllowAnonymous]
public sealed class AuthorizeModel(
    SwitchboardDbContext db,
    IOpenIddictApplicationManager applications,
    IOpenIddictAuthorizationManager authorizations,
    IPermissionService permissions,
    ISettingsService settings,
    McpToolCatalog catalog,
    IAuditLog audit,
    IActorAccessor actors,
    IOptions<SwitchboardOptions> options,
    TimeProvider time) : PageModel
{
    public sealed record ScopeOption(string Key, string Title, string Consent, bool LeavesOrganization, bool Checked);

    public static readonly int[] ExpiryChoices = [1, 7, 30, 90];

    /// <summary>Form fields of this page; everything else posted back is the original OAuth request.</summary>
    private static readonly HashSet<string> OwnFields = new(StringComparer.Ordinal) { nameof(GrantedScopes), nameof(ExpiryDays), nameof(Decision), "__RequestVerificationToken" };

    public string ClientName { get; private set; } = "This app";
    public string ClientDomain { get; private set; } = "";
    public string ClientSource { get; private set; } = "";
    public bool ClientUnverified { get; private set; }
    public string RedirectHost { get; private set; } = "";
    public string UserName { get; private set; } = "";
    public string OrganizationName { get; private set; } = "Switchboard";
    public string EndpointName { get; private set; } = "";
    public IReadOnlyList<string> Workspaces { get; private set; } = [];
    public IReadOnlyList<ScopeOption> Scopes { get; private set; } = [];
    public IReadOnlyList<KeyValuePair<string, string>> Parameters { get; private set; } = [];
    public string? Problem { get; private set; }
    public bool IsAgent { get; private set; }
    public string? Error { get; private set; }

    [BindProperty]
    public List<string> GrantedScopes { get; set; } = [];

    [BindProperty]
    public int ExpiryDays { get; set; } = 30;

    [BindProperty]
    public string? Decision { get; set; }

    public string ExpiryLabel(int days) =>
        $"{days} {(days == 1 ? "day" : "days")}, until {time.GetUtcNow().AddDays(days).ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}";

    private string? _resource;
    private string _slug = "";

    public async Task<IActionResult> OnGetAsync()
    {
        var request = HttpContext.GetOpenIddictServerRequest();
        if (request is null)
        {
            Problem = "This page opens when an AI agent app asks to connect. Start from the app.";
            return Page();
        }
        if (User.Identity?.IsAuthenticated != true || ActorAccessor.FromPrincipal(User, Request.Path).UserId is null)
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Deny(Errors.LoginRequired, "Sign in to Switchboard first.");
            }
            return Challenge(new AuthenticationProperties { RedirectUri = Request.PathBase + Request.Path + Request.QueryString }, IdentityConstants.ApplicationScheme);
        }
        if (request.HasPromptValue(PromptValues.None))
        {
            return Deny(Errors.ConsentRequired, "The user must approve this connection.");
        }

        await BuildAsync(request, posted: false);
        SetContentSecurityPolicy(request.RedirectUri);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var request = HttpContext.GetOpenIddictServerRequest();
        if (request is null)
        {
            Problem = "This page opens when an AI agent app asks to connect. Start from the app.";
            return Page();
        }
        var userId = ActorAccessor.FromPrincipal(User, Request.Path).UserId;
        if (User.Identity?.IsAuthenticated != true || userId is null)
        {
            return Challenge(new AuthenticationProperties { RedirectUri = Request.PathBase + Request.Path }, IdentityConstants.ApplicationScheme);
        }

        await BuildAsync(request, posted: true);
        if (Decision != "allow" || Problem is not null)
        {
            await audit.WriteAsync("mcp.grant.declined", "mcp_client", request.ClientId, new { reason = Problem is null ? "user" : "not_allowed" });
            return Deny(Errors.AccessDenied, Problem is null ? "The user didn't allow the connection." : "This connection isn't allowed.");
        }

        var offered = Scopes.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var granted = McpScopes.All.Select(s => s.Key).Where(k => offered.Contains(k) && GrantedScopes.Contains(k)).ToList();
        if (granted.Count == 0)
        {
            Error = "Choose at least one thing the agent may do, or deny the connection.";
            SetContentSecurityPolicy(request.RedirectUri);
            return Page();
        }
        var days = ExpiryChoices.Contains(ExpiryDays) ? ExpiryDays : 30;

        var application = await applications.FindByClientIdAsync(request.ClientId!) ?? throw new InvalidOperationException("Unknown client.");
        var applicationId = await applications.GetIdAsync(application) ?? throw new InvalidOperationException("Client without id.");

        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, userId.Value.ToString());
        identity.SetScopes([.. granted, OpenIddictConstants.Scopes.OfflineAccess]);
        identity.SetResources(_resource!);

        var authorization = await authorizations.CreateAsync(identity, userId.Value.ToString(), applicationId, AuthorizationTypes.Permanent, identity.GetScopes());
        var authorizationId = await authorizations.GetIdAsync(authorization) ?? throw new InvalidOperationException("Authorization without id.");
        identity.SetAuthorizationId(authorizationId);
        identity.SetDestinations(_ => [Destinations.AccessToken]);

        var now = time.GetUtcNow();
        var grant = new McpGrant
        {
            Id = Guid.Parse(authorizationId),
            UserId = userId.Value,
            ClientId = request.ClientId!,
            ClientName = ClientName,
            Scopes = granted,
            Resource = _resource!,
            ConfigurationSlug = _slug,
            RedirectHost = RedirectHost.Length > 255 ? RedirectHost[..255] : RedirectHost,
            CreatedAt = now,
            ExpiresAt = now.AddDays(days),
        };
        db.Set<McpGrant>().Add(grant);
        // SR-AUD-1: the lifecycle of MCP grants is audited.
        audit.Record("mcp.grant.created", "mcp_grant", grant.Id.ToString(), new { grant.ClientId, grant.ClientName, scopes = granted, grant.Resource, expiresAt = grant.ExpiresAt });
        await db.SaveChangesAsync();

        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private IActionResult Deny(string error, string description) =>
        Forbid(new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

    private async Task BuildAsync(OpenIddictRequest request, bool posted)
    {
        var baseUrl = options.Value.BaseUrl;
        var userId = actors.Current.UserId!.Value;
        Parameters = request.GetParameters()
            .Where(p => !OwnFields.Contains(p.Key))
            .SelectMany(p => ((StringValues)p.Value).OfType<string>().Select(v => KeyValuePair.Create(p.Key, v)))
            .ToList();

        UserName = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync() ?? "you";
        OrganizationName = await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).Select(o => o.Name).FirstOrDefaultAsync() ?? "Switchboard";
        RedirectHost = ClientDisplay.RedirectHost(request.RedirectUri);

        var client = await db.Set<McpClient>().AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == request.ClientId);
        if (client is not null)
        {
            ClientName = client.Name;
            ClientDomain = ClientDisplay.Domain(client);
            ClientSource = ClientDisplay.Describe(client);
            ClientUnverified = client.Source == McpClientSource.Dynamic;
        }

        var perms = await permissions.ForUserAsync(userId);
        if (perms is null)
        {
            Problem = "Your account can't connect AI agents.";
            return;
        }
        if (!await settings.GetAsync(McpSettings.Enabled, true))
        {
            Problem = "Your administrator has turned off AI agent access.";
            return;
        }
        if (client is null || client.BlockedAt is not null)
        {
            Problem = "Your administrator has blocked this app.";
            return;
        }

        var resources = request.GetResources().ToList();
        _resource = resources.Count <= 1 ? McpPaths.NormalizeResource(baseUrl, resources.FirstOrDefault()) : null;
        _slug = _resource is null ? "" : McpPaths.SlugOf(new PathString(new Uri(_resource).AbsolutePath)) ?? "";
        var configuration = _resource is null ? null : await db.Set<McpServerConfiguration>().AsNoTracking().FirstOrDefaultAsync(c => c.Slug == _slug);
        if (configuration is null || !configuration.IsEnabled)
        {
            Problem = "The app asked for an address this Switchboard doesn't serve.";
            return;
        }
        if (configuration.AllowedClientIds.Count > 0 && !configuration.AllowedClientIds.Contains(client.ClientId))
        {
            Problem = "This app isn't allowed here. Ask your administrator.";
            return;
        }
        if (!McpAccessPolicy.RoleAllowed(configuration, perms))
        {
            Problem = "Your role can't use AI agents here. Ask your administrator if you need it.";
            return;
        }
        EndpointName = _slug.Length == 0 ? "" : configuration.Name;
        IsAgent = perms.IsAgent;

        if (perms.IsOrgAdmin)
        {
            Workspaces = ["Every workspace"];
        }
        else if (perms.IsAgent)
        {
            var ids = perms.Workspaces.Keys.ToList();
            Workspaces = await db.Workspaces.AsNoTracking().Where(w => ids.Contains(w.Id) && !w.IsArchived).OrderBy(w => w.Name).Select(w => w.Name).ToListAsync();
        }
        else
        {
            Workspaces = ["Your own requests"];
        }

        // Offer what was asked for (or, if nothing was, every scope a tool here uses), limited to what this user can hold.
        var toolScopes = catalog.All.Select(t => t.Definition.Scope).ToHashSet(StringComparer.Ordinal);
        var requested = McpScopes.Normalize(request.GetScopes(), perms.IsAgent);
        var offered = requested.Count > 0
            ? requested
            : McpScopes.Normalize(McpScopes.All.Select(s => s.Key).Where(k => k == McpScopes.Profile || toolScopes.Contains(k)), perms.IsAgent);
        Scopes = offered.Select(k => McpScopes.Find(k)!).Select(d => new ScopeOption(d.Key, d.TitleFor(perms.IsAgent), d.ConsentFor(perms.IsAgent), d.LeavesOrganization,
            posted ? GrantedScopes.Contains(d.Key) : requested.Count > 0 || d.Key is McpScopes.Profile or McpScopes.TicketsRead)).ToList();
    }

    /// <summary>The browser must be allowed to follow the post-consent redirect to the client (CSP form-action).</summary>
    private void SetContentSecurityPolicy(string? redirectUri)
    {
        var target = "";
        if (Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
        {
            target = uri.Scheme is "http" or "https" ? $"{uri.Scheme}://{uri.Authority}" : $"{uri.Scheme}:";
        }
        Response.Headers.ContentSecurityPolicy = string.Join("; ",
            "default-src 'self'",
            "script-src 'self'",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data:",
            "font-src 'self'",
            "connect-src 'self'",
            "frame-ancestors 'none'",
            $"form-action 'self' {target}".TrimEnd(),
            "base-uri 'self'",
            "object-src 'none'");
    }
}
