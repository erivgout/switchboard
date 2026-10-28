using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Sources;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.FieldsManage)]
public sealed class EditModel(TicketConfigService config) : AdminPageModel
{
    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public string Icon { get; set; } = "";

    [BindProperty]
    public Guid? ReplacementId { get; set; }

    public TicketSource Source { get; private set; } = default!;
    public int Tickets { get; private set; }
    public IReadOnlyList<TicketSource> Others { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }
        Name = Source.Name;
        Icon = Source.Icon;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }
        if (await TryAsync(() => config.UpdateSourceAsync(id, Name ?? "", Icon), ""))
        {
            TempData.Toast("Source saved");
            return Redirect("/admin/sources");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }
        var moved = 0;
        if (await TryAsync(async () => moved = await config.DeleteSourceAsync(id, ReplacementId), ""))
        {
            TempData.Toast(moved > 0 ? $"Source deleted. {moved} {(moved == 1 ? "ticket" : "tickets")} updated." : "Source deleted");
            return Redirect("/admin/sources");
        }
        Name = Source.Name;
        Icon = Source.Icon;
        return Page();
    }

    private async Task<bool> LoadAsync(Guid id)
    {
        var all = await config.ListSourcesAsync();
        var row = all.FirstOrDefault(r => r.Source.Id == id);
        if (row is null || row.Source.IsSystem)
        {
            return false;
        }
        Source = row.Source;
        Tickets = row.Tickets;
        Others = all.Where(r => r.Source.Id != id).Select(r => r.Source).ToList();
        return true;
    }
}
