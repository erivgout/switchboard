using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Approvals.Domain;

namespace Switchboard.Modules.Approvals.Services;

/// <summary>Automatic reminders (FR-APR-4): nudges approvers who haven't decided after the configured number of hours.</summary>
internal sealed class ApprovalReminderJob(IServiceScopeFactory scopes, TimeProvider time, ILogger<ApprovalReminderJob> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Approval reminders failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        using var _ = sp.GetRequiredService<IActorAccessor>().BeginScope(Actor.System("Approval reminders"));
        var hours = await sp.GetRequiredService<ISettingsService>().GetAsync(ApprovalSettingKeys.ReminderHours, ApprovalSettingKeys.ReminderHoursDefault, ct: ct);
        if (hours <= 0)
        {
            return 0;
        }
        var cutoff = time.GetUtcNow().AddHours(-hours);
        var due = await sp.GetRequiredService<SwitchboardDbContext>().Set<AprRequest>().AsNoTracking()
            .Where(r => r.Status == ApprovalStatus.Requested && (r.LastRemindedAt ?? r.WaitingSince ?? r.CreatedAt) < cutoff)
            .OrderBy(r => r.LastRemindedAt ?? r.WaitingSince)
            .Select(r => r.Id)
            .Take(100)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var id in due)
        {
            // A fresh scope per request so one failure can't poison the rest.
            await using var inner = scopes.CreateAsyncScope();
            using var __ = inner.ServiceProvider.GetRequiredService<IActorAccessor>().BeginScope(Actor.System("Approval reminders"));
            try
            {
                sent += await inner.ServiceProvider.GetRequiredService<IApprovalService>().RemindAsync(id, ct) > 0 ? 1 : 0;
            }
            catch (Exception ex) when (ex is ValidationException or ConflictException or DbUpdateException)
            {
                logger.LogInformation(ex, "Skipped reminder for approval {Id}", id);
            }
        }
        return sent;
    }
}
