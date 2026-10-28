using System.Globalization;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages;

/// <summary>Labels and small renderers shared by the knowledge pages.</summary>
public static class KbUi
{
    public const string Css = "~/_content/Switchboard.Modules.Knowledge/css/knowledge.css";
    public const string EditorJs = "~/_content/Switchboard.Modules.Knowledge/js/kb-editor.js";
    public const string PagesJs = "~/_content/Switchboard.Modules.Knowledge/js/kb.js";

    public static string StatusLabel(KbArticleStatus status) => status switch
    {
        KbArticleStatus.Draft => "Draft",
        KbArticleStatus.InReview => "In review",
        KbArticleStatus.Published => "Published",
        _ => "Archived",
    };

    public static IHtmlContent State(KbArticleStatus status, KbVersionState? pending = null)
    {
        var (cls, icon) = status switch
        {
            KbArticleStatus.Draft => ("draft", "bi-pencil"),
            KbArticleStatus.InReview => ("inreview", "bi-hourglass-split"),
            KbArticleStatus.Published => ("published", "bi-check2-circle"),
            _ => ("archived", "bi-archive"),
        };
        var html = $"<span class=\"kb-state kb-state--{cls}\"><i class=\"bi {icon}\" aria-hidden=\"true\"></i>{StatusLabel(status)}</span>";
        if (pending is KbVersionState.Draft or KbVersionState.InReview)
        {
            var text = pending == KbVersionState.InReview ? "Changes in review" : "Unpublished changes";
            html += $" <span class=\"kb-state kb-state--changes\"><i class=\"bi bi-pencil-square\" aria-hidden=\"true\"></i>{text}</span>";
        }
        return new HtmlString(html);
    }

    public static string VersionStateLabel(KbVersionState state, DateTimeOffset? publishedAt) => state switch
    {
        KbVersionState.Draft => "Draft",
        KbVersionState.InReview => "In review",
        KbVersionState.Published => "Live",
        _ => publishedAt is null ? "Earlier draft" : "Previously published",
    };

    public static string TypeLabel(KbArticleType type) => type == KbArticleType.Workaround ? "Workaround" : "Permanent fix";

    public static string Date(DateOnly date) => date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n.ToString("N0", CultureInfo.InvariantCulture)} {many}";

    /// <summary>Puts a service <see cref="ValidationException"/> on the page's ModelState.</summary>
    public static void AddErrors(this PageModel page, ValidationException ex, string prefix = "")
    {
        foreach (var (field, messages) in ex.Errors)
        {
            var key = string.IsNullOrEmpty(prefix) ? Pascal(field) : $"{prefix}.{Pascal(field)}";
            foreach (var message in messages)
            {
                page.ModelState.AddModelError(key, message);
            }
        }
    }

    private static string Pascal(string field) => field.Length == 0 ? field : char.ToUpperInvariant(field[0]) + field[1..];

    public static string ArticleUrl(Guid id) => $"/kb/{id}";
    public static string PortalUrl(Guid id) => $"/portal/kb/{id}";

    public static IEnumerable<(KbFolderNode Folder, int Depth)> Tree(KbFolderIndex index, Guid categoryId, IReadOnlySet<Guid>? only = null)
    {
        IEnumerable<(KbFolderNode, int)> Walk(Guid? parent, int depth)
        {
            foreach (var f in index.Folders.Values
                         .Where(f => f.CategoryId == categoryId && f.ParentId == parent && !f.IsTrashedItself)
                         .OrderBy(f => f.Order).ThenBy(f => f.Name))
            {
                if (only is null || only.Contains(f.Id))
                {
                    yield return (f, depth);
                }
                foreach (var child in Walk(f.Id, depth + 1))
                {
                    yield return child;
                }
            }
        }
        return Walk(null, 0);
    }
}

/// <summary>Model for the shared rich-text editor partial.</summary>
public sealed record KbRichText(string Name, string? Html, string Label, string Placeholder, string IdPrefix, bool Small = false);
