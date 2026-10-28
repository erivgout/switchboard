using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Agents;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Mcp.Data;
using Switchboard.Modules.Mcp.OAuth;
using Switchboard.Modules.Mcp.Server;
using Switchboard.UI;

namespace Switchboard.Modules.Mcp.Areas.Mcp.Pages.Admin;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + McpModule.ManagePrivilege)]
public abstract class McpAdminPage : PageModel
{
    public static string EndpointUrl(string baseUrl, string slug) => McpPaths.Resource(baseUrl, slug);

    protected IActionResult Invalid(ValidationException ex)
    {
        foreach (var (key, messages) in ex.Errors)
        {
            foreach (var message in messages)
            {
                ModelState.AddModelError(key, message);
            }
        }
        return Page();
    }
}

/// <summary>/admin/mcp: instance switches (MCP-14/20/26, MCP-5, MCP-11) and named endpoints (MCP-22).</summary>
public sealed class IndexModel(McpAdminService admin, IOptions<SwitchboardOptions> options) : McpAdminPage
{
    public string BaseUrl => options.Value.BaseUrl.TrimEnd('/');

    public IReadOnlyList<McpServerConfiguration> Endpoints { get; private set; } = [];

    [BindProperty] public bool Enabled { get; set; }
    [BindProperty] public bool OpenRegistration { get; set; }
    [BindProperty] public bool ApiKeysAllowed { get; set; }
    [BindProperty] public bool AuthorityInClient { get; set; }
    [BindProperty] public string? AllowedOrigins { get; set; }
    [BindProperty] public string? NewSlug { get; set; }
    [BindProperty] public string? NewName { get; set; }

    public async Task OnGetAsync()
    {
        var s = await admin.GetSettingsAsync(HttpContext.RequestAborted);
        Enabled = s.Enabled;
        OpenRegistration = s.OpenRegistration;
        ApiKeysAllowed = s.ApiKeysAllowed;
        AuthorityInClient = s.AuthorityConfirmation == "in_client";
        AllowedOrigins = string.Join("\n", s.AllowedOrigins);
        Endpoints = await admin.ListConfigurationsAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostSettingsAsync()
    {
        var origins = (AllowedOrigins ?? "").Split(['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            await admin.SaveSettingsAsync(new McpSettingsView(Enabled, OpenRegistration, ApiKeysAllowed, AuthorityInClient ? "in_client" : "out_of_band", origins), HttpContext.RequestAborted);
        }
        catch (ValidationException ex)
        {
            Endpoints = await admin.ListConfigurationsAsync(HttpContext.RequestAborted);
            return Invalid(ex);
        }
        TempData.Toast(Enabled ? "Settings saved" : "Settings saved. AI agents can't connect until you turn access back on.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateEndpointAsync()
    {
        try
        {
            var config = await admin.CreateConfigurationAsync(NewSlug ?? "", NewName ?? "", HttpContext.RequestAborted);
            TempData.Toast("Endpoint added");
            return Redirect($"{McpPaths.Admin}/tools?endpoint={config.Slug}");
        }
        catch (ValidationException ex)
        {
            await OnGetAsync();
            return Invalid(ex);
        }
    }

    public async Task<IActionResult> OnPostDeleteEndpointAsync(string slug)
    {
        await admin.DeleteConfigurationAsync(slug ?? "", HttpContext.RequestAborted);
        TempData.Toast("Endpoint deleted. Agents connected to it were disconnected.");
        return RedirectToPage();
    }
}

/// <summary>/admin/mcp/tools: per-endpoint access (MCP-21) and tool switches (MCP-22).</summary>
public sealed class ToolsModel(McpAdminService admin, SwitchboardDbContext db, IOptions<SwitchboardOptions> options) : McpAdminPage
{
    public string BaseUrl => options.Value.BaseUrl.TrimEnd('/');

    [BindProperty(SupportsGet = true)] public string? Endpoint { get; set; }

    public McpServerConfiguration Configuration { get; private set; } = default!;
    public IReadOnlyList<McpServerConfiguration> Endpoints { get; private set; } = [];
    public IReadOnlyList<AgentToolDefinition> Tools { get; private set; } = [];
    public IReadOnlyList<Role> Roles { get; private set; } = [];

    [BindProperty] public bool EndpointEnabled { get; set; }
    [BindProperty] public bool AllowRequesters { get; set; }
    [BindProperty] public List<Guid> RoleIds { get; set; } = [];
    [BindProperty] public List<string> EnabledTools { get; set; } = [];

    private string Slug => (Endpoint ?? "").ToLowerInvariant();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }
        EndpointEnabled = Configuration.IsEnabled;
        AllowRequesters = Configuration.AllowRequesters;
        RoleIds = Configuration.AllowedRoleIds;
        return Page();
    }

    public async Task<IActionResult> OnPostAccessAsync()
    {
        await admin.SaveAccessAsync(Slug, EndpointEnabled, AllowRequesters, RoleIds, HttpContext.RequestAborted);
        TempData.Toast("Access saved");
        return Redirect($"{McpPaths.Admin}/tools{(Slug.Length > 0 ? $"?endpoint={Slug}" : "")}");
    }

    public async Task<IActionResult> OnPostToolsAsync()
    {
        await admin.SaveEnabledToolsAsync(Slug, EnabledTools, HttpContext.RequestAborted);
        TempData.Toast("Tools saved. Connected agents were told the list changed.");
        return Redirect($"{McpPaths.Admin}/tools{(Slug.Length > 0 ? $"?endpoint={Slug}" : "")}");
    }

    private async Task<bool> LoadAsync()
    {
        Endpoints = await admin.ListConfigurationsAsync(HttpContext.RequestAborted);
        var config = Endpoints.FirstOrDefault(e => e.Slug == Slug);
        if (config is null)
        {
            return false;
        }
        Configuration = config;
        var order = Security.McpScopes.All.Select(s => s.Key).ToList();
        Tools = admin.AllTools().OrderBy(t => order.IndexOf(t.Scope) is var i && i < 0 ? int.MaxValue : i).ThenBy(t => t.Title).ToList();
        Roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(HttpContext.RequestAborted);
        return true;
    }
}

/// <summary>/admin/mcp/clients: registered agent apps (MCP-11, MCP-23): register, approve by URL, block.</summary>
public sealed class ClientsModel(McpAdminService admin) : McpAdminPage
{
    public IReadOnlyList<ClientRow> Clients { get; private set; } = [];
    public RegisteredClient? Registered { get; private set; }

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? RedirectUris { get; set; }
    [BindProperty] public bool Confidential { get; set; }
    [BindProperty] public string? MetadataUrl { get; set; }

    public async Task OnGetAsync() => Clients = await admin.ListClientsAsync(HttpContext.RequestAborted);

    public async Task<IActionResult> OnPostRegisterAsync()
    {
        try
        {
            var uris = (RedirectUris ?? "").Split(['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Registered = await admin.RegisterClientAsync(Name ?? "", uris, Confidential, HttpContext.RequestAborted);
            Name = RedirectUris = null;
            Confidential = false;
        }
        catch (ValidationException ex)
        {
            await OnGetAsync();
            return Invalid(ex);
        }
        // Shown once, on this response only; the secret is stored hashed.
        await OnGetAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAllowUrlAsync()
    {
        var error = await admin.AllowMetadataClientAsync(MetadataUrl ?? "", HttpContext.RequestAborted);
        if (error is not null)
        {
            ModelState.AddModelError(nameof(MetadataUrl), error);
            await OnGetAsync();
            return Page();
        }
        TempData.Toast("App approved");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBlockAsync(string clientId, bool blocked)
    {
        await admin.SetClientBlockedAsync(clientId, blocked, HttpContext.RequestAborted);
        TempData.Toast(blocked ? "App blocked. Its calls are refused from now on." : "App unblocked");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDisconnectAllAsync(string clientId)
    {
        var count = await admin.RevokeClientGrantsAsync(clientId, HttpContext.RequestAborted);
        TempData.Toast(count == 1 ? "1 connection ended" : $"{count} connections ended");
        return RedirectToPage();
    }
}

/// <summary>/admin/mcp/connections: active grants per user and app (MCP-23), with revoke.</summary>
public sealed class ConnectionsModel(McpAdminService admin, SwitchboardDbContext db) : McpAdminPage
{
    [BindProperty(SupportsGet = true, Name = "user")] public string? UserQuery { get; set; }
    [BindProperty(SupportsGet = true, Name = "client")] public string? Client { get; set; }

    public Guid? UserId { get; private set; }
    public string? UserLabel { get; private set; }
    public IReadOnlyList<GrantRow> Grants { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (!string.IsNullOrWhiteSpace(UserQuery))
        {
            var q = UserQuery.Trim();
            var normalized = q.ToUpperInvariant();
            var id = Guid.TryParse(q, out var parsed) ? parsed : Guid.Empty;
            var match = await db.Users.AsNoTracking()
                .Where(u => u.NormalizedEmail == normalized || u.Id == id || u.DisplayName == q)
                .Select(u => new { u.Id, u.DisplayName }).FirstOrDefaultAsync(HttpContext.RequestAborted);
            UserId = match?.Id ?? Guid.Empty;
            UserLabel = match?.DisplayName;
        }
        Grants = await admin.ListGrantsAsync(UserId, Client, includeEnded: false, HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid grantId)
    {
        var done = await admin.RevokeGrantAsync(grantId, HttpContext.RequestAborted);
        TempData.Toast(done ? "Connection ended" : "That connection had already ended.");
        return Redirect(Request.Path + Request.QueryString);
    }

    public async Task<IActionResult> OnPostRevokeUserAsync(Guid userId)
    {
        var count = await admin.RevokeUserGrantsAsync(userId, HttpContext.RequestAborted);
        TempData.Toast(count == 1 ? "1 connection ended" : $"{count} connections ended");
        return Redirect(Request.Path + Request.QueryString);
    }
}

/// <summary>/admin/mcp/activity: usage counts (MCP-23/24) and recent calls.</summary>
public sealed class ActivityModel(McpAdminService admin, SwitchboardDbContext db) : McpAdminPage
{
    public int Today { get; private set; }
    public int Week { get; private set; }
    public int Errors { get; private set; }
    public int Waiting { get; private set; }
    public IReadOnlyList<UsageRow> ByTool { get; private set; } = [];
    public IReadOnlyList<UsageRow> ByUser { get; private set; } = [];
    public IReadOnlyList<McpCallRecord> Recent { get; private set; } = [];
    public IReadOnlyDictionary<Guid, string> Names { get; private set; } = new Dictionary<Guid, string>();

    public async Task OnGetAsync()
    {
        (Today, Week, Errors, Waiting, ByTool, ByUser) = await admin.UsageAsync(HttpContext.RequestAborted);
        Recent = await admin.RecentCallsAsync(50, HttpContext.RequestAborted);
        var ids = Recent.Select(r => r.UserId).Distinct().ToList();
        Names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, HttpContext.RequestAborted);
    }
}
