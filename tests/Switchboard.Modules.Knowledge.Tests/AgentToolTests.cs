using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Agents;
using Switchboard.Core.Domain;
using Switchboard.Modules.Knowledge.Services;

namespace Switchboard.Modules.Knowledge.Tests;

/// <summary>Appendix B tools run as the delegating user, with the same visibility as the UI.</summary>
public sealed class AgentToolTests(KbFixture f) : IClassFixture<KbFixture>
{
    private Task<AgentToolResult> Call(User user, string tool, JsonObject args, bool dryRun = false) =>
        f.AsAsync(user, sp => sp.GetServices<IAgentTool>().Single(t => t.Definition.Name == tool)
            .InvokeAsync(args, new AgentToolContext(DryRun: dryRun), CancellationToken.None));

    [Fact]
    public async Task Catalog_matches_appendix_b()
    {
        var tools = await f.Host.RunAsSystemAsync(sp => Task.FromResult(sp.GetServices<IAgentTool>().Select(t => t.Definition).ToList()));
        var byName = tools.ToDictionary(t => t.Name);
        Assert.Equal(("kb:read", AgentActionClass.Read, true), (byName["search_knowledge"].Scope, byName["search_knowledge"].Class, byName["search_knowledge"].RequesterAllowed));
        Assert.Equal(("kb:read", AgentActionClass.Read, true), (byName["get_article"].Scope, byName["get_article"].Class, byName["get_article"].RequesterAllowed));
        foreach (var name in new[] { "create_article_draft", "update_article_draft", "submit_article_for_review" })
        {
            Assert.Equal(("kb:write", AgentActionClass.InternalWrite, false), (byName[name].Scope, byName[name].Class, byName[name].RequesterAllowed));
        }
        Assert.Equal(("kb:write", AgentActionClass.Authority), (byName["publish_article"].Scope, byName["publish_article"].Class));
    }

    [Fact]
    public async Task Search_and_get_enforce_visibility_and_mark_content_untrusted()
    {
        var open = await f.ArticleAsync(f.Everyone, "Pelican printer toner swap", "<p>Lift the blue lever.</p>");
        var internalOnly = await f.ArticleAsync(f.AgentsOnly, "Pelican printer service codes");
        var draft = await f.ArticleAsync(f.Everyone, "Pelican printer firmware", publish: false);

        var search = await Call(f.Requester, "search_knowledge", new JsonObject { ["query"] = "pelican printer", ["status"] = "any" });
        Assert.False(search.IsError);
        var results = search.Structured!["results"]!.AsArray();
        var hit = Assert.Single(results);
        Assert.Equal(open.ToString(), hit!["id"]!.GetValue<string>());
        Assert.Equal("untrusted", hit["title"]!["trust"]!.GetValue<string>());
        Assert.Equal("Pelican printer toner swap", hit["title"]!["value"]!.GetValue<string>());

        Assert.Equal("not_found", (await Call(f.Requester, "get_article", new JsonObject { ["article"] = internalOnly.ToString() })).ErrorCode);
        Assert.Equal("not_found", (await Call(f.Requester, "get_article", new JsonObject { ["article"] = draft.ToString() })).ErrorCode);
        var article = await Call(f.Requester, "get_article", new JsonObject { ["article"] = open.ToString() });
        Assert.Equal("Lift the blue lever.", article.Structured!["body"]!["value"]!.GetValue<string>());
        Assert.Equal("untrusted", article.Structured["body"]!["trust"]!.GetValue<string>());

        // Agents can ask for every status and read drafts.
        var agentSearch = await Call(f.Agent, "search_knowledge", new JsonObject { ["query"] = "pelican printer", ["status"] = "any" });
        Assert.Equal(3, agentSearch.Structured!["results"]!.AsArray().Count);
        var agentDraft = await Call(f.Agent, "get_article", new JsonObject { ["article"] = draft.ToString() });
        Assert.Equal("draft", agentDraft.Structured!["status"]!.GetValue<string>());
        Assert.Equal("not_found", (await Call(f.Agent, "get_article", new JsonObject { ["article"] = "KB-99999999" })).ErrorCode);
    }

    [Fact]
    public async Task Write_tools_follow_privileges_and_approval()
    {
        var created = await Call(f.Agent, "create_article_draft", new JsonObject
        {
            ["folder_id"] = f.NeedsApproval.ToString(),
            ["title"] = "Clean desk policy",
            ["body_html"] = "<p>Lock your screen.</p><script>x()</script>",
            ["tags"] = new JsonArray("policy"),
        });
        Assert.False(created.IsError, created.Text);
        var reference = created.Structured!["display_id"]!.GetValue<string>();

        var updated = await Call(f.Agent, "update_article_draft", new JsonObject { ["article"] = reference, ["body_html"] = "<p>Lock your screen and clear your desk.</p>" });
        Assert.False(updated.IsError, updated.Text);

        // Requesters can't write, and drafts don't appear to them.
        Assert.Equal("permission_denied", (await Call(f.Requester, "create_article_draft", new JsonObject
        {
            ["folder_id"] = f.Everyone.ToString(), ["title"] = "x", ["body_html"] = "<p>x</p>",
        })).ErrorCode);
        Assert.Equal("not_found", (await Call(f.Requester, "update_article_draft", new JsonObject { ["article"] = reference, ["title"] = "Hijacked" })).ErrorCode);

        // Approval folder: the author's agent can't publish, the folder approver can.
        Assert.Equal("permission_denied", (await Call(f.Agent, "publish_article", new JsonObject { ["article"] = reference })).ErrorCode);
        Assert.False((await Call(f.Agent, "submit_article_for_review", new JsonObject { ["article"] = reference })).IsError);
        Assert.Equal("permission_denied", (await Call(f.Agent, "publish_article", new JsonObject { ["article"] = reference })).ErrorCode);

        var description = await f.AsAsync(f.Supervisor, sp => sp.GetServices<IAgentTool>().Single(t => t.Definition.Name == "publish_article")
            .DescribeAsync(new JsonObject { ["article"] = reference }, CancellationToken.None));
        Assert.Contains("Approve and publish", description);

        var approved = await Call(f.Supervisor, "publish_article", new JsonObject { ["article"] = reference, ["comment"] = "Fine" });
        Assert.False(approved.IsError, approved.Text);

        var read = await Call(f.Requester, "get_article", new JsonObject { ["article"] = reference });
        Assert.Equal("Lock your screen and clear your desk.", read.Structured!["body"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Dry_run_changes_nothing()
    {
        var result = await Call(f.Agent, "create_article_draft", new JsonObject
        {
            ["folder_id"] = f.Everyone.ToString(), ["title"] = "Ghost article", ["body_html"] = "<p>Boo.</p>",
        }, dryRun: true);
        Assert.False(result.IsError);
        Assert.Empty((await f.SearchAsync(f.Agent, "ghost article", KbSearchMode.Agent)).Items);
    }
}
