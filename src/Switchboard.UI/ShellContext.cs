using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;

namespace Switchboard.UI;

public sealed record ShellUser(Guid Id, string Name, string? Email, bool IsAgent, bool IsOrgAdmin);

/// <summary>Everything the layouts need, computed once per request.</summary>
public sealed class ShellModel
{
    public string OrganizationName { get; init; } = "Switchboard";
    public string? LogoUrl { get; init; }
    public ShellUser? User { get; init; }
    public IReadOnlyList<NavItem> AgentNav { get; init; } = [];
    public IReadOnlyList<NavItem> PortalNav { get; init; } = [];
    public IReadOnlyList<IGrouping<string, NavItem>> AdminNav { get; init; } = [];
    public bool HasAdmin => AdminNav.Count > 0;
    /// <summary>Lit lamps on the user's lines: waiting tickets assigned to them or unassigned in their groups.</summary>
    public int WaitingCount { get; init; }
}

public interface IShellContext
{
    Task<ShellModel> GetAsync(CancellationToken ct = default);
}

public sealed class ShellContext(
    SwitchboardDbContext db,
    IActorAccessor actors,
    IPermissionService permissions,
    IServiceProvider services) : IShellContext
{
    /// <summary>Query behind the "N waiting" counter in the top bar.</summary>
    public const string WaitingQuery = "is:lit (agent:me OR (agent:none group:mine))";

    private ShellModel? _model;

    /// <summary>Admin menu groups in outcome order; unknown groups follow alphabetically.</summary>
    private static readonly string[] AdminGroupOrder =
    [
        "Organization", "Users and access", "Service desk", "Service catalog", "Service levels",
        "Channels", "Automation", "Integrations", "AI and agents", "Data and security",
    ];

    private static int AdminGroupRank(string group) =>
        Array.IndexOf(AdminGroupOrder, group) is var i and >= 0 ? i : AdminGroupOrder.Length;

    public async Task<ShellModel> GetAsync(CancellationToken ct = default)
    {
        if (_model is not null)
        {
            return _model;
        }

        var org = await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).Select(o => new { o.Name, o.LogoUrl }).FirstOrDefaultAsync(ct);
        var actor = actors.Current;
        var perms = await permissions.CurrentAsync(ct);
        var nav = services.GetServices<NavItem>().ToList();

        ShellUser? user = null;
        if (actor.UserId is { } uid)
        {
            user = await db.Users.AsNoTracking().Where(u => u.Id == uid)
                .Select(u => new ShellUser(u.Id, u.DisplayName, u.Email, u.IsAgent || u.IsOrgAdmin, u.IsOrgAdmin))
                .FirstOrDefaultAsync(ct);
        }

        bool Visible(NavItem item) =>
            perms is not null && (item.Privilege switch
            {
                null => item.Area != NavArea.Portal ? perms.IsAgent : !item.AgentsOnly || perms.IsAgent,
                // Org-admin-only items: workspace admins hold every privilege but still get 403 on these.
                SwitchboardPolicies.OrgAdmin => perms.IsOrgAdmin,
                _ => perms.Has(item.Privilege),
            });

        var waiting = 0;
        if (perms?.IsAgent == true)
        {
            try
            {
                waiting = await services.GetRequiredService<ITicketSearch>().CountAsync(WaitingQuery, ct);
            }
            catch (ValidationException)
            {
                waiting = 0;
            }
        }

        _model = new ShellModel
        {
            OrganizationName = org?.Name ?? "Switchboard",
            LogoUrl = org?.LogoUrl,
            User = user,
            AgentNav = nav.Where(n => n.Area == NavArea.Agent && Visible(n)).OrderBy(n => n.Order).ThenBy(n => n.Title).ToList(),
            PortalNav = nav.Where(n => n.Area == NavArea.Portal && (n.Privilege is null ? !n.AgentsOnly || perms?.IsAgent == true : Visible(n))).OrderBy(n => n.Order).ToList(),
            AdminNav = nav.Where(n => n.Area == NavArea.Admin && Visible(n))
                .OrderBy(n => n.Order)
                .GroupBy(n => n.Group ?? "General")
                .OrderBy(g => AdminGroupRank(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList(),
            WaitingCount = waiting,
        };
        return _model;
    }
}
