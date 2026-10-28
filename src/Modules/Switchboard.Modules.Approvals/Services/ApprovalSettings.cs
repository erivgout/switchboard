namespace Switchboard.Modules.Approvals.Services;

/// <summary>Instance settings for approvals (stored through <c>ISettingsService</c>).</summary>
public static class ApprovalSettingKeys
{
    /// <summary>Email links open a signed-in page (default). When off, links are signed, single-use and expire (SR-APR-2).</summary>
    public const string RequireLogin = "approvals.require_login";
    /// <summary>Lets requesters approve their own requests. Off by default (FR-APR-8).</summary>
    public const string AllowSelfApproval = "approvals.allow_self_approval";
    /// <summary>Hours between automatic reminders to approvers who haven't decided; 0 turns them off.</summary>
    public const string ReminderHours = "approvals.reminder_hours";
    /// <summary>Hours an email link stays valid when sign-in isn't required.</summary>
    public const string LinkExpiryHours = "approvals.link_expiry_hours";

    public const bool RequireLoginDefault = true;
    public const bool AllowSelfApprovalDefault = false;
    public const int ReminderHoursDefault = 24;
    public const int LinkExpiryHoursDefault = 72;
}

public static class ApprovalPrivileges
{
    public const string Manage = "approvals.manage";
}
