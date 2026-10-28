using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class LoginModel(
    SignInManager<User> signIn,
    SwitchboardDbContext db,
    IAuditLog audit,
    IActorAccessor actors,
    IOptions<SwitchboardOptions> options,
    IWebHostEnvironment env) : PageModel
{
    // One message for every failure so attackers can't tell which emails exist (SR-AUTHN-2).
    private const string GenericError = "That email and password don't match an active account.";

    [BindProperty, EmailAddress]
    public string Email { get; set; } = "";

    [BindProperty, DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [BindProperty]
    public bool RememberMe { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    public bool ShowDemoHint => env.IsDevelopment() && options.Value.DemoData;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await db.Organizations.AnyAsync(o => o.SetupCompletedAt == null) && !await db.Users.AnyAsync(u => u.IsOrgAdmin))
        {
            return Redirect("/setup");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var normalized = (Email ?? "").Trim().ToUpperInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized);
        using var _ = actors.BeginScope(user is null ? Actor.Anonymous : Actor.ForUser(user, Channel.Web));

        if (user is null || !user.IsActive || user.PasswordHash is null)
        {
            await audit.WriteAsync("auth.login.failed", "user", user?.Id.ToString(), new { email = Email, reason = user is null ? "unknown" : "inactive_or_no_password" });
            Error = GenericError;
            return Page();
        }

        var result = await signIn.PasswordSignInAsync(user, Password ?? "", RememberMe, lockoutOnFailure: true);
        if (result.RequiresTwoFactor)
        {
            return Redirect($"/account/login-2fa?rememberMe={RememberMe}&returnUrl={Uri.EscapeDataString(SafeReturnUrl())}");
        }
        if (result.IsLockedOut)
        {
            await audit.WriteAsync("auth.login.locked_out", "user", user.Id.ToString());
            Error = "Too many failed attempts. Try again in 15 minutes, or reset your password.";
            return Page();
        }
        if (!result.Succeeded)
        {
            await audit.WriteAsync("auth.login.failed", "user", user.Id.ToString(), new { reason = "bad_password" });
            Error = GenericError;
            return Page();
        }

        user.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.WriteAsync("auth.login.succeeded", "user", user.Id.ToString());
        return LocalRedirect(SafeReturnUrl());
    }

    private string SafeReturnUrl() => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/";
}
