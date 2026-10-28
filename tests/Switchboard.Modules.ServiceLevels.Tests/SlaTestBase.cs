using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.ServiceLevels.Domain;
using Switchboard.Modules.ServiceLevels.Monitor;
using Switchboard.Testing;

namespace Switchboard.Modules.ServiceLevels.Tests;

/// <summary>A clock the test moves by hand.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// Host with the ServiceLevels module, a seeded Mon-Fri 9-17 UTC calendar and default policy, and a
/// clock starting Wednesday 2026-10-28 10:00 UTC.
/// </summary>
public abstract class SlaTestBase : IAsyncLifetime
{
    public static readonly DateTimeOffset Start = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    protected SwitchboardTestHost Host { get; private set; } = default!;
    protected TestClock Clock { get; } = new(Start);
    protected User Agent { get; private set; } = default!;
    protected User Requester { get; private set; } = default!;

    public virtual async Task InitializeAsync()
    {
        Host = await SwitchboardTestHost.CreateAsync([new ServiceLevelsModule()], services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddScoped<Core.Services.ISystemEmailSender, CapturingEmailSender>();
        });
        Agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Supervisor, TicketScope.Global, "Service Desk");
        Requester = await Host.CreateRequesterAsync("Dana Requester");
    }

    public async Task DisposeAsync() => await Host.DisposeAsync();

    protected Task<T> AsAgent<T>(Func<IServiceProvider, Task<T>> action) => Host.RunAsync(SwitchboardTestHost.As(Agent), action);

    protected Task AsAgent(Func<IServiceProvider, Task> action) => Host.RunAsync(SwitchboardTestHost.As(Agent), action);

    protected Task<Ticket> CreateAsync(Priority priority, string? group = null, TicketType type = TicketType.Incident, Guid? agentId = null) =>
        AsAgent(async sp =>
        {
            Guid? groupId = group is null ? null : await sp.GetRequiredService<SwitchboardDbContext>().AgentGroups.Where(g => g.Name == group).Select(g => (Guid?)g.Id).FirstAsync();
            return await sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand
            {
                Subject = $"{priority} problem",
                RequesterId = Requester.Id,
                Priority = priority,
                Type = type,
                GroupId = groupId,
                AgentId = agentId,
            });
        });

    protected Task UpdateAsync(Guid ticketId, TicketPatch patch) =>
        AsAgent(sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticketId, patch));

    protected Task<Guid> StatusAsync(string name) =>
        Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().TicketStatuses.Where(s => s.Name == name).Select(s => s.Id).FirstAsync());

    protected async Task SetStatusAsync(Guid ticketId, string name) =>
        await UpdateAsync(ticketId, new TicketPatch { StatusId = await StatusAsync(name) });

    protected Task<Ticket> TicketAsync(Guid id) =>
        Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Tickets.AsNoTracking().FirstAsync(t => t.Id == id));

    protected Task<SlaTicketState> StateAsync(Guid id) =>
        Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Set<SlaTicketState>().AsNoTracking().FirstAsync(s => s.TicketId == id));

    protected Task<List<SlaEvent>> EventsAsync(Guid id) =>
        Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Set<SlaEvent>().AsNoTracking()
            .Where(e => e.TicketId == id).OrderBy(e => e.FiredAt).ThenBy(e => e.Kind).ThenBy(e => e.Level).ToListAsync());

    protected Task<int> MonitorAsync() => Host.Services.GetRequiredService<SlaMonitor>().RunOnceAsync();

    protected static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);
}
