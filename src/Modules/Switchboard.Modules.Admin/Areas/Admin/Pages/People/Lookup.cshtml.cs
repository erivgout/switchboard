using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Pages.People;

/// <summary>Options for person pickers: <c>&lt;option&gt;</c> elements matching a search.</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.UsersView)]
public sealed class LookupModel(PeopleService people) : PageModel
{
    public IReadOnlyList<PersonOption> Results { get; private set; } = [];
    public PersonOption? Selected { get; private set; }
    public string? None { get; private set; }

    public async Task OnGetAsync(string? q, string? kind, Guid? selected, string? none)
    {
        None = string.IsNullOrWhiteSpace(none) ? null : none;
        Results = await people.LookupAsync(q, kind is "agents" or "requesters" ? kind : "all");
        Selected = Results.Any(r => r.Id == selected) ? null : await people.OptionAsync(selected);
    }
}
