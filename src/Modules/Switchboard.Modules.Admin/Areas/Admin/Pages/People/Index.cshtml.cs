using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Pages.People;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.UsersView)]
public sealed class IndexModel(PeopleService people, AdminAccess access, SwitchboardDbContext db) : AdminPageModel
{
    [BindProperty(SupportsGet = true)]
    public string Kind { get; set; } = "agents";

    [BindProperty(SupportsGet = true)]
    public string Status { get; set; } = "active";

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? Department { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? Group { get; set; }

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public Paged<PersonRow> Result { get; private set; } = new([], 0, 1, PeopleQuery.PageSize);
    public bool CanManage { get; private set; }
    public List<SelectListItem> Departments { get; private set; } = [];
    public List<SelectListItem> Groups { get; private set; } = [];
    public bool Filtered => !string.IsNullOrWhiteSpace(Q) || Department is not null || Group is not null || Status != "active";

    public async Task OnGetAsync()
    {
        Kind = Kind == "requesters" ? "requesters" : "agents";
        Status = Status is "inactive" or "all" ? Status : "active";
        Result = await people.ListAsync(new PeopleQuery { Kind = Kind, Status = Status, Search = Q, DepartmentId = Department, GroupId = Group, Page = PageNumber });
        CanManage = await access.HasAsync(Privileges.UsersManage);
        Departments = await AdminSelects.DepartmentsAsync(db, Department, "Any department");
        Groups = Kind == "agents"
            ? (await db.AgentGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync())
                .Select(g => new SelectListItem(g.Name, g.Id.ToString(), g.Id == Group)).ToList()
            : (await db.RequesterGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync())
                .Select(g => new SelectListItem(g.Name, g.Id.ToString(), g.Id == Group)).ToList();
        Groups.Insert(0, new SelectListItem("Any group", "", Group is null));
    }

    public string PageUrl(int page) => Link("/admin/people", ("kind", Kind), ("status", Status == "active" ? null : Status), ("q", Q),
        ("department", Department?.ToString()), ("group", Group?.ToString()), ("page", page > 1 ? page.ToString() : null));
}
