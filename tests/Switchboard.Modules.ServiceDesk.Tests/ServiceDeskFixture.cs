using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Testing;

namespace Switchboard.Modules.ServiceDesk.Tests;

/// <summary>Shared helpers: a test host with the service desk module and a few people.</summary>
public abstract class ServiceDeskTestBase : IAsyncLifetime
{
    protected SwitchboardTestHost Host { get; private set; } = default!;
    protected User Requester { get; private set; } = default!;

    public virtual async Task InitializeAsync()
    {
        Host = await SwitchboardTestHost.CreateAsync(new ServiceDeskModule());
        Requester = await Host.CreateRequesterAsync("Dana Requester");
    }

    public async Task DisposeAsync() => await Host.DisposeAsync();

    protected Task<T> As<T>(User user, Func<IServiceProvider, Task<T>> action) => Host.RunAsync(SwitchboardTestHost.As(user), action);

    protected Task As(User user, Func<IServiceProvider, Task> action) => Host.RunAsync(SwitchboardTestHost.As(user), action);

    protected Task<Ticket> FileTicketAsync(string subject = "Printer jams", string? group = null) =>
        Host.RunAsSystemAsync(async sp =>
        {
            Guid? groupId = group is null ? null : await sp.GetRequiredService<SwitchboardDbContext>().AgentGroups.Where(g => g.Name == group).Select(g => (Guid?)g.Id).FirstAsync();
            return await sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand
            {
                Subject = subject,
                DescriptionHtml = "<p>It happened again.</p>",
                RequesterId = Requester.Id,
                SourceKey = TicketSourceKeys.Portal,
                GroupId = groupId,
            });
        });

    /// <summary>Creates a role with exactly these privileges and an agent who holds it.</summary>
    protected async Task<User> AgentWithPrivilegesAsync(string name, params string[] privileges)
    {
        var key = "test_" + Guid.NewGuid().ToString("N")[..8];
        await Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            db.Roles.Add(new Role { Key = key, Name = key, Privileges = [Privileges.TicketsView, .. privileges] });
            await db.SaveChangesAsync();
            return true;
        });
        return await Host.CreateAgentAsync(name, key, TicketScope.Global, "Service Desk");
    }

    protected Task<Ticket> ReloadAsync(Guid id) =>
        Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Tickets.AsNoTracking()
            .Include(t => t.Status).Include(t => t.Tags).FirstAsync(t => t.Id == id));
}
