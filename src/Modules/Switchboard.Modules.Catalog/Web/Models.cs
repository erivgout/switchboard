using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Switchboard.Core.Security;
using Switchboard.Modules.Catalog.Domain;
using Switchboard.Modules.Catalog.Services;

namespace Switchboard.Modules.Catalog.Web;

/// <summary>The browse view shared by <c>/portal/catalog</c> and <c>/catalog</c>.</summary>
public sealed record DirectoryModel(
    string BasePath,
    string? Query,
    Guid? CategoryId,
    IReadOnlyList<CatalogCategoryView> Categories,
    IReadOnlyList<CatalogItemSummary> Items,
    IReadOnlyList<CatalogItemSummary> Popular,
    bool ForAgent);

/// <summary>Renders an item's request form. Values are raw inputs by field key; errors by field key.</summary>
public sealed record FormRenderModel(
    IReadOnlyList<FormField> Fields,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Values,
    IReadOnlyDictionary<string, string> Errors,
    IReadOnlyDictionary<Guid, string> People,
    string IdPrefix = "q",
    bool Preview = false)
{
    public static FormRenderModel Empty(IReadOnlyList<FormField> fields, string idPrefix = "q", bool preview = false) =>
        new(fields, new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, string>(), new Dictionary<Guid, string>(), idPrefix, preview);

    public IReadOnlyList<string> ValuesOf(string key) => Values.GetValueOrDefault(key) ?? [];

    public string? ValueOf(string key) => ValuesOf(key).FirstOrDefault();

    public string? ErrorOf(string key) => Errors.GetValueOrDefault(key);
}

public sealed record RequestedItemsModel(TicketCatalogView View, bool ForRequester, bool ShowStageControls, bool ShowStage = true);

public static class CatalogFormBinding
{
    /// <summary>Answers posted as <c>f.&lt;key&gt;</c> form values.</summary>
    public static Dictionary<string, IReadOnlyList<string>> Read(IFormCollection form) =>
        form.Keys
            .Where(k => k.StartsWith(CatalogForm.FieldPrefix, StringComparison.Ordinal) && k.Length > CatalogForm.FieldPrefix.Length)
            .ToDictionary(k => k[CatalogForm.FieldPrefix.Length..], k => (IReadOnlyList<string>)form[k].Select(v => v ?? "").ToList(), StringComparer.Ordinal);

    /// <summary>Splits a validation failure into per-field errors (by field key) and everything else (added to model state).</summary>
    public static Dictionary<string, string> Errors(ValidationException ex, ModelStateDictionary modelState)
    {
        var fieldErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, messages) in ex.Errors)
        {
            if (key.StartsWith(CatalogForm.FieldPrefix, StringComparison.Ordinal))
            {
                fieldErrors[key[CatalogForm.FieldPrefix.Length..]] = messages.FirstOrDefault() ?? "Check this answer.";
            }
            else
            {
                foreach (var message in messages)
                {
                    modelState.AddModelError(key, message);
                }
            }
        }
        if (fieldErrors.Count > 0)
        {
            modelState.AddModelError("", fieldErrors.Count == 1 ? "One answer needs fixing. It's marked below." : $"{fieldErrors.Count} answers need fixing. They're marked below.");
        }
        return fieldErrors;
    }

    public static string StageCss(RequestedItemStage stage) => "cat-stage cat-stage--" + stage.ToString().ToLowerInvariant();

    /// <summary>Requester-facing stage names.</summary>
    public static string StageForRequester(RequestedItemStage stage) => stage switch
    {
        RequestedItemStage.Requested => "Requested",
        RequestedItemStage.InProgress => "Being prepared",
        RequestedItemStage.Delivered => "Delivered",
        _ => "Cancelled",
    };

    public static string TypeLabel(CatFieldType type) => type switch
    {
        CatFieldType.Text => "Short answer",
        CatFieldType.Paragraph => "Paragraph",
        CatFieldType.Number => "Number",
        CatFieldType.Dropdown => "Dropdown",
        CatFieldType.MultiSelect => "Multiple choice",
        CatFieldType.Checkbox => "Checkbox",
        CatFieldType.Date => "Date",
        _ => "Person",
    };

    public static IEnumerable<string> Paragraphs(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
