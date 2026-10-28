using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.ServiceDesk.Data;

namespace Switchboard.Modules.ServiceDesk.Services;

public static class BulkActions
{
    public const string Assign = "assign";
    public const string Status = "status";
    public const string Priority = "priority";
    public const string AddTags = "tags";
    public const string Merge = "merge";
    public const string Spam = "spam";
    public const string NotSpam = "not_spam";
    public const string Trash = "trash";
    public const string Restore = "restore";
    public const string Purge = "purge";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Assign, Status, Priority, AddTags, Merge, Spam, NotSpam, Trash, Restore, Purge,
    };
}

/// <summary>What to do and to which tickets. Either <see cref="TicketIds"/> or every ticket matching <see cref="Query"/>.</summary>
public sealed record BulkRequest
{
    public string Action { get; init; } = "";
    public IReadOnlyList<Guid> TicketIds { get; init; } = [];
    public bool AllMatching { get; init; }
    public string? Query { get; init; }
    /// <summary>Required above <see cref="BulkActionService.InlineLimit"/>: the count the agent confirmed (SR-AUT-5).</summary>
    public int? ConfirmedCount { get; init; }
    /// <summary>Empty: leave as is. <c>none</c>: clear. Otherwise a group id.</summary>
    public string? GroupChoice { get; init; }
    /// <summary>Empty: leave as is. <c>none</c>: unassign. Otherwise an agent id.</summary>
    public string? AgentChoice { get; init; }
    public Guid? StatusId { get; init; }
    public Priority? Priority { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public Guid? PrimaryId { get; init; }
}

public sealed record BulkOutcome(Guid? JobId, BulkJobStatus Status, int Total, int Succeeded, int Failed, string Summary);

/// <summary>
/// Bulk actions on tickets (FR-TKT-21). Needs <c>tickets.bulk</c>, and every record still goes through
/// <see cref="ITicketService"/> so its own privilege checks apply. Up to <see cref="InlineLimit"/> records run
/// in the request; more run as a tracked background job with per-record results (SR-AUT-5).
/// </summary>
public sealed class BulkActionService(
    SwitchboardDbContext db,
    IActorAccessor actors,
    IPermissionService permissions,
    ITicketSearch search,
    ITicketService tickets,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<BulkActionService> logger)
{
    public const int InlineLimit = 100;
    public const int MaxRecords = 5000;

    private sealed record Target(Guid Id, TicketType Type, int Number, Guid WorkspaceId)
    {
        public string DisplayId => Ticket.FormatDisplayId(Type, Number);
    }

    /// <summary>Counts the tickets a request would touch (for the confirmation step).</summary>
    public async Task<int> CountAsync(BulkRequest request, CancellationToken ct = default) =>
        (await ResolveTargetsAsync(request, ct)).Count;

    public async Task<BulkOutcome> RunAsync(BulkRequest request, CancellationToken ct = default)
    {
        var actor = actors.Current;
        if (actor.UserId is not { } userId || actor.IsInternal)
        {
            throw new ForbiddenException();
        }
        await permissions.DemandAsync(Privileges.TicketsBulk, ct: ct);
        if (!BulkActions.All.Contains(request.Action))
        {
            throw new ValidationException("action", "Choose what to do with the selected tickets.");
        }

        var targets = await ResolveTargetsAsync(request, ct);
        if (targets.Count == 0)
        {
            throw new ValidationException("tickets", "Select at least one ticket.");
        }
        if (targets.Count > InlineLimit && request.ConfirmedCount != targets.Count)
        {
            throw new ValidationException("confirmedCount", $"This changes {targets.Count} tickets. Type {targets.Count} to confirm.");
        }

        var summary = await DescribeAsync(request, ct);
        if (request.Action == BulkActions.Merge)
        {
            return await MergeAsync(request, targets, summary, ct);
        }

        var now = time.GetUtcNow();
        var job = new SdBulkJob
        {
            CreatedById = userId,
            CreatedByName = actor.DisplayName,
            Action = request.Action,
            ParametersJson = JsonSerializer.Serialize(request with { TicketIds = [], Query = null, AllMatching = false }),
            Summary = summary,
            Total = targets.Count,
            Status = targets.Count > InlineLimit ? BulkJobStatus.Queued : BulkJobStatus.Running,
            CreatedAt = now,
            StartedAt = targets.Count > InlineLimit ? null : now,
            Items = targets.Select((t, i) => new SdBulkJobItem { TicketId = t.Id, Number = t.Number, DisplayId = t.DisplayId, Order = i }).ToList(),
        };
        db.Set<SdBulkJob>().Add(job);
        await db.SaveChangesAsync(ct);

        if (job.Status == BulkJobStatus.Queued)
        {
            return new BulkOutcome(job.Id, job.Status, job.Total, 0, 0, summary);
        }

        await ProcessJobAsync(scopes, job.Id, actor, logger, time, ct);
        var done = await db.Set<SdBulkJob>().AsNoTracking().FirstAsync(j => j.Id == job.Id, ct);
        return new BulkOutcome(done.Id, done.Status, done.Total, done.Succeeded, done.Failed, summary);
    }

    /// <summary>A job the current user started (or any job, for org admins).</summary>
    public async Task<SdBulkJob> GetJobAsync(Guid id, CancellationToken ct = default)
    {
        var me = actors.Current.UserId;
        var p = await permissions.CurrentAsync(ct);
        var job = await db.Set<SdBulkJob>().AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null || (job.CreatedById != me && p?.IsOrgAdmin != true))
        {
            throw new NotFoundException();
        }
        return job;
    }

    public async Task<IReadOnlyList<SdBulkJobItem>> GetItemsAsync(Guid jobId, CancellationToken ct = default) =>
        await db.Set<SdBulkJobItem>().AsNoTracking()
            .Where(i => i.JobId == jobId)
            .OrderBy(i => i.Status == BulkItemStatus.Failed ? 0 : i.Status == BulkItemStatus.Pending ? 1 : 2)
            .ThenBy(i => i.Order)
            .Take(1000)
            .ToListAsync(ct);

    // ---------------------------------------------------------------- processing

    /// <summary>Processes the pending items of a job as <paramref name="actor"/>. Each record runs in its own scope.</summary>
    internal static async Task ProcessJobAsync(IServiceScopeFactory scopes, Guid jobId, Actor actor, ILogger logger, TimeProvider time, CancellationToken ct)
    {
        await using var bookkeeping = scopes.CreateAsyncScope();
        var db = bookkeeping.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
        using var system = bookkeeping.ServiceProvider.GetRequiredService<IActorAccessor>().BeginScope(Actor.System("Bulk actions"));
        var job = await db.Set<SdBulkJob>().FirstAsync(j => j.Id == jobId, ct);
        var request = JsonSerializer.Deserialize<BulkRequest>(job.ParametersJson) ?? new BulkRequest();
        var items = await db.Set<SdBulkJobItem>().Where(i => i.JobId == jobId && i.Status == BulkItemStatus.Pending).OrderBy(i => i.Order).ToListAsync(ct);

        var processed = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var error = await ProcessItemAsync(scopes, actor, request, item.TicketId, logger, ct);
            item.Status = error is null ? BulkItemStatus.Succeeded : BulkItemStatus.Failed;
            item.Error = error is null ? null : error.Length > 1000 ? error[..1000] : error;
            if (error is null)
            {
                job.Succeeded++;
            }
            else
            {
                job.Failed++;
            }
            if (++processed % 10 == 0)
            {
                await db.SaveChangesAsync(ct);
            }
        }

        job.Status = BulkJobStatus.Completed;
        job.CompletedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private static async Task<string?> ProcessItemAsync(IServiceScopeFactory scopes, Actor actor, BulkRequest request, Guid ticketId, ILogger logger, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        using var _ = sp.GetRequiredService<IActorAccessor>().BeginScope(actor);
        var tickets = sp.GetRequiredService<ITicketService>();
        var permissions = sp.GetRequiredService<IPermissionService>();
        var db = sp.GetRequiredService<SwitchboardDbContext>();
        try
        {
            var ticket = await tickets.GetForAgentAsync(ticketId, ct);
            await permissions.DemandAsync(Privileges.TicketsBulk, ticket.WorkspaceId, ct);
            switch (request.Action)
            {
                case BulkActions.Assign:
                    await tickets.UpdateAsync(ticket.Id, new TicketPatch
                    {
                        GroupId = Choice(request.GroupChoice),
                        AgentId = Choice(request.AgentChoice),
                    }, ct: ct);
                    break;
                case BulkActions.Status:
                    var statusId = await StatusForWorkspaceAsync(db, request.StatusId!.Value, ticket.WorkspaceId, ct)
                        ?? throw new ValidationException("status", "That status doesn't exist in this ticket's workspace.");
                    await tickets.UpdateAsync(ticket.Id, new TicketPatch { StatusId = statusId }, ct: ct);
                    break;
                case BulkActions.Priority:
                    await tickets.UpdateAsync(ticket.Id, new TicketPatch { Priority = request.Priority!.Value }, ct: ct);
                    break;
                case BulkActions.AddTags:
                    var current = await db.TicketTags.AsNoTracking().Where(t => t.TicketId == ticket.Id).Select(t => t.Tag).ToListAsync(ct);
                    await tickets.UpdateAsync(ticket.Id, new TicketPatch { Tags = current.Concat(request.Tags ?? []).ToArray() }, ct: ct);
                    break;
                case BulkActions.Spam:
                    await tickets.SetSpamAsync(ticket.Id, true, ct);
                    break;
                case BulkActions.NotSpam:
                    await tickets.SetSpamAsync(ticket.Id, false, ct);
                    break;
                case BulkActions.Trash:
                    await tickets.MoveToTrashAsync(ticket.Id, ct);
                    break;
                case BulkActions.Restore:
                    await tickets.RestoreAsync(ticket.Id, ct);
                    break;
                case BulkActions.Purge:
                    await tickets.PurgeAsync(ticket.Id, ct);
                    break;
                default:
                    return "That action isn't available.";
            }
            return null;
        }
        catch (ForbiddenException)
        {
            return "You don't have permission to change this ticket.";
        }
        catch (NotFoundException)
        {
            return "This ticket no longer exists or you can't see it.";
        }
        catch (Exception ex) when (ex is ValidationException or ConflictException)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Bulk action {Action} failed for ticket {TicketId}", request.Action, ticketId);
            return "Something went wrong with this ticket. Try it on its own.";
        }
    }

    private static Optional<Guid?> Choice(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Optional<Guid?>.None
        : value == "none" ? new Optional<Guid?>(null)
        : Guid.TryParse(value, out var id) ? new Optional<Guid?>(id)
        : throw new ValidationException("assign", "Choose a group or agent from the list.");

    private static async Task<Guid?> StatusForWorkspaceAsync(SwitchboardDbContext db, Guid statusId, Guid workspaceId, CancellationToken ct)
    {
        var chosen = await db.TicketStatuses.AsNoTracking().FirstOrDefaultAsync(s => s.Id == statusId, ct);
        if (chosen is null)
        {
            return null;
        }
        return chosen.WorkspaceId == workspaceId
            ? chosen.Id
            : await db.TicketStatuses.AsNoTracking().Where(s => s.WorkspaceId == workspaceId && s.Name == chosen.Name).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<List<Target>> ResolveTargetsAsync(BulkRequest request, CancellationToken ct)
    {
        IQueryable<Ticket> query;
        if (request.AllMatching)
        {
            query = await search.QueryAsync(request.Query, ct: ct);
        }
        else
        {
            var ids = request.TicketIds.Distinct().ToList();
            if (ids.Count == 0)
            {
                return [];
            }
            if (ids.Count > MaxRecords)
            {
                throw new ValidationException("tickets", $"Bulk actions work on up to {MaxRecords} tickets at a time.");
            }
            query = (await permissions.AgentVisibleTicketsAsync(ct)).AsNoTracking().Where(t => ids.Contains(t.Id) && t.PurgedAt == null);
        }

        var targets = await query
            .OrderBy(t => t.Number)
            .Select(t => new Target(t.Id, t.Type, t.Number, t.WorkspaceId))
            .Take(MaxRecords + 1)
            .ToListAsync(ct);
        if (targets.Count > MaxRecords)
        {
            throw new ValidationException("tickets", $"Bulk actions work on up to {MaxRecords} tickets at a time. Narrow the filter first.");
        }
        return targets;
    }

    private async Task<BulkOutcome> MergeAsync(BulkRequest request, List<Target> targets, string summary, CancellationToken ct)
    {
        if (targets.Count < 2)
        {
            throw new ValidationException("tickets", "Select at least two tickets to merge.");
        }
        if (targets.Count > InlineLimit)
        {
            throw new ValidationException("tickets", $"Merge up to {InlineLimit} tickets at a time.");
        }
        var primary = targets.FirstOrDefault(t => t.Id == request.PrimaryId)
            ?? throw new ValidationException("primaryId", "Choose which ticket the others merge into.");
        foreach (var workspaceId in targets.Select(t => t.WorkspaceId).Distinct())
        {
            await permissions.DemandAsync(Privileges.TicketsBulk, workspaceId, ct);
        }
        await tickets.MergeAsync(primary.Id, targets.Where(t => t.Id != primary.Id).Select(t => t.Id).ToList(), ct);
        return new BulkOutcome(null, BulkJobStatus.Completed, targets.Count, targets.Count, 0, summary);
    }

    private async Task<string> DescribeAsync(BulkRequest request, CancellationToken ct)
    {
        switch (request.Action)
        {
            case BulkActions.Assign:
            {
                if (string.IsNullOrWhiteSpace(request.GroupChoice) && string.IsNullOrWhiteSpace(request.AgentChoice))
                {
                    throw new ValidationException("assign", "Choose a group, an agent, or both.");
                }
                var parts = new List<string>();
                var group = Choice(request.GroupChoice);
                var agent = Choice(request.AgentChoice);
                if (group.HasValue)
                {
                    parts.Add(group.Value is { } g
                        ? $"assign to {await db.AgentGroups.AsNoTracking().Where(x => x.Id == g).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? "the group"}"
                        : "clear the group");
                }
                if (agent.HasValue)
                {
                    parts.Add(agent.Value is { } a
                        ? $"assign to {await db.Users.AsNoTracking().Where(x => x.Id == a).Select(x => x.DisplayName).FirstOrDefaultAsync(ct) ?? "the agent"}"
                        : "unassign the agent");
                }
                var text = string.Join(" and ", parts);
                return char.ToUpperInvariant(text[0]) + text[1..];
            }
            case BulkActions.Status:
            {
                var name = request.StatusId is { } s
                    ? await db.TicketStatuses.AsNoTracking().Where(x => x.Id == s).Select(x => x.Name).FirstOrDefaultAsync(ct)
                    : null;
                return name is null ? throw new ValidationException("statusId", "Choose a status.") : $"Set status to {name}";
            }
            case BulkActions.Priority:
                return request.Priority is { } p && Enum.IsDefined(p) ? $"Set priority to {p}" : throw new ValidationException("priority", "Choose a priority.");
            case BulkActions.AddTags:
            {
                var tags = (request.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                return tags.Count == 0 ? throw new ValidationException("tags", "Enter at least one tag.") : $"Add tags: {string.Join(", ", tags)}";
            }
            case BulkActions.Merge:
                return "Merge tickets";
            case BulkActions.Spam:
                return "Mark as spam";
            case BulkActions.NotSpam:
                return "Mark as not spam";
            case BulkActions.Trash:
                return "Move to trash";
            case BulkActions.Restore:
                return "Restore from trash";
            case BulkActions.Purge:
                await permissions.DemandAsync(Privileges.TicketsDeletePermanently, ct: ct);
                return "Delete permanently";
            default:
                throw new ValidationException("action", "Choose what to do with the selected tickets.");
        }
    }
}
