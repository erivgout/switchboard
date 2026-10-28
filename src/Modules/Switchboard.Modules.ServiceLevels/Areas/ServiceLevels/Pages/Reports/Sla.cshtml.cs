using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceLevels.Components;
using Switchboard.Modules.ServiceLevels.Reports;

namespace Switchboard.Modules.ServiceLevels.Areas.ServiceLevels.Pages.Reports;

/// <summary>SLA compliance (FR-ANL-2): outcomes over the viewer's visible tickets, with overrides beside the original targets (FR-SLA-6).</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + SlaSlots.ReportsPrivilege)]
public sealed class SlaModel(SlaReportService reports, SwitchboardDbContext db, IActorAccessor actors, IPermissionService permissions, TimeProvider time) : PageModel
{
    [FromQuery(Name = "period")] public string? Period { get; set; }
    [FromQuery(Name = "from")] public string? From { get; set; }
    [FromQuery(Name = "to")] public string? To { get; set; }
    [FromQuery(Name = "by")] public string? By { get; set; }
    [FromQuery(Name = "group")] public Guid? Group { get; set; }
    [FromQuery(Name = "priority")] public Priority? Priority { get; set; }

    public ReportPeriod Range { get; private set; } = default!;
    public SlaComplianceReport Report { get; private set; } = default!;
    public List<(Guid Id, string Name)> Groups { get; private set; } = [];
    public string Query { get; private set; } = "";

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnGetCsvAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        string Outcome(SlaOutcome o) => o switch
        {
            SlaOutcome.Met => "Met",
            SlaOutcome.Missed => "Missed",
            SlaOutcome.Pending => "Not due yet",
            _ => "No target",
        };
        var rows = new List<IReadOnlyList<string?>>
        {
            new[]
            {
                "Ticket", "Subject", "Priority", "Group", "Policy", "Created",
                "First response SLA target", "First response due", "First responded", "First response",
                "Resolution SLA target", "Resolution due", "Resolved", "Resolution", "Resolution against SLA target",
                "Due date set by hand", "Reason", "Breaches",
            },
        };
        rows.AddRange(Report.Rows.Select(j => (IReadOnlyList<string?>)new[]
        {
            j.Row.DisplayId, j.Row.Subject, j.Row.Priority.ToString(), j.Row.GroupName, j.Row.PolicyName, SlaCsv.Time(j.Row.CreatedAt),
            SlaCsv.Time(j.Row.FirstResponseTargetAt), SlaCsv.Time(j.Row.FirstResponseDueBy), SlaCsv.Time(j.Row.FirstRespondedAt), Outcome(j.FirstResponse),
            SlaCsv.Time(j.Row.ResolutionTargetAt), SlaCsv.Time(j.Row.DueBy), SlaCsv.Time(j.Row.ResolvedAt), Outcome(j.Resolution), Outcome(j.ResolutionAgainstOriginal),
            j.Row.Overridden ? "Yes" : "No", j.Row.OverrideReason, j.Row.Breaches.ToString(CultureInfo.InvariantCulture),
        }));
        var name = $"sla-compliance-{Range.FirstDay:yyyy-MM-dd}-to-{Range.LastDay:yyyy-MM-dd}.csv";
        return File(SlaCsv.Write(rows), "text/csv; charset=utf-8", name);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var zone = await ReportPeriod.ViewerZoneAsync(db, actors, ct);
        Range = ReportPeriod.Resolve(Period, From, To, zone, time.GetUtcNow());
        var p = await permissions.CurrentAsync(ct);
        List<Guid>? workspaces = p is null ? [] : p.IsOrgAdmin ? null : [.. p.WorkspacesWith(Privileges.TicketsView)];
        Groups = (await db.AgentGroups.AsNoTracking()
                .Where(g => workspaces == null || workspaces.Contains(g.WorkspaceId))
                .OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync(ct))
            .Select(g => (g.Id, g.Name)).ToList();
        if (Group is { } gid && Groups.All(g => g.Id != gid))
        {
            Group = null;
        }
        Report = await reports.ComplianceAsync(Range.From, Range.To, By, Group, Priority, ct);
        Query = QueryString();
    }

    private string QueryString()
    {
        var parts = new List<string> { $"period={Range.Key}" };
        if (Range.Key == "custom")
        {
            parts.Add($"from={Range.FirstDay:yyyy-MM-dd}");
            parts.Add($"to={Range.LastDay:yyyy-MM-dd}");
        }
        parts.Add($"by={Report.GroupBy}");
        if (Group is { } g)
        {
            parts.Add($"group={g}");
        }
        if (Priority is { } pr)
        {
            parts.Add($"priority={pr}");
        }
        return string.Join("&", parts);
    }
}
