using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Departments;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.DepartmentsManage)]
public sealed class EditModel(OrgStructureService org, PeopleService people, ICustomFieldService fields, SwitchboardDbContext db) : AdminPageModel
{
    public sealed class DepartmentForm
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public Guid? HeadUserId { get; set; }
        public Guid? PrimeUserId { get; set; }
        public string? Domains { get; set; }
    }

    [BindProperty]
    public DepartmentForm Input { get; set; } = new();

    /// <summary>A department id, or <c>none</c>.</summary>
    [BindProperty]
    public string? Replacement { get; set; }

    public Department? Department { get; private set; }
    public bool IsNew => Department is null;
    public int People { get; private set; }
    public int Tickets { get; private set; }
    public PersonOption? Head { get; private set; }
    public PersonOption? Prime { get; private set; }
    public CustomFieldsModel Custom { get; private set; } = default!;
    public List<SelectListItem> Replacements { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }
        if (Department is not null)
        {
            Input = new DepartmentForm
            {
                Name = Department.Name,
                Description = Department.Description,
                HeadUserId = Department.HeadUserId,
                PrimeUserId = Department.PrimeUserId,
                Domains = string.Join('\n', Department.Domains),
            };
            await LoadAsync(id);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }
        var definitions = await fields.GetDefinitionsAsync(FieldEntities.Department, null);
        var custom = CustomFieldForm.Read(Request.Form, definitions);
        var input = new DepartmentInput(Input.Name, Input.Description, Input.HeadUserId, Input.PrimeUserId,
            (Input.Domains ?? "").Split(['\n', ',', ' ', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), custom);
        Department? saved = null;
        if (await TryAsync(async () => saved = await org.SaveDepartmentAsync(Department?.Id, input)))
        {
            TempData.Toast(Department is null ? "Department created" : "Department saved");
            return Redirect($"/admin/departments/{saved!.Id}");
        }
        Custom = await CustomFieldsModel.LoadAsync(db, definitions, CustomFieldForm.ToJson(custom), false);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        if (!await LoadAsync(id) || Department is null)
        {
            return NotFound();
        }
        if (TryReadReplacement(Replacement, People + Tickets > 0, "Choose where the department's people and tickets go.", out var replacement)
            && await TryAsync(() => org.DeleteDepartmentAsync(Department.Id, replacement), ""))
        {
            TempData.Toast("Department deleted");
            return Redirect("/admin/departments");
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
            (Department, People, Tickets) = await org.GetDepartmentAsync(guid);
            Replacements = (await AdminSelects.DepartmentsAsync(db, null, "Choose where they go")).Where(d => d.Value != guid.ToString())
                .Append(new SelectListItem("Leave them without a department", "none")).ToList();
            Replacements.ForEach(r => r.Selected = r.Value == (Replacement ?? ""));
        }
        Head = await people.OptionAsync(Input.HeadUserId);
        Prime = await people.OptionAsync(Input.PrimeUserId);
        Custom = await CustomFieldsModel.LoadAsync(db, await fields.GetDefinitionsAsync(FieldEntities.Department, null), Department?.CustomFieldsJson ?? "{}", false);
        return true;
    }
}
