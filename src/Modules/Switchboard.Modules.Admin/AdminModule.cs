using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin;

/// <summary>Admin nav group names (FR-ADM-1). Other modules add their own groups.</summary>
public static class AdminGroups
{
    public const string Organization = "Organization";
    public const string UsersAndAccess = "Users and access";
    public const string ServiceDesk = "Service desk";
    public const string DataAndSecurity = "Data and security";

    /// <summary>Outcome order of the admin console (FR-ADM-1); groups not listed sort after these.</summary>
    private static readonly string[] Order =
    [
        Organization, UsersAndAccess, "Channels", ServiceDesk, "Service management", "Assets", "Operations",
        "Automation", "Knowledge", "Portals", "AI", "MCP", "Integrations and plugins", DataAndSecurity, "System",
    ];

    public static int Rank(string group) => Array.IndexOf(Order, group) is var i and >= 0 ? i : int.MaxValue;
}

/// <summary>
/// Nav privilege for pages only organization admins may open. No role can hold it (it is not a registered
/// privilege), so only <see cref="Core.Security.UserPermissions.IsOrgAdmin"/> satisfies it.
/// </summary>
public static class AdminNavPrivileges
{
    public const string OrgAdminOnly = SwitchboardPolicies.OrgAdmin;
}

public sealed class AdminModule : ISwitchboardModule
{
    public string Name => "Admin";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<AdminAccess>();
        services.AddScoped<PeopleService>();
        services.AddScoped<PersonLifecycleService>();
        services.AddScoped<InvitationService>();
        services.AddScoped<RoleService>();
        services.AddScoped<GroupService>();
        services.AddScoped<OrgStructureService>();
        services.AddScoped<TicketConfigService>();
        services.AddScoped<FieldAdminService>();
        services.AddScoped<AdminSettingsService>();
        services.AddScoped<AuditBrowser>();
        services.AddScoped<RequesterImportService>();
        services.AddScoped<IAttachmentAccessPolicy, LogoAttachmentPolicy>();

        void Nav(string key, string title, string icon, string href, int order, string privilege, string group, string description) =>
            services.AddNavItem(new NavItem(NavArea.Admin, key, title, icon, href, order, privilege, group, AgentsOnly: true, Description: description));

        Nav("admin.organization", "Organization", "bi-building", "/admin/organization", 110, AdminNavPrivileges.OrgAdminOnly, AdminGroups.Organization,
            "Name, logo, time zone, date format and currency.");
        Nav("admin.features", "Features", "bi-toggles", "/admin/features", 120, AdminNavPrivileges.OrgAdminOnly, AdminGroups.Organization,
            "Turn modules on or off for everyone or for one workspace.");

        Nav("admin.people", "People", "bi-people", "/admin/people", 210, Privileges.UsersView, AdminGroups.UsersAndAccess,
            "Agents and requesters: profiles, access, deactivation and data requests.");
        Nav("admin.roles", "Roles", "bi-shield-check", "/admin/roles", 220, Privileges.RolesManage, AdminGroups.UsersAndAccess,
            "What each agent role is allowed to see and do.");
        Nav("admin.groups", "Agent groups", "bi-diagram-3", "/admin/groups", 230, Privileges.GroupsManage, AdminGroups.UsersAndAccess,
            "Teams that share a queue, and how new work is assigned to them.");
        Nav("admin.requester-groups", "Requester groups", "bi-person-lines-fill", "/admin/requester-groups", 240, Privileges.GroupsManage, AdminGroups.UsersAndAccess,
            "Sets of requesters used for visibility, approvals and notifications.");
        Nav("admin.departments", "Departments", "bi-briefcase", "/admin/departments", 250, Privileges.DepartmentsManage, AdminGroups.UsersAndAccess,
            "Department heads, prime contacts and the email domains that place people in them.");
        Nav("admin.locations", "Locations", "bi-geo-alt", "/admin/locations", 260, Privileges.LocationsManage, AdminGroups.UsersAndAccess,
            "Offices and sites, nested by region, with addresses and contacts.");
        Nav("admin.security", "Sign-in and sign-up", "bi-door-open", "/admin/security", 270, AdminNavPrivileges.OrgAdminOnly, AdminGroups.UsersAndAccess,
            "Who can create a requester account, session timeouts and password rules.");

        Nav("admin.service-desk", "Service desk settings", "bi-sliders2", "/admin/service-desk", 410, Privileges.SettingsManage, AdminGroups.ServiceDesk,
            "Auto-close, reopening, what requesters may do, and the priority matrix.");
        Nav("admin.statuses", "Ticket statuses", "bi-flag", "/admin/statuses", 420, Privileges.FieldsManage, AdminGroups.ServiceDesk,
            "The stages a ticket moves through, and what requesters see for each.");
        Nav("admin.categories", "Categories", "bi-folder2", "/admin/categories", 430, Privileges.FieldsManage, AdminGroups.ServiceDesk,
            "Category, sub-category and item choices for tickets.");
        Nav("admin.sources", "Ticket sources", "bi-box-arrow-in-down", "/admin/sources", 440, Privileges.FieldsManage, AdminGroups.ServiceDesk,
            "Where tickets come from: email, portal, phone and your own sources.");
        Nav("admin.fields", "Custom fields", "bi-input-cursor-text", "/admin/fields", 450, Privileges.FieldsManage, AdminGroups.ServiceDesk,
            "Extra fields on tickets, people and departments.");

        Nav("admin.audit", "Audit log", "bi-journal-check", "/admin/audit", 1410, Privileges.AuditView, AdminGroups.DataAndSecurity,
            "Every security and configuration change, with who made it and when.");
        Nav("admin.import", "Import requesters", "bi-file-earmark-arrow-up", "/admin/import", 1420, Privileges.UsersManage, AdminGroups.DataAndSecurity,
            "Add or update requesters from a CSV file, with a preview before anything changes.");
    }

    public void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AdminModule).Assembly);
}
