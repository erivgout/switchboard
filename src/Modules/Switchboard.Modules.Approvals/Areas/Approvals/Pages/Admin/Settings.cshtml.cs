using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Approvals.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Approvals.Areas.Approvals.Pages.Admin;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + ApprovalPrivileges.Manage)]
public sealed class SettingsModel(ISettingsService settings, IPermissionService permissions, IAuditLog audit, SwitchboardDbContext db) : PageModel
{
    public sealed class SettingsInput
    {
        public bool RequireLogin { get; set; } = ApprovalSettingKeys.RequireLoginDefault;
        public bool AllowSelfApproval { get; set; }

        [Range(0, 168, ErrorMessage = "Use a number of hours from 0 to 168.")]
        public int ReminderHours { get; set; } = ApprovalSettingKeys.ReminderHoursDefault;

        [Range(1, 336, ErrorMessage = "Use a number of hours from 1 to 336.")]
        public int LinkExpiryHours { get; set; } = ApprovalSettingKeys.LinkExpiryHoursDefault;
    }

    [BindProperty]
    public SettingsInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        Input = new SettingsInput
        {
            RequireLogin = await settings.GetAsync(ApprovalSettingKeys.RequireLogin, ApprovalSettingKeys.RequireLoginDefault),
            AllowSelfApproval = await settings.GetAsync(ApprovalSettingKeys.AllowSelfApproval, ApprovalSettingKeys.AllowSelfApprovalDefault),
            ReminderHours = await settings.GetAsync(ApprovalSettingKeys.ReminderHours, ApprovalSettingKeys.ReminderHoursDefault),
            LinkExpiryHours = await settings.GetAsync(ApprovalSettingKeys.LinkExpiryHours, ApprovalSettingKeys.LinkExpiryHoursDefault),
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await permissions.DemandAsync(ApprovalPrivileges.Manage);
        if (!ModelState.IsValid)
        {
            return Page();
        }
        await settings.SetAsync(ApprovalSettingKeys.RequireLogin, Input.RequireLogin);
        await settings.SetAsync(ApprovalSettingKeys.AllowSelfApproval, Input.AllowSelfApproval);
        await settings.SetAsync(ApprovalSettingKeys.ReminderHours, Input.ReminderHours);
        await settings.SetAsync(ApprovalSettingKeys.LinkExpiryHours, Input.LinkExpiryHours);
        audit.Record("approvals.settings.updated", "settings", "approvals", Input);
        await db.SaveChangesAsync();
        TempData.Toast("Approval settings saved");
        return RedirectToPage();
    }
}
