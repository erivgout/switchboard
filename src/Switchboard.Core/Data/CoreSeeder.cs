using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;

namespace Switchboard.Core.Data;

/// <summary>Idempotent defaults every instance needs (Appendix C).</summary>
public static class CoreSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<SwitchboardDbContext>();
        var time = services.GetRequiredService<TimeProvider>();
        var now = time.GetUtcNow();

        if (!await db.Organizations.AnyAsync(ct))
        {
            db.Organizations.Add(new Organization { Name = "Switchboard", CreatedAt = now, UpdatedAt = now });
        }

        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.IsPrimary, ct);
        if (workspace is null)
        {
            workspace = new Workspace { Key = "it", Name = "IT", Icon = "bi-pc-display", Template = "it", IsPrimary = true, CreatedAt = now };
            db.Workspaces.Add(workspace);
        }

        if (!await db.TicketStatuses.AnyAsync(s => s.WorkspaceId == workspace.Id, ct))
        {
            db.TicketStatuses.AddRange(
                new TicketStatus { WorkspaceId = workspace.Id, Name = "Open", RequesterLabel = "Being worked on", Category = StatusCategory.Open, SlaTimerOn = true, Order = 1, IsSystem = true, IsDefault = true },
                new TicketStatus { WorkspaceId = workspace.Id, Name = "Pending", RequesterLabel = "Waiting on you", Category = StatusCategory.Pending, SlaTimerOn = false, Order = 2, IsSystem = true },
                new TicketStatus { WorkspaceId = workspace.Id, Name = "Resolved", RequesterLabel = "Resolved", Category = StatusCategory.Resolved, SlaTimerOn = false, Order = 3, IsSystem = true },
                new TicketStatus { WorkspaceId = workspace.Id, Name = "Closed", RequesterLabel = "Closed", Category = StatusCategory.Closed, SlaTimerOn = false, Order = 4, IsSystem = true });
        }

        var sources = new (string Key, string Name, string Icon)[]
        {
            (TicketSourceKeys.Email, "Email", "bi-envelope"),
            (TicketSourceKeys.Portal, "Portal", "bi-window"),
            (TicketSourceKeys.Phone, "Phone", "bi-telephone"),
            (TicketSourceKeys.Chat, "Chat", "bi-chat-dots"),
            (TicketSourceKeys.Widget, "Widget", "bi-window-sidebar"),
            (TicketSourceKeys.WalkUp, "Walk-up", "bi-person-walking"),
            (TicketSourceKeys.Slack, "Slack", "bi-slack"),
            (TicketSourceKeys.Teams, "Teams", "bi-microsoft-teams"),
            (TicketSourceKeys.Alert, "Alert", "bi-activity"),
            (TicketSourceKeys.Api, "API", "bi-code-slash"),
            (TicketSourceKeys.Mcp, "AI agent (MCP)", "bi-robot"),
            (TicketSourceKeys.Onboarding, "Onboarding", "bi-person-plus"),
            (TicketSourceKeys.Offboarding, "Offboarding", "bi-person-dash"),
            (TicketSourceKeys.Journey, "Journey", "bi-signpost-split"),
            (TicketSourceKeys.Agent, "Agent console", "bi-headset"),
            (TicketSourceKeys.Scheduler, "Scheduled", "bi-calendar-event"),
        };
        var existingSources = await db.TicketSources.Select(s => s.Key).ToListAsync(ct);
        var order = 0;
        foreach (var (key, name, icon) in sources)
        {
            order++;
            if (!existingSources.Contains(key))
            {
                db.TicketSources.Add(new TicketSource { Key = key, Name = name, Icon = icon, IsSystem = true, Order = order });
            }
        }

        await SeedRolesAsync(db, services, ct);

        if (!await db.TicketCategories.AnyAsync(c => c.WorkspaceId == workspace.Id, ct))
        {
            var tree = new (string Name, string[] Children)[]
            {
                ("Hardware", ["Laptop", "Desktop", "Monitor", "Printer", "Phone"]),
                ("Software", ["Installation", "License", "Bug", "Update"]),
                ("Network", ["Wi-Fi", "VPN", "Internet", "Firewall"]),
                ("Access", ["Account", "Password", "Permissions", "MFA"]),
                ("Email and collaboration", ["Email", "Calendar", "Video calls", "Shared drives"]),
            };
            var o = 0;
            foreach (var (name, children) in tree)
            {
                var parent = new TicketCategory { WorkspaceId = workspace.Id, Name = name, Level = 1, Order = ++o };
                db.TicketCategories.Add(parent);
                var co = 0;
                foreach (var child in children)
                {
                    db.TicketCategories.Add(new TicketCategory { WorkspaceId = workspace.Id, ParentId = parent.Id, Name = child, Level = 2, Order = ++co });
                }
            }
        }

        if (!await db.AgentGroups.AnyAsync(g => g.WorkspaceId == workspace.Id, ct))
        {
            db.AgentGroups.AddRange(
                new AgentGroup { WorkspaceId = workspace.Id, Name = "Service Desk", Description = "First line support for every request.", AssignmentMode = AssignmentMode.RoundRobin },
                new AgentGroup { WorkspaceId = workspace.Id, Name = "Network", Description = "Connectivity, VPN, Wi-Fi and firewalls." },
                new AgentGroup { WorkspaceId = workspace.Id, Name = "Hardware", Description = "Devices, peripherals and repairs." });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedRolesAsync(SwitchboardDbContext db, IServiceProvider services, CancellationToken ct)
    {
        var all = services.GetServices<Modules.PrivilegeDefinition>().Select(p => p.Key).Distinct().ToList();
        var agentPrivileges = new List<string>
        {
            Privileges.TicketsView, Privileges.TicketsCreate, Privileges.TicketsEdit, Privileges.TicketsReply,
            Privileges.TicketsNote, Privileges.TicketsAssign, Privileges.UsersView,
        };
        // Module privileges ending in .view/.use are granted to agents by default. Security-sensitive
        // view privileges are never granted automatically (the audit log shows IP addresses).
        var neverAutomatic = new HashSet<string>(StringComparer.Ordinal) { Privileges.AuditView };
        agentPrivileges.AddRange(all.Where(p => !p.StartsWith("tickets.", StringComparison.Ordinal) && !neverAutomatic.Contains(p)
            && (p.EndsWith(".view", StringComparison.Ordinal) || p.EndsWith(".use", StringComparison.Ordinal))));
        var supervisorPrivileges = agentPrivileges.Concat([
            Privileges.TicketsMerge, Privileges.TicketsDelete, Privileges.TicketsBulk, Privileges.TicketsExport,
        ]).Distinct().ToList();

        var roles = new (string Key, string Name, string Description, List<string> Privileges)[]
        {
            (RoleKeys.WorkspaceAdmin, "Workspace admin", "Full control of the workspace, including its configuration.", [Privileges.WorkspaceAdmin]),
            (RoleKeys.Supervisor, "Service desk supervisor", "Works and oversees the queue: merge, delete, bulk actions, exports.", supervisorPrivileges),
            (RoleKeys.Agent, "Service desk agent", "Works tickets: reply, note, assign and update.", agentPrivileges.Distinct().ToList()),
            (RoleKeys.ReadOnly, "Read-only agent", "Can see tickets and people but not change them.", [Privileges.TicketsView, Privileges.UsersView]),
        };

        // Privileges already offered to the built-in roles. A privilege is granted automatically only the
        // first time it appears, so an admin who later removes it from a role isn't overridden on restart.
        const string ledgerKey = "roles.offered_privileges";
        var ledgerRow = await db.Settings.FirstOrDefaultAsync(s => s.Key == ledgerKey && s.WorkspaceId == null, ct);
        var offered = ledgerRow is null
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(ledgerRow.ValueJson, JsonDefaults.Options) ?? [];

        foreach (var (key, name, description, privileges) in roles)
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Key == key, ct);
            if (role is null)
            {
                db.Roles.Add(new Role { Key = key, Name = name, Description = description, IsSystem = true, Privileges = privileges });
            }
            else if (role.IsSystem)
            {
                role.Privileges = role.Privileges.Union(privileges.Where(p => !offered.Contains(p))).ToList();
            }
        }

        offered.UnionWith(roles.SelectMany(r => r.Privileges));
        ledgerRow ??= db.Settings.Add(new Setting { Key = ledgerKey }).Entity;
        ledgerRow.ValueJson = System.Text.Json.JsonSerializer.Serialize(offered.Order(StringComparer.Ordinal), JsonDefaults.Options);
        ledgerRow.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Creates a user with a password. Used by setup and demo data.</summary>
    public static async Task<User> CreateUserAsync(SwitchboardDbContext db, IPasswordHasher<User> hasher, string email, string name,
        string? password, bool isAgent, bool isOrgAdmin, TimeProvider time, CancellationToken ct)
    {
        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
        if (user is not null)
        {
            return user;
        }
        user = new User
        {
            Email = email,
            NormalizedEmail = normalized,
            UserName = email,
            NormalizedUserName = normalized,
            EmailConfirmed = true,
            DisplayName = name,
            IsAgent = isAgent || isOrgAdmin,
            IsOrgAdmin = isOrgAdmin,
            Availability = isAgent ? AgentAvailability.Available : AgentAvailability.Offline,
            CreatedAt = time.GetUtcNow(),
        };
        if (password is not null)
        {
            user.PasswordHash = hasher.HashPassword(user, password);
        }
        db.Users.Add(user);
        return user;
    }
}
