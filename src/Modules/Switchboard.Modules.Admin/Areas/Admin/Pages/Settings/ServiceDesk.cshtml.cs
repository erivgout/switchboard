using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Settings;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.SettingsManage)]
public sealed class ServiceDeskModel(AdminSettingsService settings) : AdminPageModel
{
    public sealed class SettingsForm
    {
        public int AutoCloseDays { get; set; }
        public string ClosedReplyBehavior { get; set; } = "reopen";
        public bool PriorityMatrixEnabled { get; set; }
        public bool RequesterCanClose { get; set; }
        public bool RequesterCanReopen { get; set; }
        public bool ShowAgentNamesToRequesters { get; set; }
        public int TrashRetentionDays { get; set; }
        public bool BlockParentCloseWithOpenChildren { get; set; }
    }

    [BindProperty]
    public SettingsForm Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        var s = await settings.GetServiceDeskAsync();
        Input = new SettingsForm
        {
            AutoCloseDays = s.AutoCloseDays,
            ClosedReplyBehavior = s.ClosedReplyBehavior,
            PriorityMatrixEnabled = s.PriorityMatrixEnabled,
            RequesterCanClose = s.RequesterCanClose,
            RequesterCanReopen = s.RequesterCanReopen,
            ShowAgentNamesToRequesters = s.ShowAgentNamesToRequesters,
            TrashRetentionDays = s.TrashRetentionDays,
            BlockParentCloseWithOpenChildren = s.BlockParentCloseWithOpenChildren,
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var value = new ServiceDeskSettings
        {
            AutoCloseDays = Input.AutoCloseDays,
            ClosedReplyBehavior = Input.ClosedReplyBehavior,
            PriorityMatrixEnabled = Input.PriorityMatrixEnabled,
            RequesterCanClose = Input.RequesterCanClose,
            RequesterCanReopen = Input.RequesterCanReopen,
            ShowAgentNamesToRequesters = Input.ShowAgentNamesToRequesters,
            TrashRetentionDays = Input.TrashRetentionDays,
            BlockParentCloseWithOpenChildren = Input.BlockParentCloseWithOpenChildren,
        };
        if (!ModelState.IsValid)
        {
            return Page();
        }
        if (await TryAsync(() => settings.SaveServiceDeskAsync(value)))
        {
            TempData.Toast("Settings saved");
            return RedirectToPage();
        }
        return Page();
    }
}
