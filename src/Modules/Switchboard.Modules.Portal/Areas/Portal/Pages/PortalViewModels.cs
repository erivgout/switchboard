using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Modules.Portal.Services;

namespace Switchboard.Modules.Portal.Areas.Portal.Pages;

public sealed record RequestListModel(IReadOnlyList<PortalRequestRow> Items, bool ShowRequester = false);

public sealed record UploaderModel(string Id, string Label, IReadOnlyList<PortalFile> Files);

/// <summary>Validation errors from services, shown next to fields and, in the order given, in a summary.</summary>
internal static class PortalErrors
{
    public const string SummaryKey = "prt.errors";

    public static void Add(PageModel page, Switchboard.Core.Security.ValidationException ex)
    {
        var summary = page.ViewData[SummaryKey] as List<string> ?? [];
        foreach (var (key, messages) in ex.Errors)
        {
            foreach (var message in messages)
            {
                page.ModelState.AddModelError(key, message);
                summary.Add(message);
            }
        }
        page.ViewData[SummaryKey] = summary;
    }

    public static string? First(ModelStateDictionary modelState, string key) =>
        modelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0 ? entry.Errors[0].ErrorMessage : null;
}

public sealed record CustomFieldModel(PortalFieldDefinition Field, IReadOnlyList<string> Values, string? Error);

/// <summary>
/// Name-or-email picker. <c>Multiple</c>: chosen people become hidden <c>ChipName</c> inputs and leftover
/// text posts as <c>TextName</c>. Single: picking a person fills the text input with their email.
/// </summary>
public sealed record PeoplePickerModel(
    string Id,
    string ChipName,
    string TextName,
    string Label,
    string Help,
    IReadOnlyList<string> Values,
    string? Error,
    bool Multiple = true,
    bool Optional = true);
