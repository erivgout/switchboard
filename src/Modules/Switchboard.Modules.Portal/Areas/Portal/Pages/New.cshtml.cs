using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Portal.Services;

namespace Switchboard.Modules.Portal.Areas.Portal.Pages;

public sealed class NewModel(PortalRequests portal) : PageModel
{
    private const string CustomPrefix = "Custom[";

    /// <summary><c>request</c> files a service request; anything else reports a problem.</summary>
    [BindProperty(SupportsGet = true, Name = "type")]
    public string? Kind { get; set; }

    [BindProperty]
    public string? Subject { get; set; }

    [BindProperty]
    public string? Description { get; set; }

    [BindProperty]
    public string? Category { get; set; }

    [BindProperty]
    public List<string> Cc { get; set; } = [];

    /// <summary>Addresses typed into the CC box but not turned into chips (or no JavaScript).</summary>
    [BindProperty]
    public string? CcText { get; set; }

    [BindProperty]
    public List<Guid> AttachmentIds { get; set; } = [];

    public bool IsServiceRequest => string.Equals(Kind, "request", StringComparison.OrdinalIgnoreCase);
    public string PageTitle => IsServiceRequest ? "Request something" : "Report a problem";

    public IReadOnlyList<PortalCategory> Categories { get; private set; } = [];
    public IReadOnlyList<PortalFieldDefinition> Fields { get; private set; } = [];
    public IReadOnlyList<PortalFile> Uploaded { get; private set; } = [];
    public IReadOnlyDictionary<string, string[]> CustomValues { get; private set; } = new Dictionary<string, string[]>();

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        CustomValues = Request.Form
            .Where(f => f.Key.StartsWith(CustomPrefix, StringComparison.Ordinal) && f.Key.EndsWith(']'))
            .ToDictionary(f => f.Key[CustomPrefix.Length..^1], f => f.Value.Select(v => v ?? "").ToArray(), StringComparer.Ordinal);
        var cc = Cc.Concat((CcText ?? "").Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            var ticket = await portal.CreateAsync(new NewPortalRequest
            {
                Type = IsServiceRequest ? TicketType.ServiceRequest : TicketType.Incident,
                Subject = Subject ?? "",
                Description = Description,
                Category = Category,
                CcEmails = cc,
                AttachmentIds = AttachmentIds,
                CustomFields = CustomValues,
            }, ct);
            return Redirect($"/portal/requests/{ticket.Number}/sent");
        }
        catch (ValidationException ex)
        {
            PortalErrors.Add(this, ex);
            Cc = cc;
            CcText = null;
            await LoadAsync(ct);
            return Page();
        }
    }

    public string? Error(string key) => PortalErrors.First(ModelState, key);

    public string[] Value(PortalFieldDefinition field) =>
        CustomValues.TryGetValue(field.Key, out var v) ? v
        : HttpMethods.IsGet(Request.Method) && !string.IsNullOrEmpty(field.DefaultValue) ? [field.DefaultValue] : [];

    private async Task LoadAsync(CancellationToken ct)
    {
        Categories = await portal.CategoriesAsync(ct);
        Fields = await portal.RequesterFieldsAsync(ct);
        Uploaded = await portal.PendingUploadsAsync(AttachmentIds, ct);
    }
}
