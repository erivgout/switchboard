using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages;

[Authorize(Policy = "priv:" + KbPrivileges.View)]
public sealed class ArticleModel(IKnowledgeService knowledge) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    /// <summary>"published" shows the live version when a newer draft exists.</summary>
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }

    public KbArticleDetail Detail { get; private set; } = default!;
    public KbArticleVersion Shown { get; private set; } = default!;
    public KbRenderedBody Body { get; private set; } = default!;
    public IReadOnlyList<KbFeedbackComment> Comments { get; private set; } = [];
    public bool ShowingPublished => Detail.Published is not null && Shown.Id == Detail.Published.Id && Detail.HasPendingChanges;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Detail = await knowledge.GetForAgentAsync(Id, ct);
        Shown = Show == "published" && Detail.Published is not null ? Detail.Published : Detail.Latest;
        Body = KbHtml.Render(Shown.BodyHtml);
        Comments = await knowledge.FeedbackCommentsAsync(Id, ct);
        if (Detail.Article.Status == KbArticleStatus.Published && Detail.Article.DeletedAt is null)
        {
            await knowledge.RecordViewAsync(Id, ct);
        }
    }

    public Task<IActionResult> OnPostSubmitAsync(string? note, CancellationToken ct) =>
        Act(() => knowledge.SubmitForReviewAsync(Id, note, ct), "Sent for review");

    public Task<IActionResult> OnPostWithdrawAsync(CancellationToken ct) =>
        Act(() => knowledge.WithdrawReviewAsync(Id, ct), "Withdrawn from review");

    public Task<IActionResult> OnPostApproveAsync(string? comment, CancellationToken ct) =>
        Act(() => knowledge.ApproveAsync(Id, comment, ct), "Approved and published");

    public Task<IActionResult> OnPostRejectAsync(string? comment, CancellationToken ct) =>
        Act(() => knowledge.RejectAsync(Id, comment ?? "", ct), "Sent back to the author");

    public Task<IActionResult> OnPostPublishAsync(CancellationToken ct) =>
        Act(() => knowledge.PublishAsync(Id, ct: ct), "Published");

    public Task<IActionResult> OnPostArchiveAsync(CancellationToken ct) =>
        Act(() => knowledge.ArchiveAsync(Id, ct), "Archived");

    public Task<IActionResult> OnPostUnarchiveAsync(CancellationToken ct) =>
        Act(() => knowledge.UnarchiveAsync(Id, ct), "Restored from the archive as a draft");

    public Task<IActionResult> OnPostDiscardAsync(CancellationToken ct) =>
        Act(() => knowledge.DiscardDraftAsync(Id, ct), "Draft changes discarded");

    public Task<IActionResult> OnPostTrashAsync(CancellationToken ct) =>
        Act(() => knowledge.TrashAsync([Id], ct), "Moved to trash");

    public Task<IActionResult> OnPostRestoreAsync(CancellationToken ct) =>
        Act(() => knowledge.RestoreAsync([Id], ct), "Restored from trash");

    public async Task<IActionResult> OnPostPurgeAsync(CancellationToken ct)
    {
        try
        {
            await knowledge.PurgeAsync([Id], ct);
            TempData.Toast("Article deleted permanently");
            return Redirect("/kb/trash");
        }
        catch (ValidationException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
            return Redirect($"/kb/{Id}");
        }
    }

    private async Task<IActionResult> Act(Func<Task> action, string done)
    {
        try
        {
            await action();
            TempData.Toast(done);
        }
        catch (ValidationException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        catch (ConflictException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return Redirect($"/kb/{Id}");
    }
}
