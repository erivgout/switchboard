using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.ServiceDesk.Data;
using Switchboard.Modules.ServiceDesk.Services;
using Switchboard.UI;

namespace Switchboard.Modules.ServiceDesk.Areas.ServiceDesk.Pages.Tickets;

[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class IndexModel(
    ITicketSearch search,
    ITicketService tickets,
    SavedViewService savedViews,
    BulkActionService bulk,
    IPermissionService permissions,
    SwitchboardDbContext db,
    IAuditLog audit,
    UserManager<User> users,
    IActorAccessor actors,
    TimeProvider time) : PageModel
{
    public const int PageSize = 25;
    private const int BoardLimit = 200;
    private const int ExportLimit = 10_000;

    [FromQuery(Name = "view")] public string? View { get; set; }
    [FromQuery(Name = "saved")] public Guid? Saved { get; set; }
    [FromQuery(Name = "q")] public string? Q { get; set; }
    [FromQuery(Name = "sort")] public string? Sort { get; set; }
    [FromQuery(Name = "p")] public int PageNumber { get; set; } = 1;
    [FromQuery(Name = "layout")] public string? Layout { get; set; }
    [FromQuery(Name = "by")] public string? By { get; set; }
    [FromQuery(Name = "cols")] public string? Cols { get; set; }

    public QueueContext Queue { get; private set; } = default!;
    public QueueResults Results { get; private set; } = default!;
    public IReadOnlyList<SdSavedView> SavedViews { get; private set; } = [];
    public IReadOnlyDictionary<string, int?> Counts { get; private set; } = new Dictionary<string, int?>();
    public IReadOnlyList<string> StatusNames { get; private set; } = [];
    public IReadOnlyList<StatusOption> BulkStatuses { get; private set; } = [];
    public IReadOnlyList<NamedOption> Groups { get; private set; } = [];
    public IReadOnlyList<PersonOption> Agents { get; private set; } = [];
    public Guid MeId { get; private set; }
    public HashSet<Guid> EditableSavedViews { get; private set; } = [];

    // ---------------------------------------------------------------- pages and partials

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await ResolveAsync(ct) is { } redirect)
        {
            return redirect;
        }
        Results = await LoadResultsAsync(ct);
        await LoadSidebarAsync(ct);
        await LoadFilterChoicesAsync(ct);
        ViewData["Title"] = Queue.Title;
        return Page();
    }

    /// <summary>The table or board, for live refreshes and paging without a full reload.</summary>
    public async Task<IActionResult> OnGetRowsAsync(CancellationToken ct)
    {
        if (await ResolveAsync(ct) is not null)
        {
            return NotFound();
        }
        Results = await LoadResultsAsync(ct);
        return Partial("_QueueResults", Results);
    }

    /// <summary>The views sidebar with fresh counts.</summary>
    public async Task<IActionResult> OnGetCountsAsync(CancellationToken ct)
    {
        if (await ResolveAsync(ct) is not null)
        {
            return NotFound();
        }
        await LoadSidebarAsync(ct);
        return Partial("_ViewsNav", this);
    }

    // ---------------------------------------------------------------- export

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        if (await ResolveAsync(ct) is not null)
        {
            return NotFound();
        }
        await permissions.DemandAsync(Privileges.TicketsExport, ct: ct);
        var query = await search.QueryAsync(Queue.Query, ct: ct);
        var total = await query.CountAsync(ct);
        await audit.WriteAsync("tickets.exported", "ticket", null, new { query = Queue.Query, rows = Math.Min(total, ExportLimit) }, ct);

        var fileName = $"tickets-{time.GetUtcNow():yyyy-MM-dd-HHmm}.csv";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        Response.ContentType = "text/csv; charset=utf-8";
        string[] header = ["ID", "Subject", "Status", "Priority", "Type", "Requester", "Requester email", "Group", "Agent", "Source", "Created (UTC)", "Updated (UTC)", "Due (UTC)", "Resolved (UTC)"];
        await CsvExport.WriteAsync(Response.Body, header, ExportRowsAsync(ct), ct);
        return new EmptyResult();
    }

    private async IAsyncEnumerable<IReadOnlyList<string?>> ExportRowsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        static string? Iso(DateTimeOffset? d) => d?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var page = 1;
        var written = 0;
        while (written < ExportLimit)
        {
            var batch = await search.SearchAsync(new TicketSearchRequest { Query = Queue.Query, Sort = Queue.Sort, Page = page++, PageSize = 100 }, ct);
            foreach (var t in batch.Items)
            {
                yield return [t.DisplayId, t.Subject, t.StatusName, t.Priority.ToString(), Labels.Type(t.Type), t.RequesterName, t.RequesterEmail,
                    t.GroupName, t.AgentName, t.SourceKey, Iso(t.CreatedAt), Iso(t.UpdatedAt), Iso(t.DueBy), Iso(t.ResolvedAt)];
                if (++written >= ExportLimit)
                {
                    yield break;
                }
            }
            if (!batch.HasMore)
            {
                yield break;
            }
        }
    }

    // ---------------------------------------------------------------- bulk

    public sealed class BulkInput
    {
        public string Action { get; set; } = "";
        public List<Guid> Ids { get; set; } = [];
        public bool AllMatching { get; set; }
        public string? Query { get; set; }
        public int? ConfirmedCount { get; set; }
        public string? GroupChoice { get; set; }
        public string? AgentChoice { get; set; }
        public Guid? StatusId { get; set; }
        public Priority? Priority { get; set; }
        public string? Tags { get; set; }
        public Guid? PrimaryId { get; set; }
        public string? Password { get; set; }
        public string? ReturnUrl { get; set; }
    }

    public async Task<IActionResult> OnPostBulkAsync([FromForm] BulkInput input, CancellationToken ct)
    {
        var back = SafeReturn(input.ReturnUrl);
        try
        {
            if (input.Action == BulkActions.Purge)
            {
                // Bulk permanent delete needs re-authentication (SR-DEL-1).
                var user = await users.GetUserAsync(User);
                if (user is null || string.IsNullOrEmpty(input.Password) || !await users.CheckPasswordAsync(user, input.Password))
                {
                    TempData.Toast("That password isn't right. Nothing was deleted.", ToastKind.Error);
                    return Redirect(back);
                }
            }

            var outcome = await bulk.RunAsync(new BulkRequest
            {
                Action = input.Action,
                TicketIds = input.Ids,
                AllMatching = input.AllMatching,
                Query = input.Query,
                ConfirmedCount = input.ConfirmedCount,
                GroupChoice = input.GroupChoice,
                AgentChoice = input.AgentChoice,
                StatusId = input.StatusId,
                Priority = input.Priority,
                Tags = (input.Tags ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                PrimaryId = input.PrimaryId,
            }, ct);

            if (outcome.Status == BulkJobStatus.Queued || outcome.Failed > 0)
            {
                return Redirect($"/tickets/jobs/{outcome.JobId}?back={Uri.EscapeDataString(back)}");
            }
            TempData.Toast(BulkToast(input.Action, outcome.Succeeded));
            return Redirect(back);
        }
        catch (Exception ex) when (ex is ValidationException or ForbiddenException or ConflictException)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
            return Redirect(back);
        }
    }

    private static string BulkToast(string action, int n)
    {
        var tickets = n == 1 ? "1 ticket" : $"{n} tickets";
        return action switch
        {
            BulkActions.Merge => "Tickets merged.",
            BulkActions.Spam => $"Marked {tickets} as spam.",
            BulkActions.NotSpam => $"Moved {tickets} out of spam.",
            BulkActions.Trash => $"Moved {tickets} to trash.",
            BulkActions.Restore => $"Restored {tickets}.",
            BulkActions.Purge => $"Permanently deleted {tickets}.",
            _ => $"Updated {tickets}.",
        };
    }

    // ---------------------------------------------------------------- board

    public sealed class MoveInput
    {
        public Guid Id { get; set; }
        public string By { get; set; } = "status";
        public string Value { get; set; } = "";
    }

    /// <summary>Kanban drop or "Move to" menu: updates one property through the ticket service.</summary>
    public async Task<IActionResult> OnPostMoveAsync([FromForm] MoveInput input, CancellationToken ct)
    {
        var ticket = await tickets.GetForAgentAsync(input.Id, ct);
        TicketPatch patch;
        switch (input.By)
        {
            case "status":
                var status = await db.TicketStatuses.AsNoTracking()
                    .Where(s => s.WorkspaceId == ticket.WorkspaceId && s.Name == input.Value)
                    .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct)
                    ?? throw new ValidationException("status", "That status isn't available for this ticket.");
                patch = new TicketPatch { StatusId = status };
                break;
            case "priority":
                patch = Enum.TryParse<Priority>(input.Value, out var priority) && Enum.IsDefined(priority)
                    ? new TicketPatch { Priority = priority }
                    : throw new ValidationException("priority", "Choose a priority.");
                break;
            case "group":
                patch = new TicketPatch { GroupId = ParseOptionalId(input.Value) };
                break;
            case "agent":
                patch = new TicketPatch { AgentId = ParseOptionalId(input.Value) };
                break;
            default:
                throw new ValidationException("by", "Choose how the board is grouped.");
        }
        await tickets.UpdateAsync(ticket.Id, patch, ct: ct);
        return new JsonResult(new { ok = true, message = $"{ticket.DisplayId} moved." });
    }

    private static Guid? ParseOptionalId(string value) =>
        string.IsNullOrEmpty(value) || value == "none" ? null
        : Guid.TryParse(value, out var id) ? id
        : throw new ValidationException("value", "Choose a column from the list.");

    // ---------------------------------------------------------------- saved views

    public sealed class SaveViewInput
    {
        public Guid? Id { get; set; }
        public string Name { get; set; } = "";
        public string? Query { get; set; }
        public string? Sort { get; set; }
        public string? Cols { get; set; }
        public bool Shared { get; set; }
    }

    public async Task<IActionResult> OnPostSaveViewAsync([FromForm] SaveViewInput input, CancellationToken ct)
    {
        try
        {
            var data = new SavedViewInput(input.Name, input.Query ?? "", input.Sort, input.Cols is null ? null : [input.Cols], input.Shared);
            var view = input.Id is { } id ? await savedViews.UpdateAsync(id, data, ct) : await savedViews.CreateAsync(data, ct);
            TempData.Toast(input.Id is null ? $"View \"{view.Name}\" saved." : $"View \"{view.Name}\" updated.");
            return Redirect(new QueueState { Saved = view.Id }.ToUrl());
        }
        catch (Exception ex) when (ex is ValidationException or ForbiddenException)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
            return Redirect(new QueueState { Q = input.Query, Sort = input.Sort, Cols = input.Cols }.ToUrl());
        }
    }

    public async Task<IActionResult> OnPostDeleteViewAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await savedViews.DeleteAsync(id, ct);
            TempData.Toast("View deleted.");
        }
        catch (Exception ex) when (ex is ForbiddenException or NotFoundException)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return Redirect("/tickets");
    }

    // ---------------------------------------------------------------- loading

    /// <summary>Works out the effective query. Returns a redirect when a saved view no longer exists.</summary>
    private async Task<IActionResult?> ResolveAsync(CancellationToken ct)
    {
        MeId = actors.Current.UserId ?? Guid.Empty;
        var state = new QueueState
        {
            View = string.IsNullOrWhiteSpace(View) ? null : View,
            Saved = Saved,
            Q = Q is null ? null : Q.Trim(),
            Sort = string.IsNullOrWhiteSpace(Sort) ? null : Sort,
            Page = Math.Max(1, PageNumber),
            Layout = Layout == "board" ? "board" : "table",
            By = QueueState.BoardGroupings.Any(b => b.Key == By) ? By! : "status",
            Cols = string.IsNullOrWhiteSpace(Cols) ? null : Cols,
        };

        SdSavedView? saved = null;
        if (state.Saved is { } savedId)
        {
            try
            {
                saved = await savedViews.GetAsync(savedId, ct);
            }
            catch (NotFoundException)
            {
                TempData.Toast("That view was deleted or isn't shared with you.", ToastKind.Error);
                return Redirect("/tickets");
            }
        }
        var view = saved is null ? QueueViews.Find(state.View) : null;
        if (saved is null && view is null && state.Q is null)
        {
            view = QueueViews.Find(QueueViews.Waiting);
        }

        var query = state.Q ?? saved?.Query ?? view?.Query ?? "";
        // A filter that matches a built-in view highlights it.
        var activeView = view is not null && (state.Q is null || state.Q == view.Query) ? view
            : saved is null ? QueueViews.All.FirstOrDefault(v => v.Query == query) : null;
        var activeSaved = saved is not null && (state.Q is null || state.Q == saved.Query) ? saved : null;

        var p = await permissions.CurrentAsync(ct);
        Queue = new QueueContext
        {
            State = state,
            Query = query,
            Sort = QueueViews.NormalizeSort(state.Sort ?? activeSaved?.Sort ?? activeView?.Sort ?? "lamp"),
            Columns = QueueColumns.Normalize(state.Cols is not null ? [state.Cols] : activeSaved?.Columns),
            Title = activeSaved?.Name ?? activeView?.Title ?? "Filtered tickets",
            Description = activeSaved is not null ? (activeSaved.IsShared ? "A shared view." : "Your saved view.") : activeView?.Description,
            ActiveView = activeView,
            ActiveSaved = activeSaved,
            CanBulk = p?.Has(Privileges.TicketsBulk) == true,
            CanExport = p?.Has(Privileges.TicketsExport) == true,
            CanPurge = p?.Has(Privileges.TicketsDeletePermanently) == true,
            CanShareViews = p?.Has(Privileges.SettingsManage) == true,
        };
        return null;
    }

    private async Task<QueueResults> LoadResultsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        try
        {
            if (Queue.IsBoard)
            {
                var items = new List<TicketListItem>();
                var total = 0;
                for (var page = 1; items.Count < BoardLimit; page++)
                {
                    var batch = await search.SearchAsync(new TicketSearchRequest { Query = Queue.Query, Sort = Queue.Sort, Page = page, PageSize = 100 }, ct);
                    items.AddRange(batch.Items);
                    total = batch.Total;
                    if (!batch.HasMore)
                    {
                        break;
                    }
                }
                return new QueueResults { Context = Queue, Board = await BuildBoardAsync(items, ct), BoardTotal = total, Now = now };
            }
            var result = await search.SearchAsync(new TicketSearchRequest { Query = Queue.Query, Sort = Queue.Sort, Page = Queue.State.Page, PageSize = PageSize }, ct);
            return new QueueResults { Context = Queue, Result = result, Now = now };
        }
        catch (ValidationException ex)
        {
            return new QueueResults { Context = Queue, Error = ex.Message, Now = now };
        }
    }

    private async Task<IReadOnlyList<BoardColumn>> BuildBoardAsync(List<TicketListItem> items, CancellationToken ct)
    {
        switch (Queue.State.By)
        {
            case "priority":
                return Enum.GetValues<Priority>().Reverse()
                    .Select(pr => new BoardColumn(pr.ToString(), pr.ToString(), pr.ToString(), items.Where(i => i.Priority == pr).ToList()))
                    .ToList();
            case "group":
            {
                var groups = await db.AgentGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync(ct);
                var columns = new List<BoardColumn> { new("none", "No group", "none", items.Where(i => i.GroupId is null).ToList()) };
                columns.AddRange(groups.Select(g => new BoardColumn(g.Id.ToString(), g.Name, g.Id.ToString(), items.Where(i => i.GroupId == g.Id).ToList())));
                return columns;
            }
            case "agent":
            {
                var agentIds = items.Where(i => i.AgentId is not null).Select(i => i.AgentId!.Value).Append(MeId).Distinct().ToList();
                var agents = await db.Users.AsNoTracking().Where(u => agentIds.Contains(u.Id)).OrderBy(u => u.DisplayName)
                    .Select(u => new { u.Id, u.DisplayName }).ToListAsync(ct);
                var columns = new List<BoardColumn> { new("none", "Unassigned", "none", items.Where(i => i.AgentId is null).ToList()) };
                columns.AddRange(agents.OrderByDescending(a => a.Id == MeId)
                    .Select(a => new BoardColumn(a.Id.ToString(), a.Id == MeId ? $"{a.DisplayName} (you)" : a.DisplayName, a.Id.ToString(),
                        items.Where(i => i.AgentId == a.Id).ToList())));
                return columns;
            }
            default:
            {
                var statuses = await db.TicketStatuses.AsNoTracking().OrderBy(s => s.Category).ThenBy(s => s.Order)
                    .Select(s => new { s.Name, s.Category }).ToListAsync(ct);
                return statuses.GroupBy(s => s.Name).Select(g => g.First())
                    .Select(s => new BoardColumn(s.Name, s.Name, s.Name, items.Where(i => i.StatusName == s.Name).ToList(), s.Category))
                    .ToList();
            }
        }
    }

    private async Task LoadSidebarAsync(CancellationToken ct)
    {
        SavedViews = await savedViews.ListAsync(ct);
        foreach (var v in SavedViews)
        {
            if (await savedViews.CanEditAsync(v, ct))
            {
                EditableSavedViews.Add(v.Id);
            }
        }
        var counts = new Dictionary<string, int?>();
        foreach (var view in QueueViews.All)
        {
            counts[view.Key] = await SafeCountAsync(view.Query, ct);
        }
        foreach (var view in SavedViews)
        {
            counts[view.Id.ToString()] = await SafeCountAsync(view.Query, ct);
        }
        Counts = counts;
    }

    private async Task<int?> SafeCountAsync(string query, CancellationToken ct)
    {
        try
        {
            return await search.CountAsync(query, ct);
        }
        catch (ValidationException)
        {
            return null;
        }
    }

    private async Task LoadFilterChoicesAsync(CancellationToken ct)
    {
        var p = await permissions.CurrentAsync(ct);
        var workspaces = p is null ? [] : p.IsOrgAdmin
            ? await db.Workspaces.AsNoTracking().Select(w => w.Id).ToListAsync(ct)
            : p.WorkspacesWith(Privileges.TicketsView).ToList();
        StatusNames = await db.TicketStatuses.AsNoTracking().Where(s => workspaces.Contains(s.WorkspaceId))
            .OrderBy(s => s.Category).ThenBy(s => s.Order).Select(s => s.Name).Distinct().ToListAsync(ct);
        var primary = await db.Workspaces.AsNoTracking().Where(w => w.IsPrimary).Select(w => w.Id).FirstOrDefaultAsync(ct);
        BulkStatuses = await db.TicketStatuses.AsNoTracking().Where(s => s.WorkspaceId == primary).OrderBy(s => s.Order)
            .Select(s => new StatusOption(s.Id, s.Name, s.Category, s.Order)).ToListAsync(ct);
        Groups = await db.AgentGroups.AsNoTracking().Where(g => workspaces.Contains(g.WorkspaceId)).OrderBy(g => g.Name)
            .Select(g => new NamedOption(g.Id, g.Name)).ToListAsync(ct);
        Agents = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && (u.IsOrgAdmin || u.Memberships.Any(m => workspaces.Contains(m.WorkspaceId))))
            .OrderBy(u => u.DisplayName).Select(u => new PersonOption(u.Id, u.DisplayName, u.Email, null, null, false, true)).ToListAsync(ct);
    }

    private string SafeReturn(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith("/tickets", StringComparison.Ordinal) && !url.StartsWith("//", StringComparison.Ordinal) ? url : "/tickets";

    /// <summary>A filter term for the quick menus, quoting values with spaces.</summary>
    public static string Term(string field, string value) =>
        value.Any(char.IsWhiteSpace) || value.Contains('"') ? $"{field}:\"{value.Replace("\"", "\\\"")}\"" : $"{field}:{value}";
}
