using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Pages.Fields;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.FieldsManage)]
public sealed class IndexModel(FieldAdminService fields, SwitchboardDbContext db) : AdminPageModel
{
    [BindProperty(SupportsGet = true)]
    public string Entity { get; set; } = FieldEntities.Ticket;

    [BindProperty(SupportsGet = true)]
    public string? Ws { get; set; }

    public Workspace Workspace { get; private set; } = default!;
    public IReadOnlyList<Workspace> Workspaces { get; private set; } = [];
    public IReadOnlyList<FieldRow> Fields { get; private set; } = [];
    public bool Scoped => FieldEntities.IsWorkspaceScoped(Entity);

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostMoveAsync(Guid fieldId, int delta)
    {
        await fields.MoveAsync(fieldId, delta);
        return Redirect(Link("/admin/fields", ("entity", Entity), ("ws", Ws)) + "#field-" + fieldId);
    }

    private async Task LoadAsync()
    {
        Entity = FieldEntities.IsKnown(Entity) ? Entity : FieldEntities.Ticket;
        (Workspace, Workspaces) = await ResolveWorkspaceAsync(db, Ws);
        Ws = Scoped ? Workspace.Key : null;
        Fields = await fields.ListAsync(Entity, Scoped ? Workspace.Id : null);
    }

    public static string TypeTitle(CustomFieldType type) => FieldAdminService.Types.FirstOrDefault(t => t.Type == type).Title ?? type.ToString();
}
