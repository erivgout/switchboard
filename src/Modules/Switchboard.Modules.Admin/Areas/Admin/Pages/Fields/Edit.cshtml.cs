using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Fields;

/// <summary><c>/admin/fields/new?entity=&amp;ws=</c> or <c>/admin/fields/{id}</c>.</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.FieldsManage)]
public sealed class EditModel(FieldAdminService fields, SwitchboardDbContext db) : AdminPageModel
{
    public sealed class FieldForm
    {
        public string Label { get; set; } = "";
        public string? Key { get; set; }
        public CustomFieldType Type { get; set; } = CustomFieldType.Text;
        public string? RequesterLabel { get; set; }
        public string? Options { get; set; }
        public string? LookupTarget { get; set; }
        public bool RequiredForAgents { get; set; }
        public bool RequiredOnResolve { get; set; }
        public bool RequiredForRequesters { get; set; }
        public bool VisibleToRequesters { get; set; }
        public bool EditableByRequesters { get; set; }
        public string? HelpText { get; set; }
        public string? DefaultValue { get; set; }
        public string? Section { get; set; }

        public FieldInput ToInput() => new()
        {
            Label = Label, Key = Key, Type = Type, RequesterLabel = RequesterLabel, Options = Options, LookupTarget = LookupTarget,
            RequiredForAgents = RequiredForAgents, RequiredOnResolve = RequiredOnResolve, RequiredForRequesters = RequiredForRequesters,
            VisibleToRequesters = VisibleToRequesters, EditableByRequesters = EditableByRequesters, HelpText = HelpText,
            DefaultValue = DefaultValue, Section = Section,
        };
    }

    [BindProperty]
    public FieldForm Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string Entity { get; set; } = FieldEntities.Ticket;

    [BindProperty(SupportsGet = true)]
    public string? Ws { get; set; }

    public CustomFieldDefinition? Field { get; private set; }
    public bool HasData { get; private set; }
    public bool IsNew => Field is null;
    public Workspace? Workspace { get; private set; }
    public IReadOnlyList<string> Sections { get; private set; } = [];
    public string EntityTitle => FieldEntities.All.First(e => e.Key == Entity).Title;
    public string ListHref => Link("/admin/fields", ("entity", Entity), ("ws", FieldEntities.IsWorkspaceScoped(Entity) ? Workspace?.Key : null));

    public async Task<IActionResult> OnGetAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }
        if (Field is { } f)
        {
            Input = new FieldForm
            {
                Label = f.Label, Key = f.Key, Type = f.FieldType, RequesterLabel = f.RequesterLabel, Options = FieldAdminService.OptionsText(f),
                LookupTarget = FieldAdminService.LookupTarget(f), RequiredForAgents = f.RequiredForAgents, RequiredOnResolve = f.RequiredOnResolve,
                RequiredForRequesters = f.RequiredForRequesters, VisibleToRequesters = f.VisibleToRequesters, EditableByRequesters = f.EditableByRequesters,
                HelpText = f.HelpText, DefaultValue = f.DefaultValue, Section = f.Section,
            };
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }
        CustomFieldDefinition? saved = Field;
        if (await TryAsync(async () =>
            {
                if (Field is null)
                {
                    saved = await fields.CreateAsync(Entity, Workspace?.Id, Input.ToInput());
                }
                else
                {
                    await fields.UpdateAsync(Field.Id, Input.ToInput());
                }
            }))
        {
            TempData.Toast(Field is null ? "Field created" : "Field saved");
            return Redirect(ListHref + "#field-" + saved!.Id);
        }
        if (Field is not null)
        {
            Input.Type = Field.FieldType;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostActiveAsync(string id, bool active)
    {
        if (!await LoadAsync(id) || Field is null)
        {
            return NotFound();
        }
        await fields.SetActiveAsync(Field.Id, active);
        TempData.Toast(active ? "Field activated" : "Field deactivated");
        return Redirect($"/admin/fields/{Field.Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        if (!await LoadAsync(id) || Field is null)
        {
            return NotFound();
        }
        if (await TryAsync(() => fields.DeleteAsync(Field.Id), ""))
        {
            TempData.Toast("Field deleted");
            return Redirect(ListHref);
        }
        return await OnGetAsync(id);
    }

    private async Task<bool> LoadAsync(string id)
    {
        if (id != "new")
        {
            if (!Guid.TryParse(id, out var guid))
            {
                return false;
            }
            var row = await fields.GetAsync(guid);
            Field = row.Field;
            HasData = row.HasData;
            Entity = Field.EntityType;
            Workspace = Field.WorkspaceId is { } ws ? await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == ws) : null;
        }
        else
        {
            if (!FieldEntities.IsKnown(Entity))
            {
                return false;
            }
            Workspace = FieldEntities.IsWorkspaceScoped(Entity) ? (await ResolveWorkspaceAsync(db, Ws)).Current : null;
        }
        Sections = await db.CustomFieldDefinitions.AsNoTracking()
            .Where(f => f.EntityType == Entity && f.WorkspaceId == (Workspace == null ? null : Workspace.Id) && f.Section != null)
            .Select(f => f.Section!).Distinct().OrderBy(s => s).ToListAsync();
        return true;
    }
}
