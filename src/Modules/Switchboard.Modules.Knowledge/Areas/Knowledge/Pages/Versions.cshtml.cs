using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages;

[Authorize(Policy = "priv:" + KbPrivileges.View)]
public sealed class VersionsModel(IKnowledgeService knowledge) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    /// <summary>Older version to compare.</summary>
    [BindProperty(SupportsGet = true)] public int? From { get; set; }
    /// <summary>Newer version to compare.</summary>
    [BindProperty(SupportsGet = true)] public int? To { get; set; }

    public KbArticleDetail Detail { get; private set; } = default!;
    public IReadOnlyList<KbVersionSummary> Versions { get; private set; } = [];
    public KbArticleVersion? Left { get; private set; }
    public KbArticleVersion? Right { get; private set; }
    public IReadOnlyList<KbDiffLine> Diff { get; private set; } = [];

    public bool CanRestore =>
        Detail.Article.DeletedAt is null && Detail.Article.Status != KbArticleStatus.Archived
        && Detail.Latest.State != KbVersionState.InReview && (Detail.Access.CanEdit || Detail.Access.CanDiscardDraft);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Detail = await knowledge.GetForAgentAsync(Id, ct);
        Versions = await knowledge.VersionsAsync(Id, ct);
        if (To is { } to)
        {
            var from = From ?? Versions.Where(v => v.Number < to).Select(v => (int?)v.Number).FirstOrDefault();
            Right = await knowledge.VersionAsync(Id, to, ct);
            if (from is { } f)
            {
                Left = await knowledge.VersionAsync(Id, f, ct);
                Diff = KbDiff.Compare(Left.Title + "\n" + Left.BodyText, Right.Title + "\n" + Right.BodyText);
            }
        }
    }

    public async Task<IActionResult> OnPostRestoreAsync(int number, CancellationToken ct)
    {
        try
        {
            var saved = await knowledge.RestoreVersionAsync(Id, number, ct);
            TempData.Toast($"Version {number} restored as a new draft (version {saved.VersionNumber})");
            return Redirect($"/kb/{Id}/edit");
        }
        catch (ValidationException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
            return Redirect($"/kb/{Id}/versions");
        }
    }
}
