using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Events;
using Switchboard.Core.Services;
using Switchboard.Modules.Approvals.Domain;

namespace Switchboard.Modules.Approvals.Services;

/// <summary>
/// Signed, single-use email links for deciding without signing in (SR-APR-2, when "require sign-in" is off).
/// Only a SHA-256 hash of the token is stored.
/// </summary>
public sealed class ApprovalLinkTokens(SwitchboardDbContext db, TimeProvider time)
{
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Adds a token row to the unit of work and returns the raw token for the link.</summary>
    internal string Issue(Guid requestId, Guid levelId, Guid recipientId, Guid? onBehalfOfId, TimeSpan lifetime)
    {
        var token = WebEncoders.Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        db.Set<AprLinkToken>().Add(new AprLinkToken
        {
            RequestId = requestId,
            LevelId = levelId,
            RecipientId = recipientId,
            OnBehalfOfId = onBehalfOfId,
            TokenHash = Hash(token),
            ExpiresAt = now.Add(lifetime),
            CreatedAt = now,
        });
        return token;
    }

    /// <summary>A token that exists, hasn't been used and hasn't expired, or null.</summary>
    internal async Task<AprLinkToken?> FindValidAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }
        var hash = Hash(token);
        var now = time.GetUtcNow();
        return await db.Set<AprLinkToken>().FirstOrDefaultAsync(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now, ct);
    }

    private static class WebEncoders
    {
        public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>Emails approvers (and their active delegates) when they're asked to decide or reminded, and requesters when it's done.</summary>
internal sealed class ApprovalEmailNotifier(
    SwitchboardDbContext db,
    ISystemEmailSender email,
    ISettingsService settings,
    IApprovalDelegationService delegation,
    ApprovalLinkTokens tokens,
    ILiveNotifier live,
    IOptions<SwitchboardOptions> options,
    TimeProvider time,
    ILogger<ApprovalEmailNotifier> logger)
    : IEventHandler<ApprovalRequested>, IEventHandler<ApprovalReminded>, IEventHandler<ApprovalDecided>
{
    public Task HandleAsync(ApprovalRequested e, CancellationToken ct) => NotifyApproversAsync(e.ApprovalId, e.Level, e.ApproverIds, reminder: false, ct);

    public Task HandleAsync(ApprovalReminded e, CancellationToken ct) => NotifyApproversAsync(e.ApprovalId, e.Level, e.ApproverIds, reminder: true, ct);

    public async Task HandleAsync(ApprovalDecided e, CancellationToken ct)
    {
        if (e.Status is not (ApprovalStatus.Approved or ApprovalStatus.Rejected))
        {
            return;
        }
        var request = await db.Set<AprRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == e.ApprovalId, ct);
        if (request is null)
        {
            return;
        }
        var recipientIds = new[] { request.RequesterId, request.RequestedById ?? request.RequesterId }.Distinct().ToList();
        var recipients = await db.Users.AsNoTracking()
            .Where(u => recipientIds.Contains(u.Id) && u.IsActive && u.Email != null)
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.IsAgent })
            .ToListAsync(ct);
        var decider = await db.Users.AsNoTracking().Where(u => u.Id == e.DecidedById).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "An approver";
        var approved = e.Status == ApprovalStatus.Approved;
        foreach (var r in recipients)
        {
            var link = Url(r.IsAgent ? $"/approvals/{request.Id}" : $"/portal/approvals/{request.Id}");
            var html = new StringBuilder();
            html.Append($"<p>Hi {Enc(FirstName(r.DisplayName))},</p>");
            html.Append(approved
                ? $"<p>Your request <strong>{Enc(request.Title)}</strong> was approved. The team will take it from here.</p>"
                : $"<p>Your request <strong>{Enc(request.Title)}</strong> wasn't approved. {Enc(decider)} said:</p><blockquote>{Enc(e.Comment)}</blockquote><p>You can change the request and send it again.</p>");
            html.Append($"<p><a href=\"{Enc(link)}\">See the details</a></p>");
            await SendAsync(r.Email!, approved ? $"Approved: {request.Title}" : $"Not approved: {request.Title}", html.ToString(), ct);
            await live.ToUserAsync(r.Id, "approval.decided", new { id = request.Id, status = e.Status.ToString() }, ct);
        }
    }

    private async Task NotifyApproversAsync(Guid approvalId, int levelOrder, IReadOnlyList<Guid> approverIds, bool reminder, CancellationToken ct)
    {
        var request = await db.Set<AprRequest>().Include(r => r.Levels).Include(r => r.Decisions).AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == approvalId, ct);
        if (request is null || request.Status != ApprovalStatus.Requested || request.CurrentLevel != levelOrder)
        {
            return;
        }
        var level = request.Levels.First(l => l.Order == levelOrder);
        var waiting = approverIds.Where(id => request.Decisions.All(d => d.LevelId != level.Id || d.ApproverId != id)).ToList();
        var delegates = await delegation.DelegatesOfAsync(waiting, time.GetUtcNow(), ct);

        // Each approver, plus their active delegate (FR-APR-6).
        var sends = waiting.Select(id => (Recipient: id, OnBehalfOf: (Guid?)null))
            .Concat(delegates.Select(d => (Recipient: d.Value, OnBehalfOf: (Guid?)d.Key)))
            .ToList();
        var userIds = sends.Select(s => s.Recipient).Concat(waiting).Append(request.RequesterId).Distinct().ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.IsAgent, u.IsActive })
            .ToDictionaryAsync(u => u.Id, ct);

        var requireLogin = await settings.GetAsync(ApprovalSettingKeys.RequireLogin, ApprovalSettingKeys.RequireLoginDefault, request.WorkspaceId, ct);
        var expiry = TimeSpan.FromHours(Math.Clamp(
            await settings.GetAsync(ApprovalSettingKeys.LinkExpiryHours, ApprovalSettingKeys.LinkExpiryHoursDefault, request.WorkspaceId, ct), 1, 24 * 14));
        var requester = users.TryGetValue(request.RequesterId, out var rq) ? rq.DisplayName : "Someone";

        var messages = new List<(string To, string Subject, string Html)>();
        foreach (var (recipientId, onBehalfOf) in sends)
        {
            if (!users.TryGetValue(recipientId, out var user) || !user.IsActive || string.IsNullOrWhiteSpace(user.Email))
            {
                continue;
            }
            var link = requireLogin
                ? Url(user.IsAgent ? $"/approvals/{request.Id}" : $"/portal/approvals/{request.Id}")
                : Url($"/approvals/email/{tokens.Issue(request.Id, level.Id, recipientId, onBehalfOf, expiry)}");
            var html = new StringBuilder();
            html.Append($"<p>Hi {Enc(FirstName(user.DisplayName))},</p>");
            if (onBehalfOf is { } covered && users.TryGetValue(covered, out var approver))
            {
                html.Append($"<p>You're covering approvals for {Enc(approver.DisplayName)}.</p>");
            }
            html.Append(reminder
                ? $"<p>{Enc(requester)} is still waiting for a decision on <strong>{Enc(request.Title)}</strong>.</p>"
                : $"<p>{Enc(requester)} needs your approval for <strong>{Enc(request.Title)}</strong>.</p>");
            html.Append($"<p><a href=\"{Enc(link)}\">Review and decide</a></p>");
            if (!requireLogin)
            {
                html.Append($"<p>This link works once and expires in {(int)expiry.TotalHours} hours. You'll confirm your decision on the page it opens.</p>");
            }
            messages.Add((user.Email!, (reminder ? "Reminder: approval needed: " : "Approval needed: ") + request.Title, html.ToString()));
        }

        if (!requireLogin && messages.Count > 0)
        {
            // Token rows commit before the links go out.
            await db.SaveChangesAsync(ct);
        }
        foreach (var m in messages)
        {
            await SendAsync(m.To, m.Subject, m.Html, ct);
        }
        foreach (var (recipientId, _) in sends)
        {
            await live.ToUserAsync(recipientId, "approval.requested", new { id = request.Id }, ct);
        }
    }

    private async Task SendAsync(string to, string subject, string html, CancellationToken ct)
    {
        try
        {
            await email.SendAsync(new OutboundEmail([to], subject, html), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One bad address must not block everyone else's email; the failure is logged for admins.
            logger.LogWarning(ex, "Approval email to {To} failed", to);
        }
    }

    private string Url(string path) => options.Value.BaseUrl.TrimEnd('/') + path;

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string FirstName(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
}
