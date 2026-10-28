using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core;
using Switchboard.Core.Agents;
using Switchboard.Core.Modules;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Catalog.Agents;
using Switchboard.Modules.Catalog.Services;
using Switchboard.Modules.Catalog.Web;

namespace Switchboard.Modules.Catalog;

public sealed class CatalogModule : ISwitchboardModule
{
    public string Name => "Catalog";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<CatalogService>();
        services.AddScoped<ICatalogService>(sp => sp.GetRequiredService<CatalogService>());
        services.AddScoped<ICatalogAdmin, CatalogAdmin>();
        services.AddScoped<ICatalogRequestService, CatalogRequestService>();
        services.AddEventHandler<ApprovalDecided, CatalogApprovalFollowUp>();
        services.AddHostedService<CatalogDemoSeeder>();

        services.AddPrivileges(new PrivilegeDefinition(CatalogPrivileges.Manage, "Manage the service catalog", "Service catalog",
            "Categories, items, request forms, audiences and approvals."));

        services.AddNavItem(new NavItem(NavArea.Agent, "catalog", "Service catalog", "bi-bag", "/catalog", Order: 30));
        services.AddNavItem(new NavItem(NavArea.Portal, "catalog", "Request catalog", "bi-bag", "/portal/catalog", Order: 20));
        services.AddNavItem(new NavItem(NavArea.Admin, "catalog-items", "Catalog items", "bi-bag", "/admin/catalog/items", Order: 60,
            Privilege: CatalogPrivileges.Manage, Group: "Service catalog", Description: "What people can request, and the forms they fill in."));
        services.AddNavItem(new NavItem(NavArea.Admin, "catalog-categories", "Categories", "bi-folder", "/admin/catalog/categories", Order: 70,
            Privilege: CatalogPrivileges.Manage, Group: "Service catalog"));
        services.AddNavItem(new NavItem(NavArea.Admin, "catalog-fields", "Shared fields", "bi-input-cursor-text", "/admin/catalog/fields", Order: 80,
            Privilege: CatalogPrivileges.Manage, Group: "Service catalog"));

        services.AddSlot<CatalogTicketItemsViewComponent>("ticket.sidebar", order: 30);
        services.AddSlot<CatalogRequesterItemsViewComponent>("portal.ticket", order: 30);
        services.AddSlot<CatalogApprovalSubjectViewComponent>("approval.subject", order: 10);
        services.AddSlot<CatalogNewMenuViewComponent>("topbar.new", order: 10);

        services.AddAgentTool<SearchCatalogTool>();
        services.AddAgentTool<GetCatalogItemTool>();
        services.AddAgentTool<SubmitRequestTool>();
    }

    public void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogModule).Assembly);
}
