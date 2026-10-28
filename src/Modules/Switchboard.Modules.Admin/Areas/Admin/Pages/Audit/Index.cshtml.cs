using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Pages.Audit;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.AuditView)]
public sealed class IndexModel(AuditBrowser audit) : AdminPageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Actor { get; set; }

    [BindProperty(SupportsGet = true, Name = "event")]
    public string? ActionFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Type { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Object { get; set; }

    /// <summary>yyyy-MM-dd, inclusive, UTC.</summary>
    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    /// <summary>yyyy-MM-dd, inclusive, UTC.</summary>
    [BindProperty(SupportsGet = true)]
    public string? To { get; set; }

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public Paged<AuditEntry> Result { get; private set; } = new([], 0, 1, AuditQuery.PageSize);
    public IReadOnlyList<string> ObjectTypes { get; private set; } = [];
    public bool Filtered => new[] { Actor, ActionFilter, Type, Object, From, To }.Any(v => !string.IsNullOrWhiteSpace(v));

    public async Task OnGetAsync()
    {
        Result = await audit.SearchAsync(Query() with { Page = PageNumber });
        ObjectTypes = await audit.ObjectTypesAsync();
    }

    public async Task<IActionResult> OnPostExportAsync()
    {
        var (csv, _) = await audit.ExportCsvAsync(Query());
        return File(csv, "text/csv", $"audit-log-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }

    private AuditQuery Query() => new()
    {
        Actor = Actor,
        Action = ActionFilter,
        ObjectType = Type,
        ObjectId = Object,
        From = Date(From),
        To = Date(To)?.AddDays(1),
    };

    private static DateTimeOffset? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;

    public string PageUrl(int page) => Link("/admin/audit", ("actor", Actor), ("event", ActionFilter), ("type", Type), ("object", Object),
        ("from", From), ("to", To), ("page", page > 1 ? page.ToString(CultureInfo.InvariantCulture) : null));
}
