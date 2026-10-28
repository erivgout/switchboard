using System.Text.Json.Nodes;
using Switchboard.Modules.Catalog.Domain;
using Switchboard.Modules.Catalog.Services;

namespace Switchboard.Modules.Catalog.Tests;

/// <summary>Server-side validation of request forms (pure).</summary>
public sealed class CatalogFormTests
{
    private static readonly Guid Dana = Guid.NewGuid();
    private static readonly IReadOnlyDictionary<Guid, string> People = new Dictionary<Guid, string> { [Dana] = "Dana Whitfield" };

    private static readonly FormField[] Fields =
    [
        new("os", "Operating system", CatFieldType.Dropdown, true, null, ["Windows", "macOS"]),
        new("extras", "Extras", CatFieldType.MultiSelect, false, null, ["Dock", "Bag", "Mouse"]),
        new("count", "How many screens", CatFieldType.Number, false, null, []),
        new("needed", "Needed by", CatFieldType.Date, false, null, []),
        new("owner", "Budget owner", CatFieldType.UserLookup, false, null, []),
        new("agree", "I'll return my old laptop", CatFieldType.Checkbox, true, null, []),
        new("why", "Why", CatFieldType.Paragraph, false, null, []),
    ];

    private static FormResult Validate(bool strict = false, params (string Key, string Value)[] values) =>
        CatalogForm.Validate(Fields, CatalogFixture.Answers(values), People, strict);

    [Fact]
    public void Valid_answers_become_typed_values_with_readable_text()
    {
        var result = Validate(false, ("os", "macOS"), ("extras", "Mouse"), ("extras", "Dock"), ("count", "2.5"), ("needed", "2026-12-01"),
            ("owner", Dana.ToString()), ("agree", "true"), ("why", "  Old one is broken  "));

        Assert.True(result.IsValid);
        var answers = result.Answers.ToDictionary(a => a.Key);
        Assert.Equal("macOS", answers["os"].Display);
        Assert.Equal("Dock, Mouse", answers["extras"].Display);          // form order, not click order
        Assert.Equal(2.5m, answers["count"].Value!.GetValue<decimal>());
        Assert.Equal("Dec 1, 2026", answers["needed"].Display);
        Assert.Equal("Dana Whitfield", answers["owner"].Display);
        Assert.Equal(Dana.ToString(), ((JsonObject)answers["owner"].Value!)["id"]!.GetValue<string>());
        Assert.True(answers["agree"].Value!.GetValue<bool>());
        Assert.Equal("Old one is broken", answers["why"].Display);
    }

    [Fact]
    public void Each_kind_of_bad_answer_is_reported_on_its_field()
    {
        var result = Validate(false, ("os", "Linux"), ("extras", "Jetpack"), ("count", "lots"), ("needed", "next week"),
            ("owner", Guid.NewGuid().ToString()), ("why", new string('x', CatalogForm.MaxParagraph + 1)));

        Assert.False(result.IsValid);
        Assert.Equal(["f.agree", "f.count", "f.extras", "f.needed", "f.os", "f.owner", "f.why"], result.Errors.Keys.Order());
        Assert.Contains("Tick", result.Errors["f.agree"][0]);
    }

    [Fact]
    public void Required_answers_are_required_and_blank_counts_as_missing()
    {
        var result = Validate(false, ("os", "   "), ("agree", "false"));
        Assert.Equal("Operating system is required.", result.Errors["f.os"][0]);
        Assert.True(result.Errors.ContainsKey("f.agree"));
    }

    [Fact]
    public void Strict_mode_rejects_questions_that_are_not_on_the_form()
    {
        Assert.True(Validate(false, ("os", "Windows"), ("agree", "true"), ("color", "red")).IsValid);
        var strict = Validate(true, ("os", "Windows"), ("agree", "true"), ("color", "red"));
        Assert.True(strict.Errors.ContainsKey("f.color"));
    }

    [Fact]
    public void Single_value_fields_refuse_several_values()
    {
        var result = Validate(false, ("os", "Windows"), ("os", "macOS"), ("agree", "true"));
        Assert.True(result.Errors.ContainsKey("f.os"));
    }

    [Fact]
    public void Answers_round_trip_back_into_form_input_for_resubmitting()
    {
        var first = Validate(false, ("os", "Windows"), ("extras", "Bag"), ("count", "3"), ("owner", Dana.ToString()), ("agree", "true"));
        var stored = CatalogForm.Deserialize(CatalogForm.Serialize(first.Answers));
        var again = CatalogForm.Validate(Fields, CatalogForm.ToInput(stored), People);
        Assert.True(again.IsValid);
        Assert.Equal(first.Answers.Select(a => a.Display), again.Answers.Select(a => a.Display));
    }

    [Theory]
    [InlineData("Cost centre", "cost_centre")]
    [InlineData("  Why do you need it?  ", "why_do_you_need_it")]
    [InlineData("2nd monitor", "field_2nd_monitor")]
    public void Keys_are_derived_from_labels(string label, string key) => Assert.Equal(key, CatalogForm.KeyFrom(label));

    [Fact]
    public void Money_uses_minor_units() => Assert.Equal("$1,299.00", CatalogForm.Money(129900, "USD"));
}
