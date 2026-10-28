using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages;

[Authorize(Policy = "priv:" + KbPrivileges.Write)]
public sealed class EditModel(IKnowledgeService knowledge, IKnowledgeVisibility visibility, SwitchboardDbContext db)
    : EditorPage(knowledge, visibility, db)
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var detail = await LoadAsync(ct);
        if (!detail.Access.CanEdit)
        {
            TempData.Toast(detail.PendingReview is not null
                ? $"{detail.Article.DisplayId} is waiting for review. Withdraw it from review to make changes."
                : $"You can't edit {detail.Article.DisplayId} right now.", ToastKind.Error);
            return Redirect($"/kb/{Id}");
        }
        var a = detail.Article;
        Input = new KbEditorInput
        {
            Title = detail.Latest.Title,
            BodyHtml = detail.Latest.BodyHtml,
            FolderId = a.FolderId,
            Type = a.Type,
            Tags = string.Join(", ", a.Tags),
            Keywords = a.Keywords,
            OwnerId = a.OwnerId,
            ReviewBy = a.ReviewBy,
            ExternalUrl = a.ExternalUrl,
            SeoTitle = a.SeoTitle,
            SeoDescription = a.SeoDescription,
            RelatedIds = detail.Related.Select(r => r.Id).ToList(),
            Revision = detail.Latest.State == KbVersionState.Draft ? detail.Latest.Revision : null,
            ChangeNote = detail.Latest.State == KbVersionState.Draft ? detail.Latest.ChangeNote : null,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? intent, CancellationToken ct)
    {
        try
        {
            ArticleId = Id;
            var id = await SaveAsync(intent, ct);
            return Redirect($"/kb/{id}");
        }
        catch (ValidationException ex)
        {
            ShowErrors(ex);
            await LoadAsync(ct);
            return Page();
        }
        catch (ConflictException ex)
        {
            ModelState.AddModelError("", ex.Message);
            await LoadAsync(ct);
            return Page();
        }
    }

    public Task<IActionResult> OnPostAutosaveAsync(CancellationToken ct)
    {
        ArticleId = Id;
        return AutosaveAsync(ct);
    }

    private async Task<KbArticleDetail> LoadAsync(CancellationToken ct)
    {
        var detail = await Knowledge.GetForAgentAsync(Id, ct);
        if (detail.Article.DeletedAt is not null)
        {
            throw new NotFoundException();
        }
        ArticleId = detail.Article.Id;
        DisplayId = detail.Article.DisplayId;
        Related = detail.Related;
        Files = detail.Attachments;
        PublishedBefore = detail.Article.PublishedAt is not null;
        await LoadOptionsAsync(ct);
        return detail;
    }
}
