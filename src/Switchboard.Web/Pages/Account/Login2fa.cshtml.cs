using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;

namespace Switchboard.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class Login2faModel(SignInManager<User> signIn, IAuditLog audit) : PageModel
{
    [BindProperty]
    public string Code { get; set; } = "";

    [BindProperty]
    public bool RememberDevice { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool RememberMe { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync() =>
        await signIn.GetTwoFactorAuthenticationUserAsync() is null ? Redirect("/account/login") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            return Redirect("/account/login");
        }

        var code = (Code ?? "").Replace(" ", "").Replace("-", "");
        var result = code.Length == 6 && code.All(char.IsDigit)
            ? await signIn.TwoFactorAuthenticatorSignInAsync(code, RememberMe, RememberDevice)
            : await signIn.TwoFactorRecoveryCodeSignInAsync(code);

        if (result.Succeeded)
        {
            await audit.WriteAsync("auth.mfa.succeeded", "user", user.Id.ToString());
            return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
        }
        await audit.WriteAsync("auth.mfa.failed", "user", user.Id.ToString());
        Error = result.IsLockedOut ? "Too many failed attempts. Try again in 15 minutes." : "That code didn't work. Check the time on your device and try a new code.";
        return Page();
    }
}
