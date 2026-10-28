using Switchboard.Core.Modules;

namespace Switchboard.Core.Security;

/// <summary>
/// Core privilege keys. Modules add their own through <see cref="ModuleServiceCollectionExtensions.AddPrivileges"/>;
/// keep the <c>area.verb</c> naming.
/// </summary>
public static class Privileges
{
    public const string TicketsView = "tickets.view";
    public const string TicketsCreate = "tickets.create";
    public const string TicketsEdit = "tickets.edit";
    /// <summary>Send replies, forwards and public notes (messages requesters or third parties see).</summary>
    public const string TicketsReply = "tickets.reply";
    public const string TicketsNote = "tickets.note";
    public const string TicketsAssign = "tickets.assign";
    public const string TicketsMerge = "tickets.merge";
    public const string TicketsDelete = "tickets.delete";
    public const string TicketsDeletePermanently = "tickets.delete_permanently";
    public const string TicketsBulk = "tickets.bulk";
    public const string TicketsExport = "tickets.export";

    public const string UsersView = "users.view";
    public const string UsersManage = "users.manage";
    public const string GroupsManage = "groups.manage";
    public const string DepartmentsManage = "departments.manage";
    public const string LocationsManage = "locations.manage";
    public const string RolesManage = "roles.manage";

    public const string WorkspaceAdmin = "workspace.admin";
    public const string SettingsManage = "settings.manage";
    public const string FieldsManage = "fields.manage";
    public const string AuditView = "audit.view";

    public static IReadOnlyList<PrivilegeDefinition> CoreDefinitions { get; } =
    [
        new(TicketsView, "View tickets", "Tickets"),
        new(TicketsCreate, "Create tickets", "Tickets"),
        new(TicketsEdit, "Edit ticket properties", "Tickets"),
        new(TicketsReply, "Send replies, forwards and public notes", "Tickets"),
        new(TicketsNote, "Add private notes", "Tickets"),
        new(TicketsAssign, "Assign tickets", "Tickets"),
        new(TicketsMerge, "Merge tickets", "Tickets"),
        new(TicketsDelete, "Delete tickets to trash, mark spam, restore", "Tickets"),
        new(TicketsDeletePermanently, "Permanently delete tickets", "Tickets"),
        new(TicketsBulk, "Run bulk actions", "Tickets"),
        new(TicketsExport, "Export ticket lists", "Tickets"),
        new(UsersView, "View people directory", "Users and access"),
        new(UsersManage, "Manage requesters and agents", "Users and access"),
        new(GroupsManage, "Manage agent and requester groups", "Users and access"),
        new(DepartmentsManage, "Manage departments", "Users and access"),
        new(LocationsManage, "Manage locations", "Users and access"),
        new(RolesManage, "Manage roles", "Users and access"),
        new(WorkspaceAdmin, "Administer the workspace", "Administration"),
        new(SettingsManage, "Manage service desk settings", "Administration"),
        new(FieldsManage, "Manage fields and forms", "Administration"),
        new(AuditView, "View the audit log", "Administration"),
    ];
}

/// <summary>Built-in role keys seeded at setup (Appendix C).</summary>
public static class RoleKeys
{
    public const string WorkspaceAdmin = "workspace_admin";
    public const string Supervisor = "sd_supervisor";
    public const string Agent = "sd_agent";
    public const string ReadOnly = "read_only";
}
