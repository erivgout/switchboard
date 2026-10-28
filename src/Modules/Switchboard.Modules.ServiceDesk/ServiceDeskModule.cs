using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.ServiceDesk.Data;
using Switchboard.Modules.ServiceDesk.Services;
using Switchboard.Modules.ServiceDesk.Slots;

namespace Switchboard.Modules.ServiceDesk;

/// <summary>The agent ticket workspace: queue, ticket page, new ticket, canned responses, templates and search.</summary>
public sealed class ServiceDeskModule : ISwitchboardModule
{
    /// <summary>Promote an incident to a major incident, or demote it (FR-INC-3).</summary>
    public const string MajorIncidentPrivilege = "incidents.major";

    private const string SeededMajorPrivilegeKey = "servicedesk.seeded.major_privilege";
    private const string SeededStarterContentKey = "servicedesk.seeded.starter_content";

    public string Name => "ServiceDesk";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<SavedViewService>();
        services.AddScoped<CannedResponseService>();
        services.AddScoped<TicketTemplateService>();
        services.AddScoped<BulkActionService>();
        services.AddScoped<TicketFormOptions>();
        services.AddScoped<TicketPageLoader>();
        services.AddScoped<AgentSearch>();
        services.AddHostedService<BulkJobRunner>();

        services.AddNavItem(new NavItem(NavArea.Agent, "tickets", "Tickets", "bi-inbox", "/tickets", Order: 20));
        services.AddNavItem(new NavItem(NavArea.Admin, "ticket-templates", "Ticket templates", "bi-file-earmark-text", "/admin/ticket-templates",
            Order: 30, Privilege: Privileges.SettingsManage, Group: "Service desk", Description: "Pre-filled forms agents start new tickets from."));
        services.AddPrivileges(new PrivilegeDefinition(MajorIncidentPrivilege, "Promote and demote major incidents", "Tickets"));
        services.AddSlot<CannedResponsesMenuItem>("usermenu.items", order: 20);
    }

    public void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ServiceDeskModule).Assembly);

    public async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<SwitchboardDbContext>();
        var settings = services.GetRequiredService<ISettingsService>();

        // Supervisors can run major incidents by default. Seeded once, so an admin can take it away again.
        if (!await settings.GetAsync(SeededMajorPrivilegeKey, false, ct: cancellationToken))
        {
            var supervisor = await db.Roles.FirstOrDefaultAsync(r => r.Key == RoleKeys.Supervisor, cancellationToken);
            if (supervisor is not null && !supervisor.Privileges.Contains(MajorIncidentPrivilege))
            {
                supervisor.Privileges = [.. supervisor.Privileges, MajorIncidentPrivilege];
            }
            await settings.SetAsync(SeededMajorPrivilegeKey, true, ct: cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var options = services.GetService<IOptions<SwitchboardOptions>>()?.Value;
        if (options?.DemoData == true && !await settings.GetAsync(SeededStarterContentKey, false, ct: cancellationToken))
        {
            await SeedStarterContentAsync(db, cancellationToken);
            await settings.SetAsync(SeededStarterContentKey, true, ct: cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task SeedStarterContentAsync(SwitchboardDbContext db, CancellationToken ct)
    {
        if (!await db.Set<SdCannedResponseFolder>().AnyAsync(ct))
        {
            var folder = new SdCannedResponseFolder { Name = "Common replies", Visibility = CannedVisibility.Shared };
            db.Set<SdCannedResponseFolder>().Add(folder);
            db.Set<SdCannedResponse>().AddRange(
                new SdCannedResponse
                {
                    FolderId = folder.Id,
                    Title = "We're on it",
                    BodyHtml = "<p>Hi {{requester.first_name}},</p><p>Thanks for letting us know. I'm looking into {{ticket.id}} now and will update you here as soon as I know more.</p><p>{{agent.first_name}}</p>",
                },
                new SdCannedResponse
                {
                    FolderId = folder.Id,
                    Title = "Did that fix it?",
                    BodyHtml = "<p>Hi {{requester.first_name}},</p><p>I've made the change described above. Could you check whether it works for you now? If I don't hear back in three days I'll mark this as resolved.</p><p>{{agent.first_name}}</p>",
                },
                new SdCannedResponse
                {
                    FolderId = folder.Id,
                    Title = "Restart and reconnect",
                    BodyHtml = "<p>Hi {{requester.first_name}},</p><p>Please try these steps:</p><ol><li>Save your work and restart your computer.</li><li>Reconnect to the VPN.</li><li>Try again and reply here with what you see.</li></ol>",
                });
        }

        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.IsPrimary, ct);
        if (workspace is not null && !await db.Set<SdTicketTemplate>().AnyAsync(ct))
        {
            var categories = await db.TicketCategories.AsNoTracking().Where(c => c.WorkspaceId == workspace.Id).ToListAsync(ct);
            var groups = await db.AgentGroups.AsNoTracking().Where(g => g.WorkspaceId == workspace.Id).ToListAsync(ct);
            Guid? Cat(string name, Guid? parent = null) => categories.FirstOrDefault(c => c.Name == name && c.ParentId == parent)?.Id;
            Guid? Group(string name) => groups.FirstOrDefault(g => g.Name == name)?.Id;

            var access = Cat("Access");
            var hardware = Cat("Hardware");
            db.Set<SdTicketTemplate>().AddRange(
                new SdTicketTemplate
                {
                    WorkspaceId = workspace.Id,
                    Name = "Password reset",
                    Description = "Caller can't sign in and needs a new password.",
                    Type = TicketType.Incident,
                    Subject = "Password reset for ",
                    DescriptionHtml = "<p>Identity confirmed by: </p><p>Systems affected: </p>",
                    CategoryId = access,
                    SubCategoryId = access is null ? null : Cat("Password", access),
                    Urgency = Level.Medium,
                    Impact = Level.Low,
                    GroupId = Group("Service Desk"),
                    Tags = ["password"],
                },
                new SdTicketTemplate
                {
                    WorkspaceId = workspace.Id,
                    Name = "Replacement laptop",
                    Description = "A broken or failing laptop that needs swapping.",
                    Type = TicketType.ServiceRequest,
                    Subject = "Replacement laptop for ",
                    DescriptionHtml = "<p>Current asset tag: </p><p>What's wrong with it: </p><p>Needed by: </p>",
                    CategoryId = hardware,
                    SubCategoryId = hardware is null ? null : Cat("Laptop", hardware),
                    Priority = Priority.Medium,
                    GroupId = Group("Hardware"),
                    Tags = ["hardware-swap"],
                });
        }
    }
}
