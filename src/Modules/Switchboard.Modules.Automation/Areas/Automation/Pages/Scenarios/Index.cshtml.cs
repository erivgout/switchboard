using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Scenarios;

namespace Switchboard.Modules.Automation.Areas.Automation.Pages.Scenarios;

[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class IndexModel(ScenarioService scenarios, CatalogLoader catalogs, SwitchboardDbContext db) : PageModel
{
    public sealed record Row(AutScenario Scenario, string Audience, string Steps, bool CanEdit);

    public List<Row> Mine { get; private set; } = [];
    public List<Row> Shared { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var visible = await scenarios.VisibleAsync(null, ct);
        var groupIds = visible.Where(s => s.GroupId != null).Select(s => s.GroupId!.Value).Distinct().ToList();
        var groups = await db.AgentGroups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        foreach (var s in visible)
        {
            var catalog = await catalogs.LoadAsync(s.WorkspaceId, ct);
            var steps = ScenarioService.Actions(s).Select(a => Describer.Action(a, catalog, html: false)).ToList();
            var sentence = steps.Count == 0 ? "No steps yet" : string.Join(", then ", steps);
            var audience = s.Visibility switch
            {
                ScenarioVisibility.Personal => "Only you",
                ScenarioVisibility.Group => s.GroupId is { } g && groups.TryGetValue(g, out var name) ? $"{name} group" : "A group",
                _ => "Everyone",
            };
            var row = new Row(s, audience, char.ToUpperInvariant(sentence[0]) + sentence[1..], await scenarios.CanEditAsync(s, ct));
            (s.Visibility == ScenarioVisibility.Personal ? Mine : Shared).Add(row);
        }
    }
}
