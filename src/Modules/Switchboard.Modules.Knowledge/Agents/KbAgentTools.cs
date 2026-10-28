using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Agents;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;

namespace Switchboard.Modules.Knowledge.Agents;

/// <summary>Shared plumbing: error mapping and links. Article text is always returned as untrusted data (MCP-60).</summary>
public abstract class KbToolBase(IKnowledgeService knowledge, IKnowledgeVisibility visibility, IOptions<SwitchboardOptions> options) : IAgentTool
{
    protected IKnowledgeService Knowledge => knowledge;
    protected IKnowledgeVisibility Visibility => visibility;

    public abstract AgentToolDefinition Definition { get; }

    public async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        try
        {
            return await RunAsync(arguments, context, ct);
        }
        catch (NotFoundException)
        {
            return AgentToolResult.Error("not_found", "No article matches that reference, or you can't see it.");
        }
        catch (ForbiddenException)
        {
            return AgentToolResult.Error("permission_denied", "You don't have permission to do that.");
        }
        catch (ConflictException ex)
        {
            return AgentToolResult.Error("conflict", ex.Message);
        }
        catch (ValidationException ex)
        {
            var details = new JsonObject();
            foreach (var (field, messages) in ex.Errors)
            {
                details[field] = new JsonArray(messages.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
            }
            return AgentToolResult.Error("validation_failed", "The request isn't valid. See details.", details);
        }
    }

    protected abstract Task<AgentToolResult> RunAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct);

    public virtual Task<string> DescribeAsync(JsonObject arguments, CancellationToken ct) =>
        Task.FromResult($"{Definition.Title}: {Str(arguments, "article") ?? Str(arguments, "title") ?? ""}".TrimEnd(' ', ':'));

    protected string AgentUrl(Guid id) => $"{options.Value.BaseUrl.TrimEnd('/')}/kb/{id}";
    protected string PortalUrl(Guid id) => $"{options.Value.BaseUrl.TrimEnd('/')}/portal/kb/{id}";

    protected static string? Str(JsonObject args, string name) =>
        args.TryGetPropertyValue(name, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    protected static int? Int(JsonObject args, string name) =>
        args.TryGetPropertyValue(name, out var node) && node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    protected static IReadOnlyList<string>? StrList(JsonObject args, string name) =>
        args.TryGetPropertyValue(name, out var node) && node is JsonArray a
            ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList()
            : null;

    protected async Task<Guid> ResolveAsync(JsonObject args, CancellationToken ct) =>
        await Knowledge.ResolveAsync(Str(args, "article") ?? "", ct) ?? throw new NotFoundException();

    protected static JsonArray Tags(IEnumerable<string> tags) =>
        new(tags.Select(t => (JsonNode)AgentContent.Untrusted(t, "kb_article.tag")).ToArray());
}

public sealed class SearchKnowledgeTool(IKnowledgeService knowledge, IKnowledgeVisibility visibility, IOptions<SwitchboardOptions> options)
    : KbToolBase(knowledge, visibility, options)
{
    public override AgentToolDefinition Definition { get; } = new(
        "search_knowledge",
        "Search knowledge articles",
        "Searches knowledge base articles the user can read and returns matches ranked by relevance, with a short snippet. "
        + "Requesters only get published articles. Agents may pass status \"any\" to include drafts, articles in review and archived articles. "
        + "Use get_article to read one in full.",
        "kb:read",
        AgentActionClass.Read,
        AgentContent.ObjectSchema(["query"],
            ("query", AgentContent.StringSchema("Words to search for, e.g. \"vpn disconnects\" or an article number like KB-1042.", 200)),
            ("status", AgentContent.EnumSchema("published (default) or any (agents only).", "published", "any")),
            ("limit", AgentContent.IntegerSchema("Maximum results (default 10).", 1, 25))),
        RequesterAllowed: true);

    protected override async Task<AgentToolResult> RunAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var viewer = await Visibility.ViewerAsync(ct);
        var any = Str(arguments, "status") == "any" && viewer.IsAnyKbAgent;
        var page = await Knowledge.SearchAsync(new KbSearchQuery
        {
            Text = Str(arguments, "query") ?? "",
            Mode = any ? KbSearchMode.Agent : KbSearchMode.Published,
            PageSize = Math.Clamp(Int(arguments, "limit") ?? 10, 1, 25),
        }, ct);

        var results = new JsonArray(page.Items.Select(i => (JsonNode)new JsonObject
        {
            ["id"] = i.Id.ToString(),
            ["display_id"] = i.DisplayId,
            ["title"] = AgentContent.Untrusted(i.Title, "kb_article.title"),
            ["snippet"] = AgentContent.Untrusted(i.Snippet, "kb_article.body"),
            ["folder"] = AgentContent.Untrusted(i.FolderPath, "kb_folder.name"),
            ["status"] = StatusName(i.Status),
            ["type"] = i.Type == KbArticleType.Workaround ? "workaround" : "permanent",
            ["updated_at"] = i.UpdatedAt.ToString("O"),
            ["url"] = any ? AgentUrl(i.Id) : PortalUrl(i.Id),
        }).ToArray());
        return AgentToolResult.Ok(new JsonObject { ["results"] = results, ["total"] = page.Total },
            page.Total == 0 ? "No articles matched." : $"Found {page.Total} article{(page.Total == 1 ? "" : "s")}.");
    }

    internal static string StatusName(KbArticleStatus status) => status switch
    {
        KbArticleStatus.Draft => "draft",
        KbArticleStatus.InReview => "in_review",
        KbArticleStatus.Published => "published",
        _ => "archived",
    };
}

public sealed class GetArticleTool(IKnowledgeService knowledge, IKnowledgeVisibility visibility, IOptions<SwitchboardOptions> options)
    : KbToolBase(knowledge, visibility, options)
{
    public override AgentToolDefinition Definition { get; } = new(
        "get_article",
        "Read a knowledge article",
        "Returns one knowledge base article as plain text with its metadata. Accepts an id or a number like KB-1042. "
        + "Requesters get the published version. Agents get the latest version, which may be an unpublished draft (see status and has_unpublished_changes).",
        "kb:read",
        AgentActionClass.Read,
        AgentContent.ObjectSchema(["article"],
            ("article", AgentContent.StringSchema("Article id or number, e.g. KB-1042.", 64))),
        RequesterAllowed: true);

    protected override async Task<AgentToolResult> RunAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var id = await ResolveAsync(arguments, ct);
        var agentFolders = await Visibility.AgentFolderIdsAsync(ct);
        var viewer = await Visibility.ViewerAsync(ct);
        if (viewer.IsAnyKbAgent)
        {
            try
            {
                var detail = await Knowledge.GetForAgentAsync(id, ct);
                if (detail.Article.DeletedAt is null && agentFolders.Contains(detail.Article.FolderId))
                {
                    var a = detail.Article;
                    var result = new JsonObject
                    {
                        ["id"] = a.Id.ToString(),
                        ["display_id"] = a.DisplayId,
                        ["title"] = AgentContent.Untrusted(detail.Latest.Title, "kb_article.title", detail.AuthorName),
                        ["body"] = AgentContent.Untrusted(detail.Latest.BodyText, "kb_article.body", detail.AuthorName),
                        ["status"] = SearchKnowledgeTool.StatusName(a.Status),
                        ["has_unpublished_changes"] = detail.HasPendingChanges,
                        ["version"] = detail.Latest.Number,
                        ["revision"] = detail.Latest.Revision,
                        ["type"] = a.Type == KbArticleType.Workaround ? "workaround" : "permanent",
                        ["folder_id"] = a.FolderId.ToString(),
                        ["folder"] = AgentContent.Untrusted(detail.FolderPath, "kb_folder.name"),
                        ["tags"] = Tags(a.Tags),
                        ["external_url"] = a.ExternalUrl,
                        ["review_by"] = a.ReviewBy?.ToString("yyyy-MM-dd"),
                        ["updated_at"] = detail.Latest.UpdatedAt.ToString("O"),
                        ["url"] = AgentUrl(a.Id),
                    };
                    return AgentToolResult.Ok(result, $"{a.DisplayId}, {SearchKnowledgeTool.StatusName(a.Status).Replace('_', ' ')}.");
                }
            }
            catch (NotFoundException)
            {
                // Not workable as an agent here; fall back to the published version below.
            }
        }

        var article = await Knowledge.GetPublishedAsync(id, ct);
        return AgentToolResult.Ok(new JsonObject
        {
            ["id"] = article.Id.ToString(),
            ["display_id"] = article.DisplayId,
            ["title"] = AgentContent.Untrusted(article.Title, "kb_article.title", article.AuthorName),
            ["body"] = AgentContent.Untrusted(article.BodyText, "kb_article.body", article.AuthorName),
            ["status"] = "published",
            ["version"] = article.VersionNumber,
            ["type"] = article.Type == KbArticleType.Workaround ? "workaround" : "permanent",
            ["folder"] = AgentContent.Untrusted(article.FolderPath, "kb_folder.name"),
            ["tags"] = Tags(article.Tags),
            ["external_url"] = article.ExternalUrl,
            ["updated_at"] = article.UpdatedAt.ToString("O"),
            ["url"] = PortalUrl(article.Id),
        }, $"{article.DisplayId}, published.");
    }
}

public sealed class CreateArticleDraftTool(IKnowledgeService knowledge, IKnowledgeVisibility visibility, IOptions<SwitchboardOptions> options)
    : KbToolBase(knowledge, visibility, options)
{
    public override AgentToolDefinition Definition { get; } = new(
        "create_article_draft",
        "Create a knowledge article draft",
        "Creates a new knowledge base article as an unpublished draft in a folder. Nobody outside the service team sees it until it is published. "
        + "The body is HTML (headings, paragraphs, lists, links, code); unsafe markup is removed.",
        "kb:write",
        AgentActionClass.InternalWrite,
        AgentContent.ObjectSchema(["folder_id", "title", "body_html"],
            ("folder_id", AgentContent.StringSchema("Id of the folder to create the article in.", 64)),
            ("title", AgentContent.StringSchema("Article title, phrased the way people search, e.g. \"Connect to the VPN from home\".", 300)),
            ("body_html", AgentContent.StringSchema("Article body as HTML.", 200_000)),
            ("type", AgentContent.EnumSchema("permanent (default) or workaround.", "permanent", "workaround")),
            ("tags", new JsonObject { ["type"] = "array", ["items"] = AgentContent.StringSchema("Tag", 40), ["maxItems"] = 20 })));

    protected override async Task<AgentToolResult> RunAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        if (!Guid.TryParse(Str(arguments, "folder_id"), out var folderId))
        {
            throw new ValidationException("folder_id", "Pass the folder's id.");
        }
        var input = new KbDraftInput
        {
            FolderId = folderId,
            Title = Str(arguments, "title") ?? "",
            BodyHtml = Str(arguments, "body_html"),
            Type = Str(arguments, "type") == "workaround" ? KbArticleType.Workaround : KbArticleType.Permanent,
            Tags = StrList(arguments, "tags"),
            ChangeNote = "Drafted by an AI agent",
        };
        if (context.DryRun)
        {
            return AgentToolResult.Ok(new JsonObject { ["would_create"] = true, ["folder_id"] = folderId.ToString() }, "Would create a draft.");
        }
        var saved = await Knowledge.CreateDraftAsync(input, ct);
        return AgentToolResult.Ok(new JsonObject
        {
            ["id"] = saved.ArticleId.ToString(),
            ["display_id"] = KbArticle.FormatDisplayId(saved.Number),
            ["status"] = "draft",
            ["revision"] = saved.Revision,
            ["url"] = AgentUrl(saved.ArticleId),
        }, $"Created draft {KbArticle.FormatDisplayId(saved.Number)}.");
    }
}

public sealed class UpdateArticleDraftTool(IKnowledgeService knowledge, IKnowledgeVisibility visibility, IOptions<SwitchboardOptions> options)
    : KbToolBase(knowledge, visibility, options)
{
    public override AgentToolDefinition Definition { get; } = new(
        "update_article_draft",
        "Update a knowledge article draft",
        "Changes the draft of a knowledge base article. Editing a published article creates a new draft; readers keep seeing the published version "
        + "until it is published. Omitted fields stay as they are. Pass expected_revision from get_article to avoid overwriting someone else's edit.",
        "kb:write",
        AgentActionClass.InternalWrite,
        AgentContent.ObjectSchema(["article"],
            ("article", AgentContent.StringSchema("Article id or number, e.g. KB-1042.", 64)),
            ("title", AgentContent.StringSchema("New title.", 300)),
            ("body_html", AgentContent.StringSchema("New body as HTML; replaces the whole body.", 200_000)),
            ("tags", new JsonObject { ["type"] = "array", ["items"] = AgentContent.StringSchema("Tag", 40), ["maxItems"] = 20 }),
            ("expected_revision", AgentContent.IntegerSchema("Revision returned by get_article.", 0))),
        Idempotent: true);

    protected override async Task<AgentToolResult> RunAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var id = await ResolveAsync(arguments, ct);
        var detail = await Knowledge.GetForAgentAsync(id, ct);
        var a = detail.Article;
        var input = new KbDraftInput
        {
            FolderId = a.FolderId,
            Title = Str(arguments, "title") ?? detail.Latest.Title,
            BodyHtml = Str(arguments, "body_html") ?? detail.Latest.BodyHtml,
            Type = a.Type,
            Tags = StrList(arguments, "tags") ?? a.Tags,
            Keywords = a.Keywords,
            OwnerId = a.OwnerId,
            ReviewBy = a.ReviewBy,
            ExternalUrl = a.ExternalUrl,
            SeoTitle = a.SeoTitle,
            SeoDescription = a.SeoDescription,
            ExpectedRevision = Int(arguments, "expected_revision") is { } r && detail.Latest.State == KbVersionState.Draft ? r : null,
        };
        if (context.DryRun)
        {
            return AgentToolResult.Ok(new JsonObject { ["would_update"] = a.DisplayId }, $"Would update the draft of {a.DisplayId}.");
        }
        var saved = await Knowledge.SaveDraftAsync(id, input, ct);
        return AgentToolResult.Ok(new JsonObject
        {
            ["id"] = saved.ArticleId.ToString(),
            ["display_id"] = a.DisplayId,
            ["version"] = saved.VersionNumber,
            ["revision"] = saved.Revision,
            ["url"] = AgentUrl(saved.ArticleId),
        }, $"Saved the draft of {a.DisplayId}.");
    }
}

public sealed class SubmitArticleForReviewTool(IKnowledgeService knowledge, IKnowledgeVisibility visibility, IOptions<SwitchboardOptions> options)
    : KbToolBase(knowledge, visibility, options)
{
    public override AgentToolDefinition Definition { get; } = new(
        "submit_article_for_review",
        "Submit a knowledge article for review",
        "Sends an article's draft to its folder's approvers. The draft becomes read-only until it is approved, rejected or withdrawn.",
        "kb:write",
        AgentActionClass.InternalWrite,
        AgentContent.ObjectSchema(["article"],
            ("article", AgentContent.StringSchema("Article id or number, e.g. KB-1042.", 64)),
            ("note", AgentContent.StringSchema("Optional note for the approvers.", 2000))));

    protected override async Task<AgentToolResult> RunAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var id = await ResolveAsync(arguments, ct);
        if (context.DryRun)
        {
            return AgentToolResult.Ok(new JsonObject { ["would_submit"] = id.ToString() }, "Would submit the draft for review.");
        }
        await Knowledge.SubmitForReviewAsync(id, Str(arguments, "note"), ct);
        return AgentToolResult.Ok(new JsonObject { ["id"] = id.ToString(), ["status"] = "in_review", ["url"] = AgentUrl(id) }, "Submitted for review.");
    }
}

public sealed class PublishArticleTool(IKnowledgeService knowledge, IKnowledgeVisibility visibility, IOptions<SwitchboardOptions> options)
    : KbToolBase(knowledge, visibility, options)
{
    public override AgentToolDefinition Definition { get; } = new(
        "publish_article",
        "Publish a knowledge article",
        "Makes an article's latest draft the version readers see. In folders that require approval, this approves the pending review instead, "
        + "which only the folder's approvers can do (and never for their own submission).",
        "kb:write",
        AgentActionClass.Authority,
        AgentContent.ObjectSchema(["article"],
            ("article", AgentContent.StringSchema("Article id or number, e.g. KB-1042.", 64)),
            ("comment", AgentContent.StringSchema("Optional approval comment or change note.", 2000))));

    protected override async Task<AgentToolResult> RunAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var id = await ResolveAsync(arguments, ct);
        var detail = await Knowledge.GetForAgentAsync(id, ct);
        var approving = detail.PendingReview is not null;
        if (context.DryRun)
        {
            return AgentToolResult.Ok(new JsonObject { ["would_publish"] = detail.Article.DisplayId, ["via_approval"] = approving },
                approving ? "Would approve and publish." : "Would publish.");
        }
        if (approving)
        {
            await Knowledge.ApproveAsync(id, Str(arguments, "comment"), ct);
        }
        else
        {
            await Knowledge.PublishAsync(id, Str(arguments, "comment"), ct);
        }
        return AgentToolResult.Ok(new JsonObject { ["id"] = id.ToString(), ["status"] = "published", ["url"] = PortalUrl(id) },
            $"Published {detail.Article.DisplayId}.");
    }

    public override async Task<string> DescribeAsync(JsonObject arguments, CancellationToken ct)
    {
        var id = await ResolveAsync(arguments, ct);
        var detail = await Knowledge.GetForAgentAsync(id, ct);
        var verb = detail.PendingReview is not null ? "Approve and publish" : "Publish";
        return $"{verb} {detail.Article.DisplayId} \"{detail.Latest.Title}\" (version {detail.Latest.Number}) in {detail.FolderPath}. "
            + "Everyone who can read that folder will see this version.";
    }
}
