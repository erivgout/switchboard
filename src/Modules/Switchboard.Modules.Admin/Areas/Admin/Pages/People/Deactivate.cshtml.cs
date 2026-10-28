using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.People;

/// <summary>Hand off open work (SR-DEL-3). Shared by deactivation and converting an agent to a requester.</summary>
public sealed class HandOffInput
{
    public OpenWorkAction Action { get; set; } = OpenWorkAction.ReassignToGroup;
    public Guid? GroupId { get; set; }
    public Guid? AgentId { get; set; }

    public OpenWorkPlan ToPlan() => new(Action, Action == OpenWorkAction.ReassignToGroup ? GroupId : null, Action == OpenWorkAction.ReassignToAgent ? AgentId : null);
}

public sealed record HandOffModel(HandOffInput Input, OpenWork Work, IReadOnlyList<SelectListItem> Groups, PersonOption? Agent, string PersonName);

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.UsersManage)]
public sealed class DeactivateModel(PeopleService people, PersonLifecycleService lifecycle, SwitchboardDbContext db) : AdminPageModel
{
    [BindProperty]
    public HandOffInput Input { get; set; } = new();

    public PersonDetail Detail { get; private set; } = default!;
    public HandOffModel HandOff { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        await LoadAsync(id);
        return Detail.User.IsActive ? Page() : RedirectToPage("Account", new { id });
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (await TryAsync(() => lifecycle.DeactivateAsync(id, Input.ToPlan())))
        {
            TempData.Toast("Account deactivated");
            return RedirectToPage("Account", new { id });
        }
        await LoadAsync(id);
        return Page();
    }

    private async Task LoadAsync(Guid id)
    {
        Detail = await people.GetAsync(id);
        HandOff = await HandOffLoader.LoadAsync(db, people, lifecycle, Input, Detail);
    }
}

internal static class HandOffLoader
{
    public static async Task<HandOffModel> LoadAsync(SwitchboardDbContext db, PeopleService people, PersonLifecycleService lifecycle, HandOffInput input, PersonDetail detail)
    {
        var work = await lifecycle.GetOpenWorkAsync(detail.User.Id);
        var workspaces = work.Tickets.Select(t => t.WorkspaceId).Distinct().ToList();
        var groups = (await db.AgentGroups.AsNoTracking()
                .Where(g => workspaces.Count == 0 || workspaces.Contains(g.WorkspaceId))
                .OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync())
            .Select(g => new SelectListItem(g.Name, g.Id.ToString(), g.Id == input.GroupId)).ToList();
        if (input.GroupId is null && work.Tickets.FirstOrDefault(t => t.Group is not null) is { } first)
        {
            groups.FirstOrDefault(g => g.Text == first.Group)?.Selected = true;
        }
        return new HandOffModel(input, work, groups, await people.OptionAsync(input.AgentId), detail.User.DisplayName);
    }
}
