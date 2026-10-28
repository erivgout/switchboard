using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages.Portal;

public sealed record KbVoteModel(Guid ArticleId, bool? Helpful, bool HasComment, bool SignedIn, bool JustVoted);

/// <summary>Requester article page: published version only, folder visibility enforced by the service (INV-KB-2).</summary>
[AllowAnonymous]
public sealed class PortalArticleModel(IKnowledgeService knowledge, IKnowledgeVisibility visibility) : PageModel
{
    /// <summary>Identifies a signed-out reader's vote; random and meaningless outside this feature.</summary>
    public const string VisitorCookie = "sb.kbv";

    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(SupportsGet = true, Name = "ref")] public string? Ref { get; set; }

    public KbPublishedArticle Article { get; private set; } = default!;
    public KbRenderedBody Body { get; private set; } = default!;
    public KbVoteModel Vote { get; private set; } = default!;
    public bool SignedIn { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        SignedIn = (await visibility.ViewerAsync(ct)).SignedIn;
        try
        {
            Article = await knowledge.GetPublishedAsync(Id, ct);
        }
        catch (NotFoundException) when (!SignedIn)
        {
            // It may be readable after signing in.
            return Challenge();
        }
        Body = KbHtml.Render(Article.BodyHtml);
        await knowledge.RecordViewAsync(Id, ct);
        if (Ref == "deflect" && SignedIn)
        {
            await knowledge.RecordDeflectionCandidateAsync(Id, ct);
        }
        var mine = await knowledge.MyVoteAsync(Id, Request.Cookies[VisitorCookie], ct);
        Vote = new KbVoteModel(Id, mine?.Helpful, mine?.Comment is not null, SignedIn, false);
        return Page();
    }

    public async Task<IActionResult> OnPostVoteAsync(bool helpful, string? comment, CancellationToken ct)
    {
        var signedIn = (await visibility.ViewerAsync(ct)).SignedIn;
        var key = Request.Cookies[VisitorCookie];
        if (!signedIn && string.IsNullOrEmpty(key))
        {
            key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            Response.Cookies.Append(VisitorCookie, key, new CookieOptions
            {
                HttpOnly = true,
                IsEssential = false,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps,
                MaxAge = TimeSpan.FromDays(365),
            });
        }
        var saved = await knowledge.VoteAsync(Id, helpful, signedIn ? comment : null, signedIn ? null : key, ct);
        var model = new KbVoteModel(Id, saved.Helpful, saved.Comment is not null, signedIn, true);
        if (Request.IsHtmx())
        {
            return Partial("_Vote", model);
        }
        TempData.Toast(saved.Comment is not null && comment is not null ? "Thanks, your comment was sent" : "Thanks for the feedback");
        return Redirect($"/portal/kb/{Id}#kbVote");
    }
}
