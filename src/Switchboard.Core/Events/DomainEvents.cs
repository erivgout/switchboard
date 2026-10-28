using Switchboard.Core.Domain;
using Switchboard.Core.Security;

namespace Switchboard.Core.Events;

/// <summary>
/// Something that happened in the domain. Raise with <c>SwitchboardDbContext.Raise</c>; it is
/// dispatched to <see cref="IPreCommitHandler{TEvent}"/> inside the same transaction, then persisted
/// to the outbox and delivered to <see cref="IEventHandler{TEvent}"/> after commit (at least once).
/// </summary>
public abstract record DomainEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset OccurredAt { get; init; }
    public ActorSnapshot? Actor { get; init; }

    /// <summary>How many automation hops produced this event (SR-AUT-1).</summary>
    public int CascadeDepth { get; init; }
}

/// <summary>
/// Runs synchronously inside <c>SaveChangesAsync</c>, before commit. May modify tracked entities
/// and raise further events. Keep it fast and free of external I/O.
/// </summary>
public interface IPreCommitHandler<in TEvent> where TEvent : DomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Runs after commit via the outbox, at least once, in its own DI scope as the system actor.
/// Must be idempotent; a completed handler is not re-run for the same event.
/// </summary>
public interface IEventHandler<in TEvent> where TEvent : DomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}

/// <summary>Implemented by events about one ticket, so handlers (automations, webhooks) can treat them uniformly.</summary>
public interface ITicketEvent
{
    Guid TicketId { get; }
    Guid WorkspaceId { get; }
}

public sealed record TicketCreated(Guid TicketId, Guid WorkspaceId, TicketType Type) : DomainEvent, ITicketEvent;

/// <summary>Raised once per save that changed ticket properties (not conversations).</summary>
public sealed record TicketUpdated(Guid TicketId, Guid WorkspaceId, IReadOnlyList<FieldChange> Changes) : DomainEvent, ITicketEvent;

public sealed record TicketStatusChanged(
    Guid TicketId,
    Guid WorkspaceId,
    Guid FromStatusId,
    Guid ToStatusId,
    StatusCategory FromCategory,
    StatusCategory ToCategory) : DomainEvent, ITicketEvent;

public sealed record TicketAssigned(
    Guid TicketId,
    Guid WorkspaceId,
    Guid? FromGroupId,
    Guid? ToGroupId,
    Guid? FromAgentId,
    Guid? ToAgentId) : DomainEvent, ITicketEvent;

public sealed record TicketConversationAdded(
    Guid TicketId,
    Guid WorkspaceId,
    Guid ConversationId,
    ConversationKind Kind,
    bool IsPrivate,
    bool ByAgent) : DomainEvent, ITicketEvent;

/// <summary>Agents @mentioned in a note.</summary>
public sealed record AgentsMentioned(Guid TicketId, Guid ConversationId, IReadOnlyList<Guid> UserIds) : DomainEvent;

public sealed record TicketsMerged(Guid PrimaryTicketId, IReadOnlyList<Guid> SecondaryTicketIds) : DomainEvent;

public sealed record TicketDeleted(Guid TicketId, Guid WorkspaceId, bool Permanently) : DomainEvent, ITicketEvent;

public sealed record TicketRestored(Guid TicketId, Guid WorkspaceId) : DomainEvent, ITicketEvent;

public sealed record TicketTaskChanged(Guid TicketId, Guid TaskId, TicketTaskStatus Status, Guid? AssigneeId, bool Created) : DomainEvent;
