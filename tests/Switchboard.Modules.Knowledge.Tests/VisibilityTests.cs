using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;

namespace Switchboard.Modules.Knowledge.Tests;

/// <summary>INV-KB-2 and FR-KB-1: who can read which folder, on every read path.</summary>
public sealed class VisibilityTests(KbFixture f) : IClassFixture<KbFixture>
{
    private async Task<Dictionary<string, Guid>> PublishOneEverywhereAsync(string tag)
    {
        var folders = new Dictionary<string, Guid>
        {
            ["everyone"] = f.Everyone,
            ["signedin"] = f.SignedIn,
            ["agents"] = f.AgentsOnly,
            ["group"] = f.ForRequesterGroup,
            ["finance"] = f.ForFinance,
            ["hardware"] = f.ForHardware,
            ["sub"] = f.SubFolder,
        };
        var ids = new Dictionary<string, Guid>();
        foreach (var (key, folder) in folders)
        {
            ids[key] = await f.ArticleAsync(folder, $"{tag} {key} article");
        }
        return ids;
    }

    private async Task<HashSet<string>> VisibleKeysAsync(Core.Domain.User? user, string tag, Dictionary<string, Guid> ids)
    {
        var page = await f.SearchAsync(user, tag);
        var found = page.Items.Select(i => i.Id).ToHashSet();
        return ids.Where(kv => found.Contains(kv.Value)).Select(kv => kv.Key).ToHashSet();
    }

    [Fact]
    public async Task Visibility_matrix_holds_for_search()
    {
        var ids = await PublishOneEverywhereAsync("zebracrossing");
        await f.SetPublicAsync(false);

        Assert.Empty(await VisibleKeysAsync(null, "zebracrossing", ids));
        Assert.Equal(["everyone", "signedin"], (await VisibleKeysAsync(f.Requester, "zebracrossing", ids)).Order());
        Assert.Equal(["everyone", "group", "signedin"], (await VisibleKeysAsync(f.GroupRequester, "zebracrossing", ids)).Order());
        Assert.Equal(["everyone", "finance", "signedin"], (await VisibleKeysAsync(f.FinanceRequester, "zebracrossing", ids)).Order());
        // Agents maintain every folder of their workspace except ones restricted to other agent groups.
        Assert.Equal(["agents", "everyone", "finance", "group", "signedin", "sub"], (await VisibleKeysAsync(f.Agent, "zebracrossing", ids)).Order());
        Assert.Contains("hardware", await VisibleKeysAsync(f.HardwareAgent, "zebracrossing", ids));
        Assert.Equal(ids.Keys.Order(), (await VisibleKeysAsync(f.Manager, "zebracrossing", ids)).Order());

        await f.SetPublicAsync(true);
        try
        {
            Assert.Equal(["everyone"], await VisibleKeysAsync(null, "zebracrossing", ids));
        }
        finally
        {
            await f.SetPublicAsync(false);
        }
    }

    [Fact]
    public async Task Article_reads_enforce_folder_visibility()
    {
        var ids = await PublishOneEverywhereAsync("yellowpine");

        await Assert.ThrowsAsync<NotFoundException>(() => f.AsAsync(f.Requester, sp => sp.GetRequiredService<IKnowledgeService>().GetPublishedAsync(ids["agents"])));
        await Assert.ThrowsAsync<NotFoundException>(() => f.AsAsync(f.Requester, sp => sp.GetRequiredService<IKnowledgeService>().GetPublishedAsync(ids["finance"])));
        await Assert.ThrowsAsync<NotFoundException>(() => f.AsAsync(f.Requester, sp => sp.GetRequiredService<IKnowledgeService>().GetPublishedAsync(ids["sub"])));
        await Assert.ThrowsAsync<NotFoundException>(() => f.AnonymousAsync(sp => sp.GetRequiredService<IKnowledgeService>().GetPublishedAsync(ids["everyone"])));
        await Assert.ThrowsAsync<NotFoundException>(() => f.AsAsync(f.Agent, sp => sp.GetRequiredService<IKnowledgeService>().GetForAgentAsync(ids["hardware"])));
        // Requesters never reach the agent read path.
        await Assert.ThrowsAsync<NotFoundException>(() => f.AsAsync(f.Requester, sp => sp.GetRequiredService<IKnowledgeService>().GetForAgentAsync(ids["everyone"])));

        var finance = await f.AsAsync(f.FinanceRequester, sp => sp.GetRequiredService<IKnowledgeService>().GetPublishedAsync(ids["finance"]));
        Assert.Equal("yellowpine finance article", finance.Title);
        var hardware = await f.AsAsync(f.HardwareAgent, sp => sp.GetRequiredService<IKnowledgeService>().GetForAgentAsync(ids["hardware"]));
        Assert.Equal(KbArticleStatus.Published, hardware.Article.Status);
    }

    [Fact]
    public async Task Sub_folders_follow_their_top_level_folder()
    {
        var index = await f.AsAsync(f.Manager, sp => sp.GetRequiredService<IKnowledgeVisibility>().FoldersAsync());
        var sub = index.Get(f.SubFolder)!;
        Assert.Equal(f.AgentsOnly, sub.RootId);
        Assert.Equal(KbFolderVisibility.AgentsOnly, sub.Visibility);
    }

    [Fact]
    public async Task Drafts_reviews_and_archived_articles_are_never_shown_to_requesters()
    {
        var draft = await f.ArticleAsync(f.Everyone, "quokkadraft unpublished", publish: false);
        var archived = await f.ArticleAsync(f.Everyone, "quokkadraft archived");
        await f.AsAsync(f.Supervisor, sp => sp.GetRequiredService<IKnowledgeService>().ArchiveAsync(archived));
        var inReview = await f.ArticleAsync(f.NeedsApproval, "quokkadraft waiting", publish: false);
        await f.AsAsync(f.Agent, sp => sp.GetRequiredService<IKnowledgeService>().SubmitForReviewAsync(inReview, null));

        foreach (var user in new[] { f.Requester, f.GroupRequester, f.FinanceRequester })
        {
            Assert.Empty((await f.SearchAsync(user, "quokkadraft")).Items);
            foreach (var id in new[] { draft, archived, inReview })
            {
                await Assert.ThrowsAsync<NotFoundException>(() => f.AsAsync(user, sp => sp.GetRequiredService<IKnowledgeService>().GetPublishedAsync(id)));
            }
            Assert.Empty(await f.AsAsync(user, sp => sp.GetRequiredService<IKnowledgeService>().SuggestAsync("quokkadraft waiting")));
        }

        // Agents see all three in the agent console.
        var agentView = await f.SearchAsync(f.Agent, "quokkadraft", KbSearchMode.Agent);
        Assert.Equal(3, agentView.Items.Count);
        // Even agents get only published articles on published read paths (portal, suggestions).
        Assert.Empty((await f.SearchAsync(f.Agent, "quokkadraft")).Items);
    }

    [Fact]
    public async Task Requesters_read_the_published_version_while_a_newer_draft_exists()
    {
        var id = await f.ArticleAsync(f.Everyone, "Reset the badge printer", "<p>Hold the power button for ten seconds.</p>");
        await f.AsAsync(f.Agent, sp => sp.GetRequiredService<IKnowledgeService>().SaveDraftAsync(id, new KbDraftInput
        {
            FolderId = f.Everyone,
            Title = "Reset the badge printer (secret new title)",
            BodyHtml = "<p>Unreleased steps mentioning flamingoprotocol.</p>",
        }));

        var published = await f.AsAsync(f.Requester, sp => sp.GetRequiredService<IKnowledgeService>().GetPublishedAsync(id));
        Assert.Equal("Reset the badge printer", published.Title);
        Assert.DoesNotContain("flamingoprotocol", published.BodyHtml);
        Assert.Empty((await f.SearchAsync(f.Requester, "flamingoprotocol")).Items);
        Assert.Single((await f.SearchAsync(f.Agent, "flamingoprotocol", KbSearchMode.Agent)).Items);
    }

    [Fact]
    public async Task Suggestions_only_offer_visible_published_articles()
    {
        await f.ArticleAsync(f.Everyone, "Wombat scanner jams on thick paper");
        await f.ArticleAsync(f.AgentsOnly, "Wombat scanner internal service manual");
        await f.ArticleAsync(f.ForFinance, "Wombat scanner for invoices");
        await f.ArticleAsync(f.Everyone, "Wombat scanner draft notes", publish: false);

        var requester = await f.AsAsync(f.Requester, sp => sp.GetRequiredService<IKnowledgeService>().SuggestAsync("my wombat scanner keeps jamming"));
        Assert.Equal(["Wombat scanner jams on thick paper"], requester.Select(h => h.Title));

        var finance = await f.AsAsync(f.FinanceRequester, sp => sp.GetRequiredService<IKnowledgeService>().SuggestAsync("wombat scanner"));
        Assert.Equal(2, finance.Count);
        Assert.DoesNotContain(finance, h => h.Title.Contains("internal"));

        await f.SetPublicAsync(false);
        Assert.Empty(await f.AnonymousAsync(sp => sp.GetRequiredService<IKnowledgeService>().SuggestAsync("wombat scanner")));
    }

    [Fact]
    public async Task Search_ranks_title_matches_above_body_matches()
    {
        await f.ArticleAsync(f.Everyone, "Laptop docking basics", "<p>If the kangaroomonitor stays black, reseat the cable.</p>");
        await f.ArticleAsync(f.Everyone, "Kangaroomonitor flickers", "<p>Lower the refresh rate.</p>");

        var page = await f.SearchAsync(f.Requester, "kangaroomonitor");
        Assert.Equal(["Kangaroomonitor flickers", "Laptop docking basics"], page.Items.Select(i => i.Title));
        Assert.Contains("kangaroomonitor", page.Items[1].Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Trashed_articles_and_folders_disappear_from_every_read_path()
    {
        var article = await f.ArticleAsync(f.Everyone, "Narwhal keyboard pairing");
        await f.AsAsync(f.Supervisor, sp => sp.GetRequiredService<IKnowledgeService>().TrashAsync([article]));
        Assert.Empty((await f.SearchAsync(f.Requester, "narwhal")).Items);
        Assert.Empty((await f.SearchAsync(f.Agent, "narwhal", KbSearchMode.Agent)).Items);

        // Only knowledge managers delete permanently.
        await Assert.ThrowsAsync<ForbiddenException>(() => f.AsAsync(f.Supervisor, sp => sp.GetRequiredService<IKnowledgeService>().PurgeAsync([article])));
        await f.AsAsync(f.Supervisor, sp => sp.GetRequiredService<IKnowledgeService>().RestoreAsync([article]));
        Assert.Single((await f.SearchAsync(f.Requester, "narwhal")).Items);

        // Trashing the folder hides everything in it, and restoring brings it back.
        var folder = await f.AsAsync(f.Manager, async sp =>
        {
            var index = await sp.GetRequiredService<IKnowledgeVisibility>().FoldersAsync();
            return await sp.GetRequiredService<IKbStructureService>().CreateFolderAsync(new KbFolderInput
            {
                CategoryId = index.Get(f.Everyone)!.CategoryId,
                Name = "Temporary",
            });
        });
        var inFolder = await f.ArticleAsync(folder.Id, "Narwhal mouse pairing");
        await f.AsAsync(f.Manager, sp => sp.GetRequiredService<IKbStructureService>().TrashAsync(KbStructureService.Folder, [folder.Id]));
        Assert.DoesNotContain((await f.SearchAsync(f.Requester, "narwhal")).Items, i => i.Id == inFolder);
        await Assert.ThrowsAsync<NotFoundException>(() => f.AsAsync(f.Requester, sp => sp.GetRequiredService<IKnowledgeService>().GetPublishedAsync(inFolder)));

        await f.AsAsync(f.Manager, sp => sp.GetRequiredService<IKbStructureService>().PurgeAsync(KbStructureService.Folder, [folder.Id]));
        await Assert.ThrowsAsync<NotFoundException>(() => f.AsAsync(f.Manager, sp => sp.GetRequiredService<IKnowledgeService>().GetForAgentAsync(inFolder)));
    }

    [Fact]
    public async Task Requesters_cannot_write()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => f.AsAsync(f.Requester, sp => sp.GetRequiredService<IKnowledgeService>()
            .CreateDraftAsync(new KbDraftInput { FolderId = f.Everyone, Title = "Sneaky", BodyHtml = "<p>x</p>" })));
        var id = await f.ArticleAsync(f.Everyone, "Owl projector setup");
        await Assert.ThrowsAsync<NotFoundException>(() => f.AsAsync(f.Requester, sp => sp.GetRequiredService<IKnowledgeService>()
            .SaveDraftAsync(id, new KbDraftInput { FolderId = f.Everyone, Title = "Changed" })));
        await Assert.ThrowsAsync<NotFoundException>(() => f.AsAsync(f.Requester, sp => sp.GetRequiredService<IKnowledgeService>().PublishAsync(id)));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.AsAsync(f.Agent, sp => sp.GetRequiredService<IKbStructureService>()
            .CreateCategoryAsync(f.Host.Workspace.Id, "Not allowed", null)));
    }
}
