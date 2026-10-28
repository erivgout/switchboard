using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages;

/// <summary>What the editor form posts.</summary>
public sealed class KbEditorInput
{
    public string Title { get; set; } = "";
    public string? BodyHtml { get; set; }
    public Guid FolderId { get; set; }
    public KbArticleType Type { get; set; } = KbArticleType.Permanent;
    public string? Tags { get; set; }
    public string? Keywords { get; set; }
    public Guid? OwnerId { get; set; }
    public DateOnly? ReviewBy { get; set; }
    public string? ExternalUrl { get; set; }
    public string? SeoTitle { get; set; }
    public string? SeoDescription { get; set; }
    public List<Guid> RelatedIds { get; set; } = [];
    public List<Guid> AttachmentIds { get; set; } = [];
    public List<Guid> RemoveAttachmentIds { get; set; } = [];
    public int? Revision { get; set; }
    public Guid? SourceTicketId { get; set; }
    public string? ChangeNote { get; set; }

    public KbDraftInput ToDraft() => new()
    {
        FolderId = FolderId,
        Title = Title,
        BodyHtml = BodyHtml,
        Type = Type,
        Tags = (Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Keywords = Keywords,
        OwnerId = OwnerId,
        ReviewBy = ReviewBy,
        ExternalUrl = ExternalUrl,
        SeoTitle = SeoTitle,
        SeoDescription = SeoDescription,
        RelatedArticleIds = RelatedIds,
        AttachmentIds = AttachmentIds,
        RemoveAttachmentIds = RemoveAttachmentIds,
        ExpectedRevision = Revision,
        SourceTicketId = SourceTicketId,
        ChangeNote = ChangeNote,
    };
}

public sealed record KbFolderOption(Guid Id, string Label, bool RequiresApproval);

public sealed record KbPersonOption(Guid Id, string Name);

/// <summary>Shared by the new-article and edit pages.</summary>
public abstract class EditorPage(IKnowledgeService knowledge, IKnowledgeVisibility visibility, SwitchboardDbContext db) : PageModel
{
    protected IKnowledgeService Knowledge => knowledge;
    protected SwitchboardDbContext Db => db;

    [BindProperty] public KbEditorInput Input { get; set; } = new();

    public Guid? ArticleId { get; protected set; }
    public string? DisplayId { get; protected set; }
    public bool IsNew => ArticleId is null;
    public IReadOnlyList<KbFolderOption> Folders { get; private set; } = [];
    public IReadOnlyList<KbPersonOption> Owners { get; private set; } = [];
    public IReadOnlyList<KbArticleLink> Related { get; protected set; } = [];
    public IReadOnlyList<Attachment> Files { get; protected set; } = [];
    public KbViewer Viewer { get; private set; } = default!;
    public bool PublishedBefore { get; protected set; }
    public string? TicketLabel { get; protected set; }

    /// <summary>Whether the folder lets this user publish straight away (otherwise they submit for review).</summary>
    public bool CanPublishIn(Guid folderId) =>
        Folders.FirstOrDefault(f => f.Id == folderId) is { } f && !f.RequiresApproval && Viewer.CanPublish(WorkspaceOf(folderId));

    private KbFolderIndex? _index;

    private Guid WorkspaceOf(Guid folderId) => _index?.Get(folderId)?.WorkspaceId ?? Guid.Empty;

    protected async Task LoadOptionsAsync(CancellationToken ct)
    {
        Viewer = await visibility.ViewerAsync(ct);
        _index = await visibility.FoldersAsync(ct);
        var agentFolders = await visibility.AgentFolderIdsAsync(ct);
        var categories = _index.Categories.Values.Where(c => !c.InTrash).OrderBy(c => c.Order).ThenBy(c => c.Name);
        var options = new List<KbFolderOption>();
        foreach (var category in categories)
        {
            foreach (var (folder, _) in KbUi.Tree(_index, category.Id, agentFolders))
            {
                if (Viewer.CanWrite(folder.WorkspaceId))
                {
                    options.Add(new KbFolderOption(folder.Id, $"{category.Name} / {_index.PathOf(folder.Id)}", folder.RequiresApproval));
                }
            }
        }
        Folders = options;
        Owners = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && (u.IsAgent || u.IsOrgAdmin))
            .OrderBy(u => u.DisplayName)
            .Select(u => new KbPersonOption(u.Id, u.DisplayName))
            .ToListAsync(ct);
    }

    /// <summary>Saves the draft, then publishes or submits when asked. Returns the article id.</summary>
    protected async Task<Guid> SaveAsync(string? intent, CancellationToken ct)
    {
        var draft = Input.ToDraft();
        Guid id;
        if (ArticleId is { } existing)
        {
            id = (await knowledge.SaveDraftAsync(existing, draft, ct)).ArticleId;
        }
        else
        {
            id = (await knowledge.CreateDraftAsync(draft, ct)).ArticleId;
        }
        switch (intent)
        {
            case "publish":
                await knowledge.PublishAsync(id, ct: ct);
                TempData.Toast("Published");
                break;
            case "submit":
                await knowledge.SubmitForReviewAsync(id, Input.ChangeNote, ct);
                TempData.Toast("Sent for review");
                break;
            default:
                TempData.Toast("Draft saved");
                break;
        }
        return id;
    }

    /// <summary>Autosave from kb-editor.js: JSON in, JSON out.</summary>
    protected async Task<IActionResult> AutosaveAsync(CancellationToken ct)
    {
        try
        {
            var draft = Input.ToDraft();
            draft.ChangeNote = null;
            var saved = ArticleId is { } existing
                ? await knowledge.SaveDraftAsync(existing, draft, ct)
                : await knowledge.CreateDraftAsync(draft, ct);
            return new JsonResult(new
            {
                id = saved.ArticleId,
                revision = saved.Revision,
                version = saved.VersionNumber,
                displayId = KbArticle.FormatDisplayId(saved.Number),
                savedAt = saved.SavedAt.ToString("O", CultureInfo.InvariantCulture),
                editUrl = $"/kb/{saved.ArticleId}/edit",
            });
        }
        catch (ValidationException ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = StatusCodes.Status400BadRequest };
        }
        catch (ConflictException ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = StatusCodes.Status409Conflict };
        }
    }

    protected void ShowErrors(ValidationException ex) => this.AddErrors(ex, nameof(Input));
}
