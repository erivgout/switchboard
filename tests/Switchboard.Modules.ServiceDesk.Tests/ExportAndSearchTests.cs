using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceDesk.Services;

namespace Switchboard.Modules.ServiceDesk.Tests;

/// <summary>CSV formula neutralization (SR-INP-6).</summary>
public sealed class CsvExportTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "'=HYPERLINK(\"http://evil\")")]
    [InlineData("+1+2", "'+1+2")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("\rcmd", "'\rcmd")]
    [InlineData("VPN drops", "VPN drops")]
    [InlineData("a=b", "a=b")]
    [InlineData("", "")]
    public void Formula_triggers_are_neutralized(string input, string expected) =>
        Assert.Equal(expected, CsvExport.Neutralize(input));

    [Fact]
    public void Cells_are_quoted_and_escaped() =>
        Assert.Equal("\"Say \"\"hi\"\"\",\"'=1+1\",\"\"", CsvExport.Row(["Say \"hi\"", "=1+1", null]));
}

/// <summary>Global search recognizes ticket references and only jumps to visible tickets.</summary>
public sealed class SearchRedirectTests : ServiceDeskTestBase
{
    [Theory]
    [InlineData("INC-123", 123)]
    [InlineData("sr-77", 77)]
    [InlineData("#1042", 1042)]
    [InlineData("  INC-5  ", 5)]
    public void Ticket_references_are_recognized(string text, int expected)
    {
        Assert.True(TicketReference.TryParse(text, out var number));
        Assert.Equal(expected, number);
    }

    [Theory]
    [InlineData("1042")]
    [InlineData("INC 12")]
    [InlineData("INC-")]
    [InlineData("#0")]
    [InlineData("vpn INC-12")]
    [InlineData(null)]
    public void Other_text_is_not_a_reference(string? text) => Assert.False(TicketReference.TryParse(text, out _));

    [Fact]
    public async Task A_reference_redirects_only_when_the_agent_can_see_the_ticket()
    {
        var agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        var restricted = await Host.CreateAgentAsync("Rita Restricted", RoleKeys.Agent, TicketScope.Restricted);
        var ticket = await FileTicketAsync("VPN drops");

        var forAgent = await As(agent, sp => sp.GetRequiredService<AgentSearch>().SearchAsync(ticket.DisplayId));
        var forRestricted = await As(restricted, sp => sp.GetRequiredService<AgentSearch>().SearchAsync(ticket.DisplayId));
        var forRequester = await As(Requester, sp => sp.GetRequiredService<AgentSearch>().FindTicketNumberAsync($"#{ticket.Number}"));

        Assert.Equal(ticket.Number, forAgent.RedirectNumber);
        Assert.Null(forRestricted.RedirectNumber);
        Assert.Null(forRequester);
    }

    [Fact]
    public async Task Text_search_finds_tickets_and_people()
    {
        var agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        await FileTicketAsync("Laptop battery swelling");

        var result = await As(agent, sp => sp.GetRequiredService<AgentSearch>().SearchAsync("battery"));
        Assert.Null(result.RedirectNumber);
        Assert.Single(result.Tickets!.Items);

        var people = await As(agent, sp => sp.GetRequiredService<AgentSearch>().SearchAsync("Dana"));
        Assert.True(people.CanSeePeople);
        Assert.Contains(people.People, p => p.Id == Requester.Id);

        // Text that isn't valid filter syntax falls back to a phrase search instead of failing.
        var odd = await As(agent, sp => sp.GetRequiredService<AgentSearch>().SearchAsync("colour:blue \"half"));
        Assert.Null(odd.TicketError);
    }
}
