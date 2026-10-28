using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceLevels.Domain;
using Switchboard.Modules.ServiceLevels.Engine;
using Switchboard.Modules.ServiceLevels.Services;
using Switchboard.UI;

namespace Switchboard.Modules.ServiceLevels.Areas.ServiceLevels.Pages.Admin;

public sealed class PoliciesModel(SwitchboardDbContext db, IPermissionService permissions, SlaAdminService service, SlaEngine engine)
    : SlaAdminPage(db, permissions)
{
    public sealed record Row(SlaPolicy Policy, string AppliesTo, string? CalendarName, int Tickets);

    public List<Row> Rows { get; private set; } = [];
    public bool ReevaluateOnChange { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? ws, CancellationToken ct)
    {
        if (!await UseWorkspaceAsync(ws, ct))
        {
            return Forbid();
        }
        var policies = await service.PoliciesAsync(Workspace.Id, ct);
        var calendars = await Db.Set<SlaCalendar>().AsNoTracking().Where(c => c.WorkspaceId == Workspace.Id).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var ids = policies.Select(p => p.Id).ToList();
        var usage = await Db.Set<SlaTicketState>().AsNoTracking()
            .Where(s => s.PolicyId != null && ids.Contains(s.PolicyId.Value))
            .GroupBy(s => s.PolicyId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        foreach (var p in policies)
        {
            var conditions = new List<string>();
            foreach (var c in p.Conditions)
            {
                conditions.Add(await engine.DescribeConditionAsync(c, ct));
            }
            Rows.Add(new Row(p, conditions.Count == 0 ? "Every ticket" : string.Join("; ", conditions),
                p.CalendarId is { } cid ? calendars.GetValueOrDefault(cid) : null, usage.GetValueOrDefault(p.Id)));
        }
        ReevaluateOnChange = await service.ReevaluateOnChangeAsync(Workspace.Id, ct);
        return Page();
    }

    /// <summary>Drag-and-drop order from service-levels.js.</summary>
    public async Task<IActionResult> OnPostReorderAsync(string? ws, [FromForm] List<Guid> ids, CancellationToken ct)
    {
        if (!await UseWorkspaceAsync(ws, ct))
        {
            return Forbid();
        }
        try
        {
            await service.ReorderAsync(Workspace.Id, ids, ct);
        }
        catch (ValidationException ex)
        {
            Response.HxToast(ex.Message, ToastKind.Error);
            return StatusCode(StatusCodes.Status409Conflict);
        }
        Response.HxToast("Order saved");
        return new NoContentResult();
    }

    /// <summary>Move up or down: the keyboard and no-script way to reorder.</summary>
    public async Task<IActionResult> OnPostMoveAsync(string? ws, Guid id, int by, CancellationToken ct)
    {
        if (!await UseWorkspaceAsync(ws, ct))
        {
            return Forbid();
        }
        var order = (await service.PoliciesAsync(Workspace.Id, ct)).Select(p => p.Id).ToList();
        var index = order.IndexOf(id);
        var target = index + Math.Sign(by);
        if (index >= 0 && target >= 0 && target < order.Count)
        {
            (order[index], order[target]) = (order[target], order[index]);
            await service.ReorderAsync(Workspace.Id, order, ct);
            TempData.Toast("Order saved");
        }
        return Redirect(WithWorkspace("/admin/service-levels/policies"));
    }

    public async Task<IActionResult> OnPostActiveAsync(string? ws, Guid id, bool active, CancellationToken ct)
    {
        if (!await UseWorkspaceAsync(ws, ct))
        {
            return Forbid();
        }
        await service.SetActiveAsync(id, active, ct);
        TempData.Toast(active ? "Policy turned on" : "Policy turned off");
        return Redirect(WithWorkspace("/admin/service-levels/policies"));
    }

    public async Task<IActionResult> OnPostSettingsAsync(string? ws, bool reevaluate, CancellationToken ct)
    {
        if (!await UseWorkspaceAsync(ws, ct))
        {
            return Forbid();
        }
        await service.SetReevaluateOnChangeAsync(Workspace.Id, reevaluate, ct);
        TempData.Toast("Setting saved");
        return Redirect(WithWorkspace("/admin/service-levels/policies"));
    }
}
