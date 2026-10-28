using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Agents;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Testing;

namespace Switchboard.Modules.Catalog.Tests;

/// <summary>Catalog and approval tools run as the delegating user, through the same services, so permissions hold.</summary>
public sealed class AgentToolTests(CatalogFixture f) : IClassFixture<CatalogFixture>
{
    private Task<AgentToolResult> CallAsync(User user, string tool, JsonObject args) =>
        f.Host.RunAsync(SwitchboardTestHost.As(user, Channel.Mcp), sp =>
            sp.GetServices<IAgentTool>().Single(t => t.Definition.Name == tool).InvokeAsync(args, new AgentToolContext(), default));

    [Fact]
    public void Tools_declare_the_catalog_scopes_and_classes()
    {
        using var scope = f.Host.Services.CreateScope();
        var tools = scope.ServiceProvider.GetServices<IAgentTool>().ToDictionary(t => t.Definition.Name, t => t.Definition);
        Assert.Equal(("catalog:read", AgentActionClass.Read), (tools["search_catalog"].Scope, tools["search_catalog"].Class));
        Assert.Equal(("catalog:read", AgentActionClass.Read), (tools["get_catalog_item"].Scope, tools["get_catalog_item"].Class));
        Assert.Equal(("catalog:request", AgentActionClass.InternalWrite), (tools["submit_request"].Scope, tools["submit_request"].Class));
        Assert.Equal(("approvals:read", AgentActionClass.Read), (tools["list_my_approvals"].Scope, tools["list_my_approvals"].Class));
        Assert.Equal(("approvals:read", AgentActionClass.Read), (tools["get_approval"].Scope, tools["get_approval"].Class));
        Assert.Equal(("approvals:decide", AgentActionClass.Authority), (tools["decide_approval"].Scope, tools["decide_approval"].Class));
        Assert.All(tools.Values, t => Assert.True(t.RequesterAllowed));
    }

    [Fact]
    public async Task Search_and_get_respect_the_audience()
    {
        var outsider = await CallAsync(f.Outsider, "search_catalog", new JsonObject { ["query"] = "Budget" });
        Assert.Empty(outsider.Structured!["items"]!.AsArray());
        var member = await CallAsync(f.Requester, "search_catalog", new JsonObject { ["query"] = "Budget" });
        var item = Assert.Single(member.Structured!["items"]!.AsArray());
        // Catalog text comes back labelled as untrusted data.
        Assert.Equal("untrusted", item!["name"]!["trust"]!.GetValue<string>());

        var hidden = await CallAsync(f.Outsider, "get_catalog_item", new JsonObject { ["item_id"] = f.FinanceOnlyId.ToString() });
        Assert.Equal("not_found", hidden.ErrorCode);

        var form = await CallAsync(f.Outsider, "get_catalog_item", new JsonObject { ["item_id"] = f.MonitorId.ToString() });
        var size = form.Structured!["fields"]!.AsArray().First(x => x!["key"]!.GetValue<string>() == "size")!;
        Assert.Equal("dropdown", size["type"]!.GetValue<string>());
        Assert.True(size["required"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Submit_validates_strictly_and_creates_the_request()
    {
        var bad = await CallAsync(f.Outsider, "submit_request", new JsonObject
        {
            ["item_id"] = f.MonitorId.ToString(),
            ["fields"] = new JsonObject { ["size"] = "24-inch", ["colour"] = "red" },
        });
        Assert.Equal("validation_failed", bad.ErrorCode);
        Assert.NotNull(bad.Structured!["errors"]!["f.colour"]);

        var ok = await CallAsync(f.Outsider, "submit_request", new JsonObject
        {
            ["item_id"] = f.MonitorId.ToString(),
            ["quantity"] = 2,
            ["fields"] = new JsonObject { ["size"] = "27-inch" },
        });
        Assert.False(ok.IsError, ok.Text);
        Assert.StartsWith("SR-", ok.Structured!["display_id"]!.GetValue<string>());
        Assert.Equal("not_required", ok.Structured["approval_status"]!.GetValue<string>());

        // A requester can't use requester_id to file for someone else.
        var impersonate = await CallAsync(f.Outsider, "submit_request", new JsonObject
        {
            ["item_id"] = f.MonitorId.ToString(),
            ["requester_id"] = f.Requester.Id.ToString(),
            ["fields"] = new JsonObject { ["size"] = "27-inch" },
        });
        Assert.Equal("permission_denied", impersonate.ErrorCode);
    }

    [Fact]
    public async Task Approval_tools_only_let_approvers_decide()
    {
        var submitted = await CallAsync(f.Requester, "submit_request", new JsonObject
        {
            ["item_id"] = f.LaptopId.ToString(),
            ["fields"] = new JsonObject { ["operating_system"] = "Windows" },
        });
        var approvalId = submitted.Structured!["approval_id"]!.GetValue<string>();

        var pending = await CallAsync(f.Manager, "list_my_approvals", new JsonObject());
        Assert.Contains(pending.Structured!["items"]!.AsArray(), i => i!["id"]!.GetValue<string>() == approvalId);

        Assert.Equal("not_found", (await CallAsync(f.Outsider, "get_approval", new JsonObject { ["approval_id"] = approvalId })).ErrorCode);
        Assert.Equal("not_found", (await CallAsync(f.Outsider, "decide_approval", new JsonObject { ["approval_id"] = approvalId, ["decision"] = "approve" })).ErrorCode);
        // The requester can see it but not approve it (SR-APR-1, also over MCP).
        Assert.Equal("permission_denied", (await CallAsync(f.Requester, "decide_approval", new JsonObject { ["approval_id"] = approvalId, ["decision"] = "approve" })).ErrorCode);
        // Rejecting needs a reason.
        Assert.Equal("validation_failed", (await CallAsync(f.Manager, "decide_approval", new JsonObject { ["approval_id"] = approvalId, ["decision"] = "reject" })).ErrorCode);

        var decided = await CallAsync(f.Manager, "decide_approval", new JsonObject { ["approval_id"] = approvalId, ["decision"] = "approve", ["comment"] = "Go ahead" });
        Assert.False(decided.IsError, decided.Text);
        Assert.Equal("approved", decided.Structured!["approval_status"]!.GetValue<string>());

        var detail = await CallAsync(f.Requester, "get_approval", new JsonObject { ["approval_id"] = approvalId });
        var decision = Assert.Single(detail.Structured!["decisions"]!.AsArray())!;
        Assert.Equal("mcp", decision["channel"]!.GetValue<string>());
        Assert.Equal("untrusted", decision["comment"]!["trust"]!.GetValue<string>());
    }

    [Fact]
    public async Task Describing_an_authority_action_names_what_will_happen_without_doing_it()
    {
        var submitted = await CallAsync(f.Requester, "submit_request", new JsonObject
        {
            ["item_id"] = f.LaptopId.ToString(),
            ["fields"] = new JsonObject { ["operating_system"] = "macOS" },
        });
        var approvalId = submitted.Structured!["approval_id"]!.GetValue<string>();
        var text = await f.Host.RunAsync(SwitchboardTestHost.As(f.Manager, Channel.Mcp), sp =>
            sp.GetServices<IAgentTool>().Single(t => t.Definition.Name == "decide_approval")
                .DescribeAsync(new JsonObject { ["approval_id"] = approvalId, ["decision"] = "approve" }, default));
        Assert.StartsWith("Approve", text);
        Assert.Contains("Laptop", text);

        var still = await CallAsync(f.Manager, "get_approval", new JsonObject { ["approval_id"] = approvalId });
        Assert.Equal("requested", still.Structured!["status"]!.GetValue<string>());
    }
}
