using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using static Switchboard.Core.Tickets.TicketQueryLanguage;

namespace Switchboard.Core.Tests;

public sealed class TicketQueryLanguageTests
{
    [Fact]
    public void Terms_combine_with_implicit_and()
    {
        var node = Assert.IsType<And>(Parse("priority>=high group:Network"));
        Assert.Equal(new FieldTerm("priority", ">=", "high"), node.Items[0]);
        Assert.Equal(new FieldTerm("group", ":", "Network"), node.Items[1]);
    }

    [Fact]
    public void Or_binds_looser_than_and()
    {
        var node = Assert.IsType<Or>(Parse("state:open agent:me OR is:lit"));
        Assert.IsType<And>(node.Items[0]);
        Assert.Equal(new FieldTerm("is", ":", "lit"), node.Items[1]);
    }

    [Fact]
    public void Quoted_values_and_negation_parse()
    {
        var node = Assert.IsType<And>(Parse("group:\"Service Desk\" -tag:vip \"vpn timeout\""));
        Assert.Equal(new FieldTerm("group", ":", "Service Desk"), node.Items[0]);
        Assert.Equal(new Not(new FieldTerm("tag", ":", "vip")), node.Items[1]);
        Assert.Equal(new TextTerm("vpn timeout"), node.Items[2]);
    }

    [Theory]
    [InlineData("INC-1042")]
    [InlineData("sr-1042")]
    [InlineData("#1042")]
    public void Display_ids_become_number_filters(string query) =>
        Assert.Equal(new FieldTerm("number", ":", "1042"), Parse(query));

    [Theory]
    [InlineData("(state:open")]
    [InlineData("group:\"Service Desk")]
    [InlineData(")")]
    public void Malformed_queries_raise_validation_errors(string query) =>
        Assert.Throws<ValidationException>(() => Parse(query));

    [Fact]
    public void Empty_query_parses_to_null() => Assert.Null(Parse("   "));

    [Theory]
    [InlineData(Level.High, Level.High, Priority.Urgent)]
    [InlineData(Level.High, Level.Medium, Priority.High)]
    [InlineData(Level.Medium, Level.Medium, Priority.Medium)]
    [InlineData(Level.Low, Level.High, Priority.Medium)]
    [InlineData(Level.Low, Level.Low, Priority.Low)]
    public void Priority_matrix_matches_appendix_c(Level impact, Level urgency, Priority expected) =>
        Assert.Equal(expected, PriorityMatrix.Derive(impact, urgency));
}
