using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Engine;

namespace Switchboard.Modules.Automation.Tests;

/// <summary>Condition evaluation, plain-language descriptions and placeholder encoding. No database.</summary>
public sealed class ConditionTests
{
    private static readonly BuilderCatalog Catalog = new()
    {
        Triggers = [],
        Clocks = [],
        ConversationKinds = [],
        Placeholders = [],
        Actions = [],
        Operators = new Dictionary<string, IReadOnlyList<CatalogOption>>(),
        Fields =
        [
            new("ticket.subject", "Subject", "Ticket", FieldKind.Text, []),
            new("ticket.priority", "Priority", "Ticket", FieldKind.Ordinal, CatalogLoader.Priorities),
            new("ticket.tags", "Tags", "Ticket", FieldKind.Tags, []),
            new("requester.vip", "Requester is a VIP", "Requester", FieldKind.Bool, CatalogLoader.YesNo, Negated: "Requester isn't a VIP"),
            new("ticket.group", "Group", "Ticket", FieldKind.Choice, [new("g-net", "Network"), new("g-hw", "Hardware")], Nullable: true),
            new("ticket.category", "Category", "Ticket", FieldKind.Choice, [new("c-net", "Network"), new("c-vpn", "Network / VPN")], Nullable: true),
            new("ticket.cf.cost", "Cost", "Custom fields", FieldKind.Number, [], Nullable: true),
            new("ticket.cf.vpn_region", "VPN region", "Custom fields", FieldKind.Text, [], Nullable: true),
        ],
    };

    private static TicketFacts Facts(string subject = "VPN outage in Chicago", string priority = "High", bool vip = false,
        string[]? tags = null, string? group = null, string[]? category = null, decimal? cost = null)
    {
        var facts = new TicketFacts { TicketId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), DisplayId = "INC-1" };
        facts.Values["ticket.subject"] = subject;
        facts.Values["ticket.priority"] = priority;
        facts.Values["requester.vip"] = vip;
        facts.Values["ticket.tags"] = (tags ?? []).ToList();
        facts.Values["ticket.group"] = group;
        facts.Values["ticket.category"] = (category ?? []).ToList();
        facts.Values["ticket.cf.cost"] = cost;
        return facts;
    }

    private static Condition R(string field, string op, string? value = null) => new() { Field = field, Op = op, Value = value };

    private static bool Eval(ConditionSet set, TicketFacts facts) => ConditionEvaluator.Evaluate(set, facts, Catalog).Matched;

    [Fact]
    public void And_or_groups_combine_as_written()
    {
        // (priority at least High AND requester is VIP) OR (subject contains "outage")
        var set = new ConditionSet
        {
            Match = "any",
            Groups =
            [
                new ConditionGroup { Match = "all", Rules = [R("ticket.priority", Operators.AtLeast, "High"), R("requester.vip", Operators.Is, "true")] },
                new ConditionGroup { Match = "all", Rules = [R("ticket.subject", Operators.Contains, "OUTAGE")] },
            ],
        };
        Assert.True(Eval(set, Facts(subject: "Laptop", priority: "Urgent", vip: true)));
        Assert.False(Eval(set, Facts(subject: "Laptop", priority: "Urgent", vip: false)));
        Assert.True(Eval(set, Facts(subject: "Total outage", priority: "Low", vip: false)));
        Assert.False(Eval(set, Facts(subject: "Laptop", priority: "Medium", vip: true)));

        set.Match = "all";
        Assert.False(Eval(set, Facts(subject: "Laptop", priority: "Urgent", vip: true)));
        Assert.True(Eval(set, Facts(subject: "outage", priority: "Urgent", vip: true)));
    }

    [Fact]
    public void Any_within_a_group_needs_one_match()
    {
        var set = new ConditionSet { Groups = [new ConditionGroup { Match = "any", Rules = [R("ticket.group", Operators.Is, "g-net"), R("ticket.tags", Operators.Has, "network")] }] };
        Assert.True(Eval(set, Facts(group: "g-net")));
        Assert.True(Eval(set, Facts(tags: ["Network"])));
        Assert.False(Eval(set, Facts(group: "g-hw")));
    }

    [Fact]
    public void No_conditions_always_match()
    {
        Assert.True(Eval(new ConditionSet(), Facts()));
        Assert.True(Eval(new ConditionSet { Groups = [new ConditionGroup()] }, Facts()));
    }

    [Theory]
    [InlineData(Operators.Contains, "vpn", true)]
    [InlineData(Operators.NotContains, "vpn", false)]
    [InlineData(Operators.Is, "vpn outage in chicago", true)]
    [InlineData(Operators.IsNot, "vpn outage in chicago", false)]
    [InlineData(Operators.StartsWith, "VPN", true)]
    [InlineData(Operators.EndsWith, "chicago", true)]
    [InlineData(Operators.Empty, null, false)]
    [InlineData(Operators.NotEmpty, null, true)]
    public void Text_operators_ignore_case(string op, string? value, bool expected) =>
        Assert.Equal(expected, Eval(new ConditionSet { Groups = [new ConditionGroup { Rules = [R("ticket.subject", op, value)] }] }, Facts()));

    [Theory]
    [InlineData("High", Operators.AtLeast, "Medium", true)]
    [InlineData("High", Operators.AtLeast, "Urgent", false)]
    [InlineData("Low", Operators.AtMost, "Medium", true)]
    [InlineData("Urgent", Operators.Is, "Urgent", true)]
    [InlineData("Urgent", Operators.IsNot, "Urgent", false)]
    public void Priority_compares_by_rank(string actual, string op, string value, bool expected) =>
        Assert.Equal(expected, Eval(new ConditionSet { Groups = [new ConditionGroup { Rules = [R("ticket.priority", op, value)] }] }, Facts(priority: actual)));

    [Fact]
    public void Category_matches_any_level_and_empty()
    {
        var at = (string op, string? v) => new ConditionSet { Groups = [new ConditionGroup { Rules = [R("ticket.category", op, v)] }] };
        Assert.True(Eval(at(Operators.Is, "c-net"), Facts(category: ["c-net", "c-vpn"])));
        Assert.True(Eval(at(Operators.Is, "c-vpn"), Facts(category: ["c-net", "c-vpn"])));
        Assert.True(Eval(at(Operators.IsNot, "c-vpn"), Facts(category: ["c-net"])));
        Assert.True(Eval(at(Operators.Empty, null), Facts(category: [])));
        Assert.False(Eval(at(Operators.NotEmpty, null), Facts(category: [])));
    }

    [Fact]
    public void Numbers_need_a_value_to_compare()
    {
        var gt = new ConditionSet { Groups = [new ConditionGroup { Rules = [R("ticket.cf.cost", Operators.GreaterThan, "100")] }] };
        Assert.True(Eval(gt, Facts(cost: 250m)));
        Assert.False(Eval(gt, Facts(cost: 50m)));
        Assert.False(Eval(gt, Facts(cost: null)));
        Assert.True(Eval(new ConditionSet { Groups = [new ConditionGroup { Rules = [R("ticket.cf.cost", Operators.Empty)] }] }, Facts(cost: null)));
    }

    [Fact]
    public void Unknown_fields_never_match()
    {
        var result = ConditionEvaluator.Evaluate(new ConditionSet { Groups = [new ConditionGroup { Rules = [R("ticket.cf.deleted", Operators.NotEmpty)] }] }, Facts(), Catalog);
        Assert.False(result.Matched);
        Assert.Equal("This field no longer exists", result.Groups[0].Rules[0].Actual);
    }

    [Fact]
    public void Describer_reads_like_a_sentence()
    {
        var def = new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketCreated },
            Conditions = new ConditionSet
            {
                Match = "any",
                Groups =
                [
                    new ConditionGroup { Rules = [R("ticket.priority", Operators.AtLeast, "High"), R("requester.vip", Operators.Is, "true")] },
                    new ConditionGroup { Rules = [R("ticket.cf.vpn_region", Operators.Is, "EMEA")] },
                ],
            },
        };
        var text = Describer.Sentence(def, Catalog, html: false);
        Assert.Equal("When a ticket is created, if (priority is at least High and requester is a VIP) or VPN region is “EMEA”, then do nothing yet.", text);
        var html = Describer.Sentence(new WorkflowDefinition { Conditions = new ConditionSet { Groups = [new ConditionGroup { Rules = [R("ticket.subject", Operators.Contains, "<script>")] }] } }, Catalog);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void Placeholders_are_encoded_for_their_destination()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ticket.subject"] = "<b>\"Hi\" & bye</b>\r\nBcc: x@evil.test" };
        Assert.Equal("S: &lt;b&gt;&quot;Hi&quot; &amp; bye&lt;/b&gt;\r\nBcc: x@evil.test", PlaceholderRenderer.Render("S: {{ticket.subject}}", values, PlaceholderEncoding.Html));
        Assert.DoesNotContain("\n", PlaceholderRenderer.Render("{{ ticket.subject }}", values, PlaceholderEncoding.SingleLine));
        var json = PlaceholderRenderer.Render("{\"text\": \"{{ticket.subject}}\"}", values, PlaceholderEncoding.Json);
        Assert.Equal("<b>\"Hi\" & bye</b>\r\nBcc: x@evil.test", System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("text").GetString());
        Assert.Equal("q=%3Cb%3E%22Hi%22%20%26%20bye%3C%2Fb%3E%0D%0ABcc%3A%20x%40evil.test", PlaceholderRenderer.Render("q={{ticket.subject}}", values, PlaceholderEncoding.Url));
        Assert.Equal("{{ticket.nope}}", PlaceholderRenderer.Render("{{ticket.nope}}", values, PlaceholderEncoding.Text));
    }
}
