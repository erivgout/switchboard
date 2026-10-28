using Switchboard.Core.Domain;
using Switchboard.Core.Events;

namespace Switchboard.Modules.ServiceLevels.Engine;

/// <summary>Feeds ticket events to <see cref="SlaEngine"/> inside the same transaction.</summary>
internal sealed class SlaPreCommitHandlers(SlaEngine engine) :
    IPreCommitHandler<TicketCreated>,
    IPreCommitHandler<TicketUpdated>,
    IPreCommitHandler<TicketStatusChanged>,
    IPreCommitHandler<TicketDeleted>,
    IPreCommitHandler<TicketRestored>,
    IPreCommitHandler<TicketConversationAdded>
{
    public Task HandleAsync(TicketCreated e, CancellationToken ct) =>
        engine.EvaluateAsync(e.TicketId, new SlaChange(SlaTrigger.Created, e.OccurredAt, e.Actor), ct);

    public Task HandleAsync(TicketUpdated e, CancellationToken ct) =>
        engine.EvaluateAsync(e.TicketId, new SlaChange(SlaTrigger.Updated, e.OccurredAt, e.Actor, e.Changes), ct);

    public Task HandleAsync(TicketStatusChanged e, CancellationToken ct) =>
        engine.EvaluateAsync(e.TicketId, new SlaChange(SlaTrigger.StatusChanged, e.OccurredAt, e.Actor, FromCategory: e.FromCategory, ToCategory: e.ToCategory), ct);

    public Task HandleAsync(TicketDeleted e, CancellationToken ct) =>
        e.Permanently ? Task.CompletedTask : engine.EvaluateAsync(e.TicketId, new SlaChange(SlaTrigger.Trashed, e.OccurredAt, e.Actor), ct);

    public Task HandleAsync(TicketRestored e, CancellationToken ct) =>
        engine.EvaluateAsync(e.TicketId, new SlaChange(SlaTrigger.Restored, e.OccurredAt, e.Actor), ct);

    public Task HandleAsync(TicketConversationAdded e, CancellationToken ct) => e switch
    {
        { ByAgent: false, Kind: ConversationKind.RequesterMessage } =>
            engine.EvaluateAsync(e.TicketId, new SlaChange(SlaTrigger.RequesterMessage, e.OccurredAt, e.Actor), ct),
        { ByAgent: true, Kind: ConversationKind.AgentReply } =>
            engine.EvaluateAsync(e.TicketId, new SlaChange(SlaTrigger.AgentReply, e.OccurredAt, e.Actor), ct),
        _ => Task.CompletedTask,
    };
}
