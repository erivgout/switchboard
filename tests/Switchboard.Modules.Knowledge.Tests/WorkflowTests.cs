using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;
using Switchboard.Testing;

namespace Switchboard.Modules.Knowledge.Tests;

public sealed class WorkflowTests(KbFixture f) : IClassFixture<KbFixture>
{
    private Task<List<KbArticleVersion>> VersionsAsync(Guid articleId) =>
        f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Set<KbArticleVersion>().AsNoTracking()
            .Where(v => v.ArticleId == articleId).OrderBy(v => v.Number).ToListAsync());

    private Task Save(User user, Guid id, string title, string body, Guid? folder = null) =>
        f.AsAsync(user, sp => sp.GetRequiredService<IKnowledgeService>().SaveDraftAsync(id, new KbDraftInput { FolderId = folder ?? f.Everyone, Title = title, BodyHtml = body }));

    [Fact]
    public async Task An_article_has_at_most_one_published_version()
    {
        var id = await f.ArticleAsync(f.Everyone, "Join the guest network", "<p>Version one.</p>");
        await Save(f.Agent, id, "Join the guest network", "<p>Version two.</p>");
        await f.AsAsync(f.Supervisor, sp => sp.GetRequiredService<IKnowledgeService>().PublishAsync(id));
        await Save(f.Agent, id, "Join the guest network", "<p>Version three.</p>");
        await f.AsAsync(f.Supervisor, sp => sp.GetRequiredService<IKnowledgeService>().PublishAsync(id));

        var versions = await VersionsAsync(id);
        Assert.Equal(3, versions.Count);
        var live = Assert.Single(versions, v => v.State == KbVersionState.Published);
        Assert.Equal(3, live.Number);
        Assert.All(versions.Where(v => v.Number < 3), v => Assert.Equal(KbVersionState.Previous, v.State));

        // The database refuses a second published version too (INV-KB-1).
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var old = await db.Set<KbArticleVersion>().FirstAsync(v => v.ArticleId == id && v.Number == 1);
            old.State = KbVersionState.Published;
            return await db.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task Approval_folder_requires_an_approver_who_is_not_the_author()
    {
        var id = await f.ArticleAsync(f.NeedsApproval, "Acceptable use policy", "<p>Be kind to the network.</p>", publish: false);
        var knowledge = (IServiceProvider sp) => sp.GetRequiredService<IKnowledgeService>();

        // Publishing straight away isn't possible in an approval folder, even for publishers.
        await Assert.ThrowsAsync<ValidationException>(() => f.AsAsync(f.Supervisor, sp => knowledge(sp).PublishAsync(id)));

        await f.AsAsync(f.Agent, sp => knowledge(sp).SubmitForReviewAsync(id, "Ready for a look"));
        var submitted = await f.AsAsync(f.Agent, sp => knowledge(sp).GetForAgentAsync(id));
        Assert.Equal(KbArticleStatus.InReview, submitted.Article.Status);
        Assert.False(submitted.Access.CanEdit);
        Assert.False(submitted.Access.CanApprove);
        await Assert.ThrowsAsync<ValidationException>(() => Save(f.Agent, id, "Edited while in review", "<p>x</p>", f.NeedsApproval));

        // Not the submitter, and not someone who isn't a folder approver.
        await Assert.ThrowsAsync<ForbiddenException>(() => f.AsAsync(f.Agent, sp => knowledge(sp).ApproveAsync(id, null)));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.AsAsync(f.OtherSupervisor, sp => knowledge(sp).ApproveAsync(id, null)));

        // Rejecting needs a comment and returns the draft to the author.
        await Assert.ThrowsAsync<ValidationException>(() => f.AsAsync(f.Supervisor, sp => knowledge(sp).RejectAsync(id, " ")));
        await f.AsAsync(f.Supervisor, sp => knowledge(sp).RejectAsync(id, "Mention personal devices."));
        var rejected = await f.AsAsync(f.Agent, sp => knowledge(sp).GetForAgentAsync(id));
        Assert.Equal(KbArticleStatus.Draft, rejected.Article.Status);
        Assert.Equal(KbReviewDecision.Rejected, rejected.LastDecision!.Decision);
        Assert.Equal("Mention personal devices.", rejected.LastDecision.DecisionComment);
        Assert.True(rejected.Access.CanEdit);

        await Save(f.Agent, id, "Acceptable use policy", "<p>Be kind to the network, including on personal devices.</p>", f.NeedsApproval);
        await f.AsAsync(f.Agent, sp => knowledge(sp).SubmitForReviewAsync(id, null));
        var queue = await f.AsAsync(f.Supervisor, sp => knowledge(sp).ReviewsAwaitingMeAsync());
        Assert.Contains(queue, r => r.ArticleId == id);
        Assert.DoesNotContain(await f.AsAsync(f.OtherSupervisor, sp => knowledge(sp).ReviewsAwaitingMeAsync()), r => r.ArticleId == id);

        await f.AsAsync(f.Supervisor, sp => knowledge(sp).ApproveAsync(id, "Looks good"));
        var published = await f.AsAsync(f.Requester, sp => knowledge(sp).GetPublishedAsync(id));
        Assert.Contains("personal devices", published.BodyText);
        var reviews = await f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Set<KbArticleReview>()
            .Where(r => r.ArticleId == id).OrderBy(r => r.SubmittedAt).Select(r => r.Decision).ToListAsync());
        Assert.Equal([KbReviewDecision.Rejected, KbReviewDecision.Approved], reviews);
    }

    [Fact]
    public async Task Without_folder_approvers_anyone_who_can_publish_approves_but_agents_cannot()
    {
        var id = await f.ArticleAsync(f.Everyone, "Book a meeting room", publish: false);
        await f.AsAsync(f.Agent, sp => sp.GetRequiredService<IKnowledgeService>().SubmitForReviewAsync(id, null));
        var other = await f.Host.CreateAgentAsync("Ana Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        await Assert.ThrowsAsync<ForbiddenException>(() => f.AsAsync(other, sp => sp.GetRequiredService<IKnowledgeService>().ApproveAsync(id, null)));
        await f.AsAsync(f.OtherSupervisor, sp => sp.GetRequiredService<IKnowledgeService>().ApproveAsync(id, null));
        Assert.Equal("Book a meeting room", (await f.AsAsync(f.Requester, sp => sp.GetRequiredService<IKnowledgeService>().GetPublishedAsync(id))).Title);
    }

    [Fact]
    public async Task Organization_admins_cannot_approve_their_own_submission()
    {
        var admin = await f.Host.CreateAgentAsync("Ada Admin", RoleKeys.WorkspaceAdmin);
        await f.Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            (await db.Users.FirstAsync(u => u.Id == admin.Id)).IsOrgAdmin = true;
            return await db.SaveChangesAsync();
        });
        var id = await f.AsAsync(admin, sp => sp.GetRequiredService<IKnowledgeService>()
            .CreateDraftAsync(new KbDraftInput { FolderId = f.NeedsApproval, Title = "Password rotation policy", BodyHtml = "<p>Every 12 months.</p>" }));
        await f.AsAsync(admin, sp => sp.GetRequiredService<IKnowledgeService>().SubmitForReviewAsync(id.ArticleId, null));

        await Assert.ThrowsAsync<ForbiddenException>(() => f.AsAsync(admin, sp => sp.GetRequiredService<IKnowledgeService>().ApproveAsync(id.ArticleId, null)));
        await f.AsAsync(f.Supervisor, sp => sp.GetRequiredService<IKnowledgeService>().ApproveAsync(id.ArticleId, null));
    }

    [Fact]
    public async Task A_save_that_raced_a_publish_cannot_rewrite_the_published_version()
    {
        var id = await f.ArticleAsync(f.Everyone, "Set your out-of-office reply", "<p>Original.</p>", publish: false);
        // The editor's save loaded the draft (as SaveDraftAsync does) before a supervisor published it.
        await using var scope = f.Host.Services.CreateAsyncScope();
        var stale = await scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>().Set<KbArticleVersion>().FirstAsync(v => v.ArticleId == id);
        await f.AsAsync(f.Supervisor, sp => sp.GetRequiredService<IKnowledgeService>().PublishAsync(id));

        stale.BodyHtml = "<p>Unreviewed edit.</p>";
        stale.Revision++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>().SaveChangesAsync());
        Assert.Contains("Original", (await f.AsAsync(f.Requester, sp => sp.GetRequiredService<IKnowledgeService>().GetPublishedAsync(id))).BodyHtml);
    }

    [Fact]
    public async Task Agents_write_drafts_but_only_publishers_publish()
    {
        var id = await f.ArticleAsync(f.Everyone, "Order a new headset", publish: false);
        await Assert.ThrowsAsync<ForbiddenException>(() => f.AsAsync(f.Agent, sp => sp.GetRequiredService<IKnowledgeService>().PublishAsync(id)));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.AsAsync(f.Agent, sp => sp.GetRequiredService<IKnowledgeService>().ArchiveAsync(id)));
        await f.AsAsync(f.Supervisor, sp => sp.GetRequiredService<IKnowledgeService>().PublishAsync(id));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.AsAsync(f.Agent, sp => sp.GetRequiredService<IKnowledgeService>().TrashAsync([id])));
    }

    [Fact]
    public async Task Restoring_a_version_creates_a_new_draft_and_keeps_history()
    {
        var id = await f.ArticleAsync(f.Everyone, "Map a network drive", "<p>Original steps.</p>");
        await Save(f.Agent, id, "Map a network drive", "<p>Second steps.</p>");
        await f.AsAsync(f.Supervisor, sp => sp.GetRequiredService<IKnowledgeService>().PublishAsync(id));
        await Save(f.Agent, id, "Map a network drive (wip)", "<p>Unfinished third steps.</p>");

        var restored = await f.AsAsync(f.Agent, sp => sp.GetRequiredService<IKnowledgeService>().RestoreVersionAsync(id, 1));
        Assert.Equal(4, restored.VersionNumber);

        var versions = await VersionsAsync(id);
        Assert.Equal([KbVersionState.Previous, KbVersionState.Published, KbVersionState.Previous, KbVersionState.Draft], versions.Select(v => v.State));
        Assert.Contains("Original steps", versions[3].BodyHtml);
        Assert.Equal(1, versions[3].RestoredFromNumber);
        Assert.Contains("Unfinished third steps", versions[2].BodyHtml);

        // Readers still see version 2 until the restored draft is published.
        Assert.Contains("Second steps", (await f.AsAsync(f.Requester, sp => sp.GetRequiredService<IKnowledgeService>().GetPublishedAsync(id))).BodyText);

        var diff = KbDiff.Compare(versions[1].BodyText, versions[3].BodyText);
        Assert.Contains(diff, l => l.Segments.Any(s => s.Op == KbDiffOp.Removed && s.Text.Contains("Second")));
        Assert.Contains(diff, l => l.Segments.Any(s => s.Op == KbDiffOp.Added && s.Text.Contains("Original")));
    }

    [Fact]
    public async Task Concurrent_drafts_are_detected()
    {
        var id = await f.ArticleAsync(f.Everyone, "Reserve a parking space", publish: false);
        var detail = await f.AsAsync(f.Agent, sp => sp.GetRequiredService<IKnowledgeService>().GetForAgentAsync(id));
        await f.AsAsync(f.Supervisor, sp => sp.GetRequiredService<IKnowledgeService>().SaveDraftAsync(id, new KbDraftInput
        {
            FolderId = f.Everyone, Title = "Reserve a parking space", BodyHtml = "<p>Supervisor edit.</p>", ExpectedRevision = detail.Latest.Revision,
        }));
        await Assert.ThrowsAsync<ConflictException>(() => f.AsAsync(f.Agent, sp => sp.GetRequiredService<IKnowledgeService>().SaveDraftAsync(id, new KbDraftInput
        {
            FolderId = f.Everyone, Title = "Reserve a parking space", BodyHtml = "<p>Stale edit.</p>", ExpectedRevision = detail.Latest.Revision,
        })));
    }

    [Fact]
    public async Task Article_html_is_sanitized_on_the_way_in()
    {
        var id = await f.ArticleAsync(f.Everyone, "Use the coffee machine",
            "<h2 onclick=\"steal()\">Steps</h2><p>Press <strong>start</strong>.<script>alert(1)</script></p><a href=\"javascript:alert(1)\">x</a><img src=\"/files/00000000-0000-0000-0000-000000000000/a.png\">");
        var article = await f.AsAsync(f.Requester, sp => sp.GetRequiredService<IKnowledgeService>().GetPublishedAsync(id));
        Assert.DoesNotContain("<script", article.BodyHtml);
        Assert.DoesNotContain("onclick", article.BodyHtml);
        Assert.DoesNotContain("javascript:", article.BodyHtml);
        Assert.Contains("<strong>start</strong>", article.BodyHtml);
        Assert.Contains("src=\"/files/", article.BodyHtml);
        var rendered = KbHtml.Render(article.BodyHtml);
        Assert.Equal("Steps", Assert.Single(rendered.Outline).Text);
    }

    [Fact]
    public async Task Draft_only_images_stay_private_until_published()
    {
        var upload = await f.AsAsync(f.Agent, sp => sp.GetRequiredService<IAttachmentService>()
            .UploadAsync(new MemoryStream(Encoding.ASCII.GetBytes("PNGDATA")), "shot.png", "image/png", AttachmentOwners.Pending, null, inline: true));
        var id = await f.ArticleAsync(f.Everyone, "Find your laptop's serial number", publish: true);
        await Save(f.Agent, id, "Find your laptop's serial number", $"<p>Look here:</p><img src=\"/files/{upload.Id}/shot.png\" alt=\"Label\">");

        async Task<bool> CanOpen(User user) => await f.AsAsync(user, async sp =>
        {
            var opened = await sp.GetRequiredService<IAttachmentService>().OpenAsync(upload.Id);
            if (opened is { } o)
            {
                await o.Content.DisposeAsync();
            }
            return opened is not null;
        });

        Assert.True(await CanOpen(f.Agent));
        Assert.False(await CanOpen(f.Requester));
        await f.AsAsync(f.Supervisor, sp => sp.GetRequiredService<IKnowledgeService>().PublishAsync(id));
        Assert.True(await CanOpen(f.Requester));
    }

    [Fact]
    public async Task Votes_are_one_per_reader_and_deflections_skip_people_who_filed_a_ticket()
    {
        var id = await f.ArticleAsync(f.Everyone, "Unlock your account");
        var knowledge = (IServiceProvider sp) => sp.GetRequiredService<IKnowledgeService>();
        await f.AsAsync(f.Requester, sp => knowledge(sp).VoteAsync(id, false, "Didn't mention the self-service page.", null));
        await f.AsAsync(f.Requester, sp => knowledge(sp).VoteAsync(id, true, null, null));
        // Signed-out visitors can't even see the article unless the public knowledge base is on.
        await Assert.ThrowsAsync<NotFoundException>(() => f.AnonymousAsync(sp => knowledge(sp).VoteAsync(id, true, null, "visitor-1")));
        await f.SetPublicAsync(true);
        try
        {
            await f.AnonymousAsync(sp => knowledge(sp).VoteAsync(id, true, null, "visitor-1"));
            await f.AnonymousAsync(sp => knowledge(sp).VoteAsync(id, true, null, "visitor-1"));
            await Assert.ThrowsAsync<ValidationException>(() => f.AnonymousAsync(sp => knowledge(sp).VoteAsync(id, false, "spam", "visitor-2")));
        }
        finally
        {
            await f.SetPublicAsync(false);
        }

        await f.AsAsync(f.Requester, sp => knowledge(sp).RecordDeflectionCandidateAsync(id));
        await f.AsAsync(f.GroupRequester, sp => knowledge(sp).RecordDeflectionCandidateAsync(id));
        // The second reader still files a ticket afterwards: not a deflection.
        await f.Host.RunAsync(SwitchboardTestHost.As(f.GroupRequester, Channel.Portal), sp => sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand
        {
            Subject = "Still locked out", DescriptionHtml = "<p>Help</p>", AsRequester = true, SourceKey = TicketSourceKeys.Portal,
        }));
        await f.Host.ProcessOutboxAsync();
        await f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Set<KbDeflection>()
            .Where(d => d.ArticleId == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.ViewedAt, d => d.ViewedAt.AddHours(-1))));

        var detail = await f.AsAsync(f.Agent, sp => knowledge(sp).GetForAgentAsync(id));
        Assert.Equal(2, detail.Feedback.Helpful);
        Assert.Equal(0, detail.Feedback.NotHelpful);
        Assert.Equal(1, detail.Feedback.Deflections);
        var comments = await f.AsAsync(f.Agent, sp => knowledge(sp).FeedbackCommentsAsync(id));
        Assert.Equal("Didn't mention the self-service page.", Assert.Single(comments).Comment);
    }

    [Fact]
    public async Task Announcements_reach_their_audience_only_while_active()
    {
        var finance = await f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Departments.Where(d => d.Name == "Finance").Select(d => d.Id).FirstAsync());
        var service = (IServiceProvider sp) => sp.GetRequiredService<IKbAnnouncementService>();
        var now = DateTimeOffset.UtcNow;
        await f.AsAsync(f.Manager, async sp =>
        {
            await service(sp).SaveAsync(null, new KbAnnouncementInput { WorkspaceId = f.Host.Workspace.Id, Title = "Agents only memo", BodyHtml = "<p>Shift swap.</p>", Audience = KbAnnouncementAudience.Agents, ActiveFrom = now.AddMinutes(-5), IsPublished = true });
            await service(sp).SaveAsync(null, new KbAnnouncementInput { WorkspaceId = f.Host.Workspace.Id, Title = "Finance close", BodyHtml = "<p>Month end.</p>", Audience = KbAnnouncementAudience.Specific, DepartmentIds = [finance], ActiveFrom = now.AddMinutes(-5), IsPublished = true });
            await service(sp).SaveAsync(null, new KbAnnouncementInput { WorkspaceId = f.Host.Workspace.Id, Title = "Old outage", BodyHtml = "<p>Over.</p>", ActiveFrom = now.AddDays(-3), ActiveTo = now.AddDays(-1), IsPublished = true });
            await service(sp).SaveAsync(null, new KbAnnouncementInput { WorkspaceId = f.Host.Workspace.Id, Title = "Unposted draft", BodyHtml = "<p>Soon.</p>", ActiveFrom = now.AddMinutes(-5), IsPublished = false });
        });

        var agent = await f.AsAsync(f.Agent, sp => service(sp).ActiveAsync(asAgent: true));
        var requester = await f.AsAsync(f.Requester, sp => service(sp).ActiveAsync(asAgent: false));
        var financeRequester = await f.AsAsync(f.FinanceRequester, sp => service(sp).ActiveAsync(asAgent: false));
        Assert.Contains(agent, a => a.Title == "Agents only memo");
        Assert.DoesNotContain(requester, a => a.Title is "Agents only memo" or "Finance close" or "Old outage" or "Unposted draft");
        Assert.Contains(financeRequester, a => a.Title == "Finance close");
        Assert.Empty(await f.AnonymousAsync(sp => service(sp).ActiveAsync(asAgent: false)));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.AsAsync(f.Agent, sp => service(sp).SaveAsync(null, new KbAnnouncementInput
        {
            WorkspaceId = f.Host.Workspace.Id, Title = "Nope", BodyHtml = "<p>x</p>", ActiveFrom = now, IsPublished = true,
        })));
    }
}
