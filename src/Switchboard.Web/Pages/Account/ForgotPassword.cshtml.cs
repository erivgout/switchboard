using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;

namespace Switchboard.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class ForgotPasswordModel(
    UserManager<User> users,
    ISystemEmailSender email,
    IAuditLog audit,
    IOptions<SwitchboardOptions> options) : PageModel
{
    [BindProperty]
    public string Email { get; set; } = "";

    public bool Sent { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await users.FindByEmailAsync((Email ?? "").Trim());
        // Same response whether or not the account exists (SR-AUTHN-2).
        if (user is { IsActive: true, IsForgotten: false })
        {
            // Tokens are bound to the security stamp, so completing any reset invalidates the rest (SR-AUTHN-6).
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var link = $"{options.Value.BaseUrl.TrimEnd('/')}/account/reset-password?email={Uri.EscapeDataString(user.Email!)}&token={Uri.EscapeDataString(token)}";
            var safeLink = WebUtility.HtmlEncode(link);
            await email.SendAsync(new OutboundEmail(
                [user.Email!],
                "Reset your Switchboard password",
                $"<p>Hi {WebUtility.HtmlEncode(user.DisplayName)},</p><p>Use this link to choose a new password. It works once, for one hour:</p><p><a href=\"{safeLink}\">{safeLink}</a></p><p>If you didn't ask for this, you can ignore this email.</p>",
                $"Hi {user.DisplayName},\n\nUse this link to choose a new password. It works once, for one hour:\n{link}\n\nIf you didn't ask for this, ignore this email."));
            await audit.WriteAsync("auth.password_reset.requested", "user", user.Id.ToString());
        }
        Sent = true;
        return Page();
    }
}
