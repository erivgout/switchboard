using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Audit;

/// <summary>One audit entry: the drawer content for htmx, or a full page without JavaScript.</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.AuditView)]
public sealed class DetailModel(AuditBrowser audit) : AdminPageModel
{
    public AuditEntry Entry { get; private set; } = default!;
    public string Details { get; private set; } = "{}";
    public bool IsPartial { get; private set; }

    public async Task OnGetAsync(long sequence)
    {
        Entry = await audit.GetAsync(sequence);
        IsPartial = Request.IsHtmx();
        try
        {
            using var doc = JsonDocument.Parse(Entry.DetailsJson);
            Details = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            Details = Entry.DetailsJson;
        }
    }
}
