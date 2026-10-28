using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Modules.Admin.Services;

/// <summary>
/// Sends a "choose your password" email. The link is a one-hour, single-use password reset token
/// bound to the security stamp, so a later reset or deactivation invalidates it (SR-AUTHN-6).
/// </summary>
public sealed class InvitationService(
    SwitchboardDbContext db,
    AdminAccess access,
    UserManager<User> users,
    ISystemEmailSender email,
    IAuditLog audit,
    IOptions<SwitchboardOptions> options)
{
    public async Task InviteAsync(Guid userId, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, ct: ct);
        await access.EnsureCanManageUserAsync(userId, ct);
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new NotFoundException();
        if (!user.IsActive || user.IsForgotten || string.IsNullOrEmpty(user.Email))
        {
            throw new ValidationException("User", $"{user.DisplayName} can't be invited because the account is inactive or has no email address.");
        }

        var org = await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).Select(o => new { o.Name, o.BaseUrl }).FirstAsync(ct);
        var baseUrl = (string.IsNullOrWhiteSpace(org.BaseUrl) ? options.Value.BaseUrl : org.BaseUrl).TrimEnd('/');
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var link = $"{baseUrl}/account/reset-password?email={Uri.EscapeDataString(user.Email)}&token={Uri.EscapeDataString(token)}";
        var inviter = access.Actor.DisplayName;
        var safeLink = WebUtility.HtmlEncode(link);
        var orgName = WebUtility.HtmlEncode(org.Name);

        await email.SendAsync(new OutboundEmail(
            [user.Email],
            $"You're invited to {org.Name}",
            $"<p>Hi {WebUtility.HtmlEncode(user.DisplayName)},</p>"
            + $"<p>{WebUtility.HtmlEncode(inviter)} added you to {orgName} on Switchboard. Choose a password to sign in:</p>"
            + $"<p><a href=\"{safeLink}\">{safeLink}</a></p>"
            + "<p>The link works once, for one hour. If it expires, use \"Forgot password\" on the sign-in page.</p>",
            $"Hi {user.DisplayName},\n\n{inviter} added you to {org.Name} on Switchboard. Choose a password to sign in:\n{link}\n\n"
            + "The link works once, for one hour. If it expires, use \"Forgot password\" on the sign-in page."), ct);

        audit.Record("users.invited", "user", userId.ToString());
        await db.SaveChangesAsync(ct);
    }
}
