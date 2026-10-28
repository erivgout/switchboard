using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Approvals.Services;
using Switchboard.Testing;

namespace Switchboard.Modules.Approvals.Tests;

public sealed class ApprovalServiceTests(ApprovalsFixture f) : IClassFixture<ApprovalsFixture>
{
    [Fact]
    public async Task Approver_sources_resolve_to_people_when_requested()
    {
        var picked = await f.Host.CreateRequesterAsync("Pat Picked");
        var request = await f.RequestAsync(ApprovalsFixture.Chain(
            (ApprovalRule.Any, [new ApproverSource { Kind = ApproverSourceKind.Manager, ManagerLevel = 1 }]),
            (ApprovalRule.Any, [new ApproverSource { Kind = ApproverSourceKind.Manager, ManagerLevel = 2 }]),
            (ApprovalRule.Any, [new ApproverSource { Kind = ApproverSourceKind.DepartmentHead }]),
            (ApprovalRule.Everyone, [new ApproverSource { Kind = ApproverSourceKind.AgentGroup, GroupId = f.ServiceDeskId }]),
            (ApprovalRule.Any, [new ApproverSource { Kind = ApproverSourceKind.LookupField, FieldKey = "budget_owner" }])),
            lookups: new Dictionary<string, ApprovalLookupValue> { ["budget_owner"] = new("Budget owner", [picked.Id]) });

        var loaded = await f.LoadAsync(request.Id);
        Guid[] At(int order) => loaded.Levels.Single(l => l.Order == order).Approvers.Select(a => a.UserId).ToArray();
        Assert.Equal([f.Manager.Id], At(1));
        Assert.Equal([f.GrandManager.Id], At(2));
        Assert.Equal([f.DepartmentHead.Id], At(3));
        Assert.Contains(f.Agent.Id, At(4));
        Assert.Contains(f.OtherAgent.Id, At(4));
        Assert.Equal([picked.Id], At(5));
        Assert.Equal(ApprovalStatus.Requested, loaded.Status);
        Assert.Equal(1, loaded.CurrentLevel);
    }

    [Fact]
    public async Task Levels_run_in_sequence_and_the_next_level_is_asked_when_one_completes()
    {
        var request = await f.RequestAsync(ApprovalsFixture.Chain(
            (ApprovalRule.Any, [ApprovalsFixture.Users(f.Manager)]),
            (ApprovalRule.Everyone, [ApprovalsFixture.Users(f.Agent, f.OtherAgent)])));

        // Level 2 approvers can't jump the queue.
        await Assert.ThrowsAsync<ForbiddenException>(() => f.DecideAsync(f.Agent, request.Id, ApprovalVerdict.Approved, channel: Channel.Web));

        await f.DecideAsync(f.Manager, request.Id, ApprovalVerdict.Approved, "Fine by me");
        var afterLevel1 = await f.LoadAsync(request.Id);
        Assert.Equal(ApprovalStatus.Requested, afterLevel1.Status);
        Assert.Equal(2, afterLevel1.CurrentLevel);

        var askedLevel2 = await f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().OutboxMessages.AsNoTracking()
            .Where(m => m.EventType.Contains(nameof(ApprovalRequested)) && m.PayloadJson.Contains(request.Id.ToString())).CountAsync());
        Assert.Equal(2, askedLevel2);

        await f.DecideAsync(f.Agent, request.Id, ApprovalVerdict.Approved, channel: Channel.Web);
        Assert.Equal(ApprovalStatus.Requested, (await f.LoadAsync(request.Id)).Status);
        await f.DecideAsync(f.OtherAgent, request.Id, ApprovalVerdict.Approved, channel: Channel.Web);

        var done = await f.LoadAsync(request.Id);
        Assert.Equal(ApprovalStatus.Approved, done.Status);
        Assert.Null(done.CurrentLevel);
        Assert.NotNull(done.CompletedAt);
        // The cached status always equals the pure evaluation (INV-APR-2).
        Assert.Equal(ApprovalEvaluator.Evaluate(done).Status, done.Status);
    }

    [Fact]
    public async Task One_rejection_under_everyone_rejects_and_needs_a_reason()
    {
        var request = await f.RequestAsync(ApprovalsFixture.Chain((ApprovalRule.Everyone, [ApprovalsFixture.Users(f.Manager, f.DepartmentHead)])));

        var noReason = await Assert.ThrowsAsync<ValidationException>(() => f.DecideAsync(f.Manager, request.Id, ApprovalVerdict.Rejected));
        Assert.True(noReason.Errors.ContainsKey("comment"));

        await f.DecideAsync(f.Manager, request.Id, ApprovalVerdict.Rejected, "Over budget this quarter");
        Assert.Equal(ApprovalStatus.Rejected, (await f.LoadAsync(request.Id)).Status);

        // Decisions after completion are refused.
        await Assert.ThrowsAsync<ValidationException>(() => f.DecideAsync(f.DepartmentHead, request.Id, ApprovalVerdict.Approved));
    }

    [Fact]
    public async Task People_who_are_not_approvers_cannot_decide()
    {
        var request = await f.RequestAsync(ApprovalsFixture.Chain((ApprovalRule.Any, [ApprovalsFixture.Users(f.Manager)])));

        // An agent who can see the ticket sees the approval but may not decide (INV-APR-3).
        await Assert.ThrowsAsync<ForbiddenException>(() => f.DecideAsync(f.Agent, request.Id, ApprovalVerdict.Approved, channel: Channel.Web));
        // A requester who can't see it gets "not found", so its existence doesn't leak.
        await Assert.ThrowsAsync<NotFoundException>(() => f.DecideAsync(f.Outsider, request.Id, ApprovalVerdict.Approved));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Host.RunAsync(SwitchboardTestHost.As(f.Outsider, Channel.Portal),
            sp => sp.GetRequiredService<IApprovalService>().GetAsync(request.Id)));
        // The system actor never decides on anyone's behalf.
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<IApprovalService>()
            .DecideAsync(request.Id, new DecideApprovalCommand { Verdict = ApprovalVerdict.Approved })));

        Assert.Equal(ApprovalStatus.Requested, (await f.LoadAsync(request.Id)).Status);
    }

    [Fact]
    public async Task Requesters_cannot_approve_their_own_requests_by_default()
    {
        // The requester is left out of the approvers (FR-APR-8)...
        var request = await f.RequestAsync(ApprovalsFixture.Chain((ApprovalRule.Any, [ApprovalsFixture.Users(f.Requester, f.Manager)])));
        var loaded = await f.LoadAsync(request.Id);
        Assert.DoesNotContain(loaded.Levels[0].Approvers, a => a.UserId == f.Requester.Id);
        // ...and is refused on the server whatever the channel (SR-APR-1).
        await Assert.ThrowsAsync<ForbiddenException>(() => f.DecideAsync(f.Requester, request.Id, ApprovalVerdict.Approved));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.DecideAsync(f.Requester, request.Id, ApprovalVerdict.Approved, channel: Channel.Mcp));

        // When the requester is the only possible approver, the request can't start.
        var error = await Assert.ThrowsAsync<ValidationException>(() => f.RequestAsync(ApprovalsFixture.Chain((ApprovalRule.Any, [ApprovalsFixture.Users(f.Requester)]))));
        Assert.Contains("own requests", error.Message);
    }

    [Fact]
    public async Task Self_approval_can_be_allowed_by_policy()
    {
        var requester = await f.Host.CreateRequesterAsync("Sally Self");
        await f.Host.RunAsSystemAsync(async sp =>
        {
            await sp.GetRequiredService<ISettingsService>().SetAsync(ApprovalSettingKeys.AllowSelfApproval, true);
            await sp.GetRequiredService<SwitchboardDbContext>().SaveChangesAsync();
            return true;
        });
        try
        {
            var request = await f.RequestAsync(ApprovalsFixture.Chain((ApprovalRule.Any, [ApprovalsFixture.Users(requester)])), requester);
            await f.DecideAsync(requester, request.Id, ApprovalVerdict.Approved);
            Assert.Equal(ApprovalStatus.Approved, (await f.LoadAsync(request.Id)).Status);
        }
        finally
        {
            await f.Host.RunAsSystemAsync(async sp =>
            {
                await sp.GetRequiredService<ISettingsService>().SetAsync(ApprovalSettingKeys.AllowSelfApproval, false);
                await sp.GetRequiredService<SwitchboardDbContext>().SaveChangesAsync();
                return true;
            });
        }
    }

    [Fact]
    public async Task A_delegate_decides_on_behalf_of_the_approver()
    {
        var boss = await f.Host.CreateRequesterAsync("Boris Boss");
        var deputy = await f.Host.CreateRequesterAsync("Della Deputy");
        await f.DelegateAsync(boss, deputy);
        var request = await f.RequestAsync(ApprovalsFixture.Chain((ApprovalRule.Any, [ApprovalsFixture.Users(boss)])));

        var pending = await f.Host.RunAsync(SwitchboardTestHost.As(deputy, Channel.Portal), sp => sp.GetRequiredService<IApprovalService>().ListPendingAsync());
        var item = Assert.Single(pending, p => p.Id == request.Id);
        Assert.Equal("Boris Boss", item.OnBehalfOfName);

        var decision = await f.DecideAsync(deputy, request.Id, ApprovalVerdict.Approved, "Covering for Boris");
        Assert.Equal(boss.Id, decision.ApproverId);
        Assert.Equal(deputy.Id, decision.DecidedById);
        Assert.Equal(boss.Id, decision.OnBehalfOfId);
        Assert.Equal(Channel.Portal, decision.Channel);
        Assert.Equal(ApprovalStatus.Approved, (await f.LoadAsync(request.Id)).Status);

        var activity = await f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().ActivityEntries.AsNoTracking()
            .Where(a => a.EntityId == request.Id).Select(a => a.Summary).ToListAsync());
        Assert.Contains(activity, s => s.Contains("Della Deputy, on behalf of Boris Boss, approved"));
    }

    [Fact]
    public async Task An_ended_or_future_delegation_gives_no_authority()
    {
        var boss = await f.Host.CreateRequesterAsync("Bea Boss");
        var deputy = await f.Host.CreateRequesterAsync("Dev Deputy");
        await f.DelegateAsync(boss, deputy, DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow.AddDays(5));
        var request = await f.RequestAsync(ApprovalsFixture.Chain((ApprovalRule.Any, [ApprovalsFixture.Users(boss)])));

        await Assert.ThrowsAsync<NotFoundException>(() => f.DecideAsync(deputy, request.Id, ApprovalVerdict.Approved));
    }

    [Fact]
    public async Task Delegation_allows_one_delegate_at_a_time_and_no_cycles()
    {
        var a = await f.Host.CreateRequesterAsync("Ada A");
        var b = await f.Host.CreateRequesterAsync("Ben B");
        var c = await f.Host.CreateRequesterAsync("Cy C");
        await f.DelegateAsync(a, b);

        await Assert.ThrowsAsync<ValidationException>(() => f.DelegateAsync(a, c));     // overlapping second delegate
        await Assert.ThrowsAsync<ValidationException>(() => f.DelegateAsync(b, a));     // direct cycle
        await f.DelegateAsync(b, c);
        await Assert.ThrowsAsync<ValidationException>(() => f.DelegateAsync(c, a));     // a -> b -> c -> a
        await Assert.ThrowsAsync<ValidationException>(() => f.DelegateAsync(c, c));     // yourself
    }

    [Fact]
    public async Task Recorded_decisions_are_immutable()
    {
        var request = await f.RequestAsync(ApprovalsFixture.Chain((ApprovalRule.Any, [ApprovalsFixture.Users(f.Manager)])));
        var decision = await f.DecideAsync(f.Manager, request.Id, ApprovalVerdict.Approved, "Approved");

        // INV-APR-1, enforced by the database for any writer.
        await Assert.ThrowsAsync<SqlException>(() => f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Database
            .ExecuteSqlInterpolatedAsync($"UPDATE AprDecisions SET Verdict = 2, Comment = 'changed' WHERE Id = {decision.Id}")));
        await Assert.ThrowsAsync<SqlException>(() => f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Database
            .ExecuteSqlInterpolatedAsync($"DELETE FROM AprDecisions WHERE Id = {decision.Id}")));

        var stored = Assert.Single((await f.LoadAsync(request.Id)).Decisions);
        Assert.Equal(ApprovalVerdict.Approved, stored.Verdict);
        Assert.Equal("Approved", stored.Comment);
    }

    [Fact]
    public async Task Cancelling_keeps_decisions_and_allows_a_new_request()
    {
        var request = await f.RequestAsync(ApprovalsFixture.Chain((ApprovalRule.Everyone, [ApprovalsFixture.Users(f.Manager, f.DepartmentHead)])));
        await f.DecideAsync(f.Manager, request.Id, ApprovalVerdict.Approved);

        // Requesters can't cancel; agents who can edit the ticket can.
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Host.RunAsync(SwitchboardTestHost.As(f.Requester, Channel.Portal),
            sp => sp.GetRequiredService<IApprovalService>().CancelAsync(request.Id, null)));
        await f.Host.RunAsync(SwitchboardTestHost.As(f.Agent), sp => sp.GetRequiredService<IApprovalService>().CancelAsync(request.Id, "Wrong item"));

        var cancelled = await f.LoadAsync(request.Id);
        Assert.Equal(ApprovalStatus.Cancelled, cancelled.Status);
        Assert.Single(cancelled.Decisions);
        await Assert.ThrowsAsync<ValidationException>(() => f.DecideAsync(f.DepartmentHead, request.Id, ApprovalVerdict.Approved));

        var again = await f.Host.RunAsync(SwitchboardTestHost.As(f.Agent), sp => sp.GetRequiredService<IApprovalService>().RequestAsync(new RequestApprovalCommand
        {
            SubjectType = ApprovalSubjectTypes.Ticket,
            SubjectId = cancelled.SubjectId,
            Title = "Second try",
            RequesterId = f.Requester.Id,
            Config = ApprovalsFixture.Chain((ApprovalRule.Any, [ApprovalsFixture.Users(f.Manager)])),
        }));
        Assert.Equal(ApprovalStatus.Requested, again.Status);
        // The agent who started it may not approve it either.
        Assert.Contains(f.Agent.Id, again.ConflictUserIds);
    }

    [Fact]
    public async Task Requesting_needs_permission_to_manage_the_record()
    {
        var ticket = await f.FileTicketAsync(f.Requester);
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Host.RunAsync(SwitchboardTestHost.As(f.Requester, Channel.Portal),
            sp => sp.GetRequiredService<IApprovalService>().RequestAsync(new RequestApprovalCommand
            {
                SubjectType = ApprovalSubjectTypes.Ticket,
                SubjectId = ticket.Id,
                Title = "Please",
                RequesterId = f.Requester.Id,
                Config = ApprovalsFixture.Chain((ApprovalRule.Any, [ApprovalsFixture.Users(f.Outsider)])),
            })));
    }

    [Fact]
    public async Task Approvers_and_their_delegates_are_emailed_and_sign_in_links_are_used_by_default()
    {
        var boss = await f.Host.CreateRequesterAsync("Mona Mail");
        var deputy = await f.Host.CreateRequesterAsync("Dan Deputy");
        await f.DelegateAsync(boss, deputy);
        while (f.Email.Sent.TryDequeue(out _))
        {
        }

        var request = await f.RequestAsync(ApprovalsFixture.Chain((ApprovalRule.Any, [ApprovalsFixture.Users(boss)])));
        await f.Host.ProcessOutboxAsync();

        var mails = f.Email.Sent.Where(m => m.Subject.Contains(request.Title)).ToList();
        Assert.Contains(mails, m => m.To.Contains(boss.Email!));
        Assert.Contains(mails, m => m.To.Contains(deputy.Email!) && m.HtmlBody.Contains("covering approvals for Mona Mail"));
        Assert.All(mails, m => Assert.Contains($"/portal/approvals/{request.Id}", m.HtmlBody));
    }

    [Fact]
    public async Task Without_required_sign_in_links_are_single_use_tokens_stored_hashed()
    {
        var approver = await f.Host.CreateRequesterAsync("Tia Token");
        await SetRequireLoginAsync(false);
        try
        {
            var request = await f.RequestAsync(ApprovalsFixture.Chain((ApprovalRule.Any, [ApprovalsFixture.Users(approver)])));
            await f.Host.ProcessOutboxAsync();
            var mail = f.Email.Sent.Last(m => m.To.Contains(approver.Email!));
            var token = System.Text.RegularExpressions.Regex.Match(mail.HtmlBody, "/approvals/email/([A-Za-z0-9_-]+)").Groups[1].Value;
            Assert.NotEmpty(token);

            var stored = await f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Set<AprLinkToken>().AsNoTracking()
                .SingleAsync(t => t.RequestId == request.Id));
            Assert.NotEqual(token, stored.TokenHash);
            Assert.Equal(ApprovalLinkTokens.Hash(token), stored.TokenHash);
            Assert.Equal(approver.Id, stored.RecipientId);
            Assert.True(stored.ExpiresAt > DateTimeOffset.UtcNow.AddHours(71));
        }
        finally
        {
            await SetRequireLoginAsync(true);
        }
    }

    [Fact]
    public async Task Reminders_go_to_approvers_who_have_not_decided()
    {
        var request = await f.RequestAsync(ApprovalsFixture.Chain((ApprovalRule.Everyone, [ApprovalsFixture.Users(f.Manager, f.DepartmentHead)])));
        await f.DecideAsync(f.Manager, request.Id, ApprovalVerdict.Approved);

        var reminded = await f.Host.RunAsync(SwitchboardTestHost.As(f.Requester, Channel.Portal), sp => sp.GetRequiredService<IApprovalService>().RemindAsync(request.Id));
        Assert.Equal(1, reminded);
        // Manual reminders are limited to one an hour.
        await Assert.ThrowsAsync<ValidationException>(() => f.Host.RunAsync(SwitchboardTestHost.As(f.Requester, Channel.Portal),
            sp => sp.GetRequiredService<IApprovalService>().RemindAsync(request.Id)));
        // People outside the request can't nudge it.
        await Assert.ThrowsAsync<NotFoundException>(() => f.Host.RunAsync(SwitchboardTestHost.As(f.Outsider, Channel.Portal),
            sp => sp.GetRequiredService<IApprovalService>().RemindAsync(request.Id)));

        await f.Host.ProcessOutboxAsync();
        Assert.Contains(f.Email.Sent, m => m.To.Contains(f.DepartmentHead.Email!) && m.Subject.StartsWith("Reminder") && m.Subject.Contains(request.Title));
    }

    private Task SetRequireLoginAsync(bool value) => f.Host.RunAsSystemAsync(async sp =>
    {
        await sp.GetRequiredService<ISettingsService>().SetAsync(ApprovalSettingKeys.RequireLogin, value);
        await sp.GetRequiredService<SwitchboardDbContext>().SaveChangesAsync();
        return true;
    });
}
