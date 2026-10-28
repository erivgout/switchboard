using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Switchboard.Core.Api;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;

namespace Switchboard.Modules.Knowledge.Api;

/// <summary>REST endpoints under <c>/api/v1/kb</c> (FR-INT-1). Same services and visibility as the UI.</summary>
public static class KbApi
{
    public sealed record ArticleSummary(
        Guid Id, string DisplayId, string Title, string Snippet, Guid FolderId, string Folder, string Status, string Type, DateTimeOffset UpdatedAt, int Views);

    public sealed record ArticleList(IReadOnlyList<ArticleSummary> Items, int Total, int Page, int PerPage);

    public sealed record Article(
        Guid Id, string DisplayId, string Title, string BodyHtml, string BodyText, string Status, string Type, Guid FolderId, string Folder,
        IReadOnlyList<string> Tags, string? ExternalUrl, int Version, bool HasUnpublishedChanges, DateTimeOffset UpdatedAt);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var kb = endpoints.MapSwitchboardApi("kb", "Knowledge");

        kb.MapGet("/articles", async (string? q, Guid? folder, string? status, int? page, int? per_page, IKnowledgeService knowledge, IKnowledgeVisibility visibility, CancellationToken ct) =>
        {
            var viewer = await visibility.ViewerAsync(ct);
            KbArticleStatus? parsed = Enum.TryParse<KbArticleStatus>((status ?? "").Replace("_", ""), true, out var s) ? s : null;
            var agent = viewer.IsAnyKbAgent && (parsed is not null and not KbArticleStatus.Published || status == "any");
            var result = await knowledge.SearchAsync(new KbSearchQuery
            {
                Text = q,
                FolderId = folder,
                Mode = agent ? KbSearchMode.Agent : KbSearchMode.Published,
                Status = agent ? parsed : null,
                Page = page ?? 1,
                PageSize = Math.Clamp(per_page ?? 25, 1, 100),
                Sort = string.IsNullOrWhiteSpace(q) ? KbSort.Updated : KbSort.Relevance,
            }, ct);
            return Results.Ok(ToList(result));
        }).WithName("ListKnowledgeArticles").WithSummary("List or filter knowledge articles you can read");

        kb.MapGet("/search", async (string q, int? limit, IKnowledgeService knowledge, CancellationToken ct) =>
        {
            var result = await knowledge.SearchAsync(new KbSearchQuery { Text = q, Mode = KbSearchMode.Published, PageSize = Math.Clamp(limit ?? 10, 1, 50) }, ct);
            return Results.Ok(ToList(result));
        }).WithName("SearchKnowledge").WithSummary("Search published knowledge articles");

        kb.MapGet("/articles/{reference}", async (string reference, IKnowledgeService knowledge, IKnowledgeVisibility visibility, CancellationToken ct) =>
        {
            var id = await knowledge.ResolveAsync(reference, ct) ?? throw new NotFoundException();
            var viewer = await visibility.ViewerAsync(ct);
            if (viewer.IsAnyKbAgent && (await visibility.AgentFolderIdsAsync(ct)).Count > 0)
            {
                try
                {
                    var d = await knowledge.GetForAgentAsync(id, ct);
                    if (d.Article.DeletedAt is null)
                    {
                        return Results.Ok(new Article(d.Article.Id, d.Article.DisplayId, d.Latest.Title, d.Latest.BodyHtml, d.Latest.BodyText,
                            Status(d.Article.Status), Type(d.Article.Type), d.Article.FolderId, d.FolderPath, d.Article.Tags, d.Article.ExternalUrl,
                            d.Latest.Number, d.HasPendingChanges, d.Latest.UpdatedAt));
                    }
                }
                catch (NotFoundException)
                {
                    // Fall through to the published version.
                }
            }
            var p = await knowledge.GetPublishedAsync(id, ct);
            return Results.Ok(new Article(p.Id, p.DisplayId, p.Title, p.BodyHtml, p.BodyText, "published", Type(p.Type), p.Folder.Id, p.FolderPath,
                p.Tags, p.ExternalUrl, p.VersionNumber, false, p.UpdatedAt));
        }).WithName("GetKnowledgeArticle").WithSummary("Get one knowledge article by id or number (KB-1042)");
    }

    private static ArticleList ToList(KbSearchPage page) => new(
        page.Items.Select(i => new ArticleSummary(i.Id, i.DisplayId, i.Title, i.Snippet, i.FolderId, i.FolderPath, Status(i.Status), Type(i.Type), i.UpdatedAt, i.Views)).ToList(),
        page.Total, page.Page, page.PageSize);

    private static string Status(KbArticleStatus s) => s switch
    {
        KbArticleStatus.Draft => "draft",
        KbArticleStatus.InReview => "in_review",
        KbArticleStatus.Published => "published",
        _ => "archived",
    };

    private static string Type(KbArticleType t) => t == KbArticleType.Workaround ? "workaround" : "permanent";
}
