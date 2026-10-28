using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;
using Switchboard.UI;

namespace Switchboard.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class ResetPasswordModel(UserManager<User> users, IAuditLog audit) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Email { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    [BindProperty]
    public string Confirm { get; set; } = "";

    public IActionResult OnGet() =>
        string.IsNullOrEmpty(Email) || string.IsNullOrEmpty(Token) ? Redirect("/account/forgot-password") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (Password != Confirm)
        {
            ModelState.AddModelError(nameof(Confirm), "The two passwords don't match.");
            return Page();
        }
        var user = await users.FindByEmailAsync(Email);
        if (user is null || !user.IsActive)
        {
            ModelState.AddModelError("", "This reset link is no longer valid. Request a new one.");
            return Page();
        }
        var result = await users.ResetPasswordAsync(user, Token, Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError("", error.Code == "InvalidToken" ? "This reset link is no longer valid. Request a new one." : error.Description);
            }
            return Page();
        }
        // Signs out every other session (security stamp rotates on reset).
        await users.ResetAccessFailedCountAsync(user);
        await audit.WriteAsync("auth.password_reset.completed", "user", user.Id.ToString());
        TempData.Toast("Password changed. Sign in with your new password.");
        return Redirect("/account/login");
    }
}
