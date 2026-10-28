using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;

namespace Switchboard.Web.Pages.Account;

[AllowAnonymous]
public sealed class LogoutModel(SignInManager<User> signIn, IAuditLog audit) : PageModel
{
    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? Page() : Redirect("/account/login");

    public async Task<IActionResult> OnPostAsync()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            await audit.WriteAsync("auth.logout");
        }
        await signIn.SignOutAsync();
        return Redirect("/account/login");
    }
}
