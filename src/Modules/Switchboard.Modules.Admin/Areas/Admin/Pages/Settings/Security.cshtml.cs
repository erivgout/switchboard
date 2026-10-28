using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Settings;

/// <summary>Requester sign-up (FR-IAM-18), sessions, passwords and network ranges (FR-IAM-19).</summary>
[Authorize(Policy = SwitchboardPolicies.OrgAdmin)]
public sealed class SecurityModel(AdminSettingsService settings) : AdminPageModel
{
    public sealed class SecurityForm
    {
        public string SignupPolicy { get; set; } = "open";
        public string? SignupDomains { get; set; }
        public bool AutoCreateRequestersFromEmail { get; set; }
        public int SessionIdleMinutes { get; set; }
        public int SessionAbsoluteHours { get; set; }
        public string? AgentIpRanges { get; set; }
        public string? AdminIpRanges { get; set; }
        public string? RequesterIpRanges { get; set; }
        public int PasswordMinLength { get; set; }
        public bool PasswordRequireMixed { get; set; }
        public int PasswordHistory { get; set; }
        public int PasswordExpiryDays { get; set; }
        public int LockoutAttempts { get; set; }
        public int LockoutMinutes { get; set; }
        public bool SignOutOnPasswordChange { get; set; }
    }

    [BindProperty]
    public SecurityForm Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        var s = await settings.GetSignInAsync();
        Input = new SecurityForm
        {
            SignupPolicy = s.SignupPolicy,
            SignupDomains = string.Join('\n', s.SignupDomains),
            AutoCreateRequestersFromEmail = s.AutoCreateRequestersFromEmail,
            SessionIdleMinutes = s.SessionIdleMinutes,
            SessionAbsoluteHours = s.SessionAbsoluteHours,
            AgentIpRanges = string.Join('\n', s.AgentIpRanges),
            AdminIpRanges = string.Join('\n', s.AdminIpRanges),
            RequesterIpRanges = string.Join('\n', s.RequesterIpRanges),
            PasswordMinLength = s.PasswordMinLength,
            PasswordRequireMixed = s.PasswordRequireMixed,
            PasswordHistory = s.PasswordHistory,
            PasswordExpiryDays = s.PasswordExpiryDays,
            LockoutAttempts = s.LockoutAttempts,
            LockoutMinutes = s.LockoutMinutes,
            SignOutOnPasswordChange = s.SignOutOnPasswordChange,
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }
        static string[] Lines(string? v) => (v ?? "").Split(['\n', ',', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var i = Input;
        var value = new SignInSettings
        {
            SignupPolicy = i.SignupPolicy,
            SignupDomains = Lines(i.SignupDomains),
            AutoCreateRequestersFromEmail = i.AutoCreateRequestersFromEmail,
            SessionIdleMinutes = i.SessionIdleMinutes,
            SessionAbsoluteHours = i.SessionAbsoluteHours,
            AgentIpRanges = Lines(i.AgentIpRanges),
            AdminIpRanges = Lines(i.AdminIpRanges),
            RequesterIpRanges = Lines(i.RequesterIpRanges),
            PasswordMinLength = i.PasswordMinLength,
            PasswordRequireMixed = i.PasswordRequireMixed,
            PasswordHistory = i.PasswordHistory,
            PasswordExpiryDays = i.PasswordExpiryDays,
            LockoutAttempts = i.LockoutAttempts,
            LockoutMinutes = i.LockoutMinutes,
            SignOutOnPasswordChange = i.SignOutOnPasswordChange,
        };
        if (await TryAsync(() => settings.SaveSignInAsync(value)))
        {
            TempData.Toast("Sign-in settings saved");
            return RedirectToPage();
        }
        return Page();
    }
}
