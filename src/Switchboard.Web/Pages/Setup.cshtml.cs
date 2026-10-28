using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Web.Hosting;

namespace Switchboard.Web.Pages;

/// <summary>First-run setup (FR-ORG-1). Requires the one-time token from the server log (SR-DEP-2).</summary>
[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class SetupModel(
    SwitchboardDbContext db,
    UserManager<User> users,
    SignInManager<User> signIn,
    SetupTokenStore tokens,
    IUserDirectory directory,
    IAuditLog audit,
    IActorAccessor actors) : PageModel
{
    [BindProperty]
    public string Token { get; set; } = "";

    [BindProperty]
    public string OrganizationName { get; set; } = "";

    [BindProperty]
    public string Name { get; set; } = "";

    [BindProperty]
    public string Email { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    public async Task<IActionResult> OnGetAsync() => await SetupDoneAsync() ? Redirect("/account/login") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (await SetupDoneAsync())
        {
            return Redirect("/account/login");
        }
        if (!tokens.Verify(Token))
        {
            ModelState.AddModelError(nameof(Token), "That setup token isn't right. Copy it from the server log; it changes on every restart.");
        }
        if (string.IsNullOrWhiteSpace(OrganizationName))
        {
            ModelState.AddModelError(nameof(OrganizationName), "Enter your organization's name.");
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            ModelState.AddModelError(nameof(Name), "Enter your name.");
        }
        if (!IUserDirectory.IsValidEmail(Email))
        {
            ModelState.AddModelError(nameof(Email), "Enter a valid email address.");
        }
        else if (!await directory.IsEmailFreeAsync(Email))
        {
            ModelState.AddModelError(nameof(Email), "That email already belongs to someone.");
        }
        if (!ModelState.IsValid)
        {
            return Page();
        }

        using var _ = actors.BeginScope(Actor.System("Setup", Channel.Web));
        var user = new User
        {
            Email = Email.Trim(),
            UserName = Email.Trim(),
            DisplayName = Name.Trim(),
            IsAgent = true,
            IsOrgAdmin = true,
            EmailConfirmed = true,
            Availability = AgentAvailability.Available,
        };
        var created = await users.CreateAsync(user, Password);
        if (!created.Succeeded)
        {
            foreach (var error in created.Errors)
            {
                ModelState.AddModelError(nameof(Password), error.Description);
            }
            return Page();
        }

        var workspace = await db.Workspaces.FirstAsync(w => w.IsPrimary);
        var adminRole = await db.Roles.FirstAsync(r => r.Key == RoleKeys.WorkspaceAdmin);
        db.WorkspaceMemberships.Add(new WorkspaceMembership { UserId = user.Id, WorkspaceId = workspace.Id, RoleId = adminRole.Id, TicketScope = TicketScope.Global });
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        org.Name = OrganizationName.Trim();
        org.SetupCompletedAt = DateTimeOffset.UtcNow;
        audit.Record("setup.completed", "organization", org.Id.ToString(), new { admin = user.Email });
        await db.SaveChangesAsync();
        tokens.Revoke();

        await signIn.SignInAsync(user, isPersistent: false);
        return Redirect("/");
    }

    private async Task<bool> SetupDoneAsync() =>
        await db.Users.AnyAsync(u => u.IsOrgAdmin) || !await db.Organizations.AnyAsync(o => o.SetupCompletedAt == null);
}
