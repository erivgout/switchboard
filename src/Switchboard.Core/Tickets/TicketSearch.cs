using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;

namespace Switchboard.Core.Tickets;

public sealed record TicketSearchRequest
{
    /// <summary>Filter language query (see <see cref="TicketQueryLanguage"/>).</summary>
    public string? Query { get; init; }
    /// <summary><c>updated</c>, <c>created</c>, <c>due</c>, <c>priority</c>, <c>number</c>, <c>lamp</c>; prefix <c>-</c> for descending. Default <c>-updated</c>.</summary>
    public string? Sort { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    /// <summary>Search what the current user can see in the portal instead of the agent queue.</summary>
    public bool AsRequester { get; init; }
}

/// <summary>One queue row. Projected in SQL; safe to serialize.</summary>
public sealed record TicketListItem
{
    public Guid Id { get; init; }
    public int Number { get; init; }
    public TicketType Type { get; init; }
    public string DisplayId => Ticket.FormatDisplayId(Type, Number);
    public Guid WorkspaceId { get; init; }
    public string Subject { get; init; } = "";
    public Guid StatusId { get; init; }
    public string StatusName { get; init; } = "";
    public string? StatusRequesterLabel { get; init; }
    public StatusCategory StatusCategory { get; init; }
    public Priority Priority { get; init; }
    public Guid? GroupId { get; init; }
    public string? GroupName { get; init; }
    public Guid? AgentId { get; init; }
    public string? AgentName { get; init; }
    public Guid RequesterId { get; init; }
    public string RequesterName { get; init; } = "";
    public string? RequesterEmail { get; init; }
    public string SourceKey { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? DueBy { get; init; }
    public DateTimeOffset? FirstResponseDueBy { get; init; }
    public DateTimeOffset? FirstRespondedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public bool IsLampLit { get; init; }
    public bool IsMajorIncident { get; init; }
    public bool IsSpam { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    /// <summary>Concurrency token; send it back as the expected version when updating (e.g. board moves).</summary>
    public byte[] RowVersion { get; init; } = [];
}

public sealed record TicketSearchResult(IReadOnlyList<TicketListItem> Items, int Total, int Page, int PageSize)
{
    public bool HasMore => Page * PageSize < Total;
    public int PageCount => PageSize == 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public interface ITicketSearch
{
    Task<TicketSearchResult> SearchAsync(TicketSearchRequest request, CancellationToken ct = default);

    /// <summary>Count of visible tickets matching a query (view badges, lamp counter).</summary>
    Task<int> CountAsync(string? query, CancellationToken ct = default);

    /// <summary>Visible tickets matching a query, for custom projections (reports, exports, bulk).</summary>
    Task<IQueryable<Ticket>> QueryAsync(string? query, bool asRequester = false, CancellationToken ct = default);
}

public sealed class TicketSearch(IPermissionService permissions, TicketQueryCompiler compiler) : ITicketSearch
{
    public async Task<TicketSearchResult> SearchAsync(TicketSearchRequest request, CancellationToken ct = default)
    {
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 100);
        var query = await QueryAsync(request.Query, request.AsRequester, ct);
        var total = await query.CountAsync(ct);
        var items = await Sort(Project(query), request.Sort)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);
        return new TicketSearchResult(items, total, page, size);
    }

    public async Task<int> CountAsync(string? query, CancellationToken ct = default) =>
        await (await QueryAsync(query, false, ct)).CountAsync(ct);

    public async Task<IQueryable<Ticket>> QueryAsync(string? query, bool asRequester = false, CancellationToken ct = default)
    {
        var node = TicketQueryLanguage.Parse(query);
        var predicate = await compiler.CompileAsync(node, asRequester, ct);
        var visible = asRequester
            ? await permissions.RequesterVisibleTicketsAsync(ct)
            : await permissions.AgentVisibleTicketsAsync(ct);
        return visible.AsNoTracking().Where(predicate);
    }

    private static IQueryable<TicketListItem> Project(IQueryable<Ticket> query) =>
        query.Select(t => new TicketListItem
        {
            Id = t.Id,
            Number = t.Number,
            Type = t.Type,
            WorkspaceId = t.WorkspaceId,
            Subject = t.Subject,
            StatusId = t.StatusId,
            StatusName = t.Status!.Name,
            StatusRequesterLabel = t.Status.RequesterLabel,
            StatusCategory = t.Status.Category,
            Priority = t.Priority,
            GroupId = t.GroupId,
            GroupName = t.Group != null ? t.Group.Name : null,
            AgentId = t.AgentId,
            AgentName = t.Agent != null ? t.Agent.DisplayName : null,
            RequesterId = t.RequesterId,
            RequesterName = t.Requester!.DisplayName,
            RequesterEmail = t.Requester.Email,
            SourceKey = t.Source!.Key,
            CreatedAt = t.CreatedAt,
            UpdatedAt = t.UpdatedAt,
            DueBy = t.DueBy,
            FirstResponseDueBy = t.FirstResponseDueBy,
            FirstRespondedAt = t.FirstRespondedAt,
            ResolvedAt = t.ResolvedAt,
            IsLampLit = t.AwaitingAgentSince != null
                && (t.Status.Category == StatusCategory.Open || t.Status.Category == StatusCategory.Pending)
                && t.DeletedAt == null && !t.IsSpam && t.MergedIntoId == null,
            IsMajorIncident = t.IsMajorIncident,
            IsSpam = t.IsSpam,
            DeletedAt = t.DeletedAt,
            RowVersion = t.RowVersion,
        });

    private static IQueryable<TicketListItem> Sort(IQueryable<TicketListItem> query, string? sort)
    {
        var key = string.IsNullOrWhiteSpace(sort) ? "-updated" : sort.Trim().ToLowerInvariant();
        var descending = key.StartsWith('-');
        key = key.TrimStart('-', '+');
        IOrderedQueryable<TicketListItem> ordered = key switch
        {
            "created" => Order(query, x => x.CreatedAt, descending),
            "due" => descending
                ? query.OrderBy(x => x.DueBy == null).ThenByDescending(x => x.DueBy)
                : query.OrderBy(x => x.DueBy == null).ThenBy(x => x.DueBy),
            "priority" => Order(query, x => x.Priority, descending),
            "number" => Order(query, x => x.Number, descending),
            "lamp" => query.OrderByDescending(x => x.IsLampLit).ThenBy(x => x.DueBy == null).ThenBy(x => x.DueBy).ThenByDescending(x => x.UpdatedAt),
            _ => Order(query, x => x.UpdatedAt, descending),
        };
        return ordered.ThenByDescending(x => x.Number);
    }

    private static IOrderedQueryable<TicketListItem> Order<TKey>(IQueryable<TicketListItem> q, Expression<Func<TicketListItem, TKey>> key, bool descending) =>
        descending ? q.OrderByDescending(key) : q.OrderBy(key);
}
