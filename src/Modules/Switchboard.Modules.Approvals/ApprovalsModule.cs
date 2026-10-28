using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core;
using Switchboard.Core.Agents;
using Switchboard.Core.Data;
using Switchboard.Core.Modules;
using Switchboard.Modules.Approvals.Agents;
using Switchboard.Modules.Approvals.Data;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Approvals.Services;
using Switchboard.Modules.Approvals.Web;

namespace Switchboard.Modules.Approvals;

public sealed class ApprovalsModule : ISwitchboardModule
{
    public string Name => "Approvals";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ApproverResolver>();
        services.AddScoped<IApprovalDelegationService, ApprovalDelegationService>();
        services.AddScoped<IApprovalService, ApprovalService>();
        services.AddScoped<IApprovalSubjectPolicy, TicketApprovalSubjectPolicy>();
        services.AddScoped<ApprovalLinkTokens>();
        services.AddScoped<PendingApprovalTickets>();

        services.AddEventHandler<ApprovalRequested, ApprovalEmailNotifier>();
        services.AddEventHandler<ApprovalReminded, ApprovalEmailNotifier>();
        services.AddEventHandler<ApprovalDecided, ApprovalEmailNotifier>();
        services.AddHostedService<ApprovalReminderJob>();

        services.AddPrivileges(new PrivilegeDefinition(ApprovalPrivileges.Manage, "Manage approval settings", "Approvals",
            "Email approval links, self-approval and reminders."));

        services.AddNavItem(new NavItem(NavArea.Agent, "approvals", "Approvals", "bi-check2-square", "/approvals", Order: 25));
        services.AddNavItem(new NavItem(NavArea.Portal, "approvals", "Approvals", "bi-check2-square", "/portal/approvals", Order: 40));
        services.AddNavItem(new NavItem(NavArea.Admin, "approvals", "Approvals", "bi-check2-square", "/admin/approvals", Order: 90,
            Privilege: ApprovalPrivileges.Manage, Group: "Service catalog", Description: "Email links, self-approval and reminders."));

        services.AddSlot<TicketApprovalsViewComponent>("ticket.sidebar", order: 40);
        services.AddSlot<RequesterTicketApprovalsViewComponent>("portal.ticket", order: 40);
        services.AddSlot<TicketApprovalBadgeViewComponent>("ticket.list.badges", order: 40);
        services.AddSlot<ApprovalsTopbarViewComponent>("topbar.end", order: 10);
        services.AddSlot<PortalHomeApprovalsViewComponent>("portal.home", order: 10);
        services.AddSlot<PortalAttentionApprovalsViewComponent>("portal.home.attention", order: 10);
        services.AddSlot<DelegationProfileViewComponent>("profile.sections", order: 40);

        services.AddAgentTool<ListMyApprovalsTool>();
        services.AddAgentTool<GetApprovalTool>();
        services.AddAgentTool<DecideApprovalTool>();
    }

    public void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApprovalsModule).Assembly);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/approvals/people", PeopleSearch.HandleAsync);

    public async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        // INV-APR-1 at the database level: recorded decisions can't be updated or deleted by anything.
        var db = services.GetRequiredService<SwitchboardDbContext>();
        await db.Database.ExecuteSqlRawAsync($"""
            IF OBJECT_ID(N'dbo.{AprDecisionConfiguration.ImmutableTrigger}', N'TR') IS NULL
            EXEC(N'CREATE TRIGGER dbo.{AprDecisionConfiguration.ImmutableTrigger} ON dbo.AprDecisions AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                THROW 51000, ''Approval decisions are immutable (INV-APR-1).'', 1;
            END');
            """, cancellationToken);
    }
}
