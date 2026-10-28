using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;

namespace Switchboard.Core.Events;

internal sealed class OutboxSignal : IOutboxSignal
{
    private readonly System.Threading.Channels.Channel<bool> _channel = System.Threading.Channels.Channel.CreateBounded<bool>(new System.Threading.Channels.BoundedChannelOptions(1)
    {
        FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
    });

    public void Notify() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Poll interval elapsed.
        }
    }
}

/// <summary>
/// Delivers committed domain events to <see cref="IEventHandler{TEvent}"/> implementations.
/// Leases batches with an UPDATE ... OUTPUT so several instances can run safely.
/// </summary>
internal sealed class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    OutboxSignal signal,
    TimeProvider time,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private const int MaxAttempts = 10;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Outbox batch failed");
            }

            if (processed < BatchSize)
            {
                await signal.WaitAsync(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    internal async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        List<OutboxMessage> batch;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
            var now = time.GetUtcNow();
            batch = await db.OutboxMessages
                .FromSqlInterpolated($"""
                    UPDATE TOP ({BatchSize}) [OutboxMessages] WITH (ROWLOCK, READPAST)
                    SET [LockedUntil] = {now.Add(Lease)}, [Attempts] = [Attempts] + 1
                    OUTPUT inserted.*
                    WHERE [ProcessedAt] IS NULL AND [DeadAt] IS NULL AND [NextAttemptAt] <= {now}
                      AND ([LockedUntil] IS NULL OR [LockedUntil] < {now})
                    """)
                .AsNoTracking()
                .ToListAsync(ct);
        }

        foreach (var message in batch.OrderBy(m => m.OccurredAt))
        {
            await ProcessMessageAsync(message, ct);
        }

        return batch.Count;
    }

    private async Task ProcessMessageAsync(OutboxMessage message, CancellationToken ct)
    {
        var eventType = Type.GetType(message.EventType);
        string? error = null;

        if (eventType is null)
        {
            error = $"Unknown event type {message.EventType}";
        }
        else
        {
            var domainEvent = (DomainEvent)JsonSerializer.Deserialize(message.PayloadJson, eventType, JsonDefaults.Options)!;
            error = await RunHandlersAsync(message, domainEvent, ct);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
        var tracked = await db.OutboxMessages.FirstAsync(m => m.Id == message.Id, ct);
        var now = time.GetUtcNow();
        tracked.LockedUntil = null;
        if (error is null)
        {
            tracked.ProcessedAt = now;
            tracked.LastError = null;
        }
        else
        {
            tracked.LastError = error.Length > 4000 ? error[..4000] : error;
            if (tracked.Attempts >= MaxAttempts)
            {
                tracked.DeadAt = now;
                logger.LogError("Outbox message {Id} ({Type}) is dead after {Attempts} attempts: {Error}", message.Id, message.EventType, tracked.Attempts, error);
            }
            else
            {
                tracked.NextAttemptAt = now.AddSeconds(Math.Min(3600, Math.Pow(2, tracked.Attempts) * 5));
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<string?> RunHandlersAsync(OutboxMessage message, DomainEvent domainEvent, CancellationToken ct)
    {
        await using var probeScope = scopeFactory.CreateAsyncScope();
        var handlerTypes = EventDispatch
            .ResolveHandlers(probeScope.ServiceProvider, typeof(IEventHandler<>), domainEvent.GetType())
            .Select(h => h.GetType())
            .ToList();

        var completed = await probeScope.ServiceProvider.GetRequiredService<SwitchboardDbContext>()
            .OutboxHandlerReceipts.Where(r => r.MessageId == message.Id)
            .Select(r => r.HandlerName)
            .ToListAsync(ct);

        var errors = new List<string>();
        foreach (var handlerType in handlerTypes)
        {
            var handlerName = handlerType.FullName!;
            if (completed.Contains(handlerName))
            {
                continue;
            }

            // Each handler gets its own scope and DbContext so one failure can't poison another.
            await using var scope = scopeFactory.CreateAsyncScope();
            var actors = scope.ServiceProvider.GetRequiredService<IActorAccessor>();
            using var _ = actors.BeginScope(Actor.System("System", Domain.Channel.System, domainEvent.CascadeDepth + 1));
            try
            {
                var handler = EventDispatch
                    .ResolveHandlers(scope.ServiceProvider, typeof(IEventHandler<>), domainEvent.GetType())
                    .First(h => h.GetType() == handlerType);
                await EventDispatch.InvokeHandlerAsync(handler, typeof(IEventHandler<>), domainEvent, ct);

                var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
                db.OutboxHandlerReceipts.Add(new OutboxHandlerReceipt
                {
                    MessageId = message.Id,
                    HandlerName = handlerName,
                    CompletedAt = time.GetUtcNow(),
                });
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var inner = ex is System.Reflection.TargetInvocationException { InnerException: { } ie } ? ie : ex;
                logger.LogWarning(inner, "Handler {Handler} failed for event {EventId}", handlerName, message.Id);
                errors.Add($"{handlerName}: {inner.Message}");
            }
        }

        return errors.Count == 0 ? null : string.Join("\n", errors);
    }
}
