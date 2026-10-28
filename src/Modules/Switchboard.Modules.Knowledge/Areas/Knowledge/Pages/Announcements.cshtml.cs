using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages;

[Authorize(Policy = "priv:" + KbPrivileges.Manage)]
public sealed class AnnouncementsModel(IKbAnnouncementService announcements) : PageModel
{
    public IReadOnlyList<KbAnnouncementItem> Items { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct) => Items = await announcements.ListForManagersAsync(ct);

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        await announcements.DeleteAsync(id, ct);
        TempData.Toast("Announcement deleted");
        return RedirectToPage();
    }

    public static string AudienceLabel(KbAnnouncementItem a) => a.Audience switch
    {
        KbAnnouncementAudience.Everyone => "Everyone",
        KbAnnouncementAudience.Agents => "Agents",
        KbAnnouncementAudience.Requesters => "Requesters",
        _ => KbUi.Plural(a.TargetCount, "group or department", "groups and departments"),
    };
}
