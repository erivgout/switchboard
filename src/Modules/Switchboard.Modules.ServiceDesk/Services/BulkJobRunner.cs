using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceDesk.Data;

namespace Switchboard.Modules.ServiceDesk.Services;

/// <summary>Runs queued bulk jobs (over the inline limit) in the background, as the agent who started them.</summary>
internal sealed class BulkJobRunner(IServiceScopeFactory scopes, TimeProvider time, ILogger<BulkJobRunner> logger) : BackgroundService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                while (await RunNextAsync(stoppingToken))
                {
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Bulk job runner failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Claims and runs one queued job. Returns false when none is waiting.</summary>
    internal async Task<bool> RunNextAsync(CancellationToken ct)
    {
        Guid jobId;
        Actor actor;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
            var now = time.GetUtcNow();

            // A job left running by a stopped process goes back in the queue; finished items are kept.
            var stale = now - StaleAfter;
            await db.Set<SdBulkJob>()
                .Where(j => j.Status == BulkJobStatus.Running && j.StartedAt < stale && j.Total > j.Succeeded + j.Failed)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, BulkJobStatus.Queued), ct);

            var next = await db.Set<SdBulkJob>().AsNoTracking()
                .Where(j => j.Status == BulkJobStatus.Queued)
                .OrderBy(j => j.CreatedAt)
                .Select(j => new { j.Id, j.CreatedById, j.CreatedByName })
                .FirstOrDefaultAsync(ct);
            if (next is null)
            {
                return false;
            }
            var claimed = await db.Set<SdBulkJob>()
                .Where(j => j.Id == next.Id && j.Status == BulkJobStatus.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, BulkJobStatus.Running).SetProperty(j => j.StartedAt, now), ct);
            if (claimed == 0)
            {
                return true;
            }
            jobId = next.Id;
            actor = new Actor(ActorKind.User, next.CreatedById, next.CreatedByName, Channel.Web);
        }

        await BulkActionService.ProcessJobAsync(scopes, jobId, actor, logger, time, ct);
        return true;
    }
}
