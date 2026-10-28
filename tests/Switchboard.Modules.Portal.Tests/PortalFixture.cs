using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Portal.Services;
using Switchboard.Testing;

namespace Switchboard.Modules.Portal.Tests;

/// <summary>One throwaway database per test class. Tests create their own people so they don't interfere.</summary>
public sealed class PortalFixture : IAsyncLifetime
{
    public SwitchboardTestHost Host { get; private set; } = default!;
    public User Agent { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        Host = await SwitchboardTestHost.CreateAsync(new PortalModule());
        Agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Supervisor, TicketScope.Global, "Service Desk");
    }

    public async Task DisposeAsync() => await Host.DisposeAsync();

    public Task<User> RequesterAsync(string name) => Host.CreateRequesterAsync(name);

    public Task<T> AsRequester<T>(User user, Func<PortalRequests, Task<T>> action) =>
        Host.RunAsync(SwitchboardTestHost.As(user, Channel.Portal), sp => action(sp.GetRequiredService<PortalRequests>()));

    public Task AsRequester(User user, Func<PortalRequests, Task> action) =>
        Host.RunAsync(SwitchboardTestHost.As(user, Channel.Portal), sp => action(sp.GetRequiredService<PortalRequests>()));

    public Task<T> AsAgent<T>(Func<IServiceProvider, Task<T>> action) => Host.RunAsync(SwitchboardTestHost.As(Agent), action);

    public Task AsAgent(Func<IServiceProvider, Task> action) => Host.RunAsync(SwitchboardTestHost.As(Agent), action);

    public Task<Ticket> FileAsync(User requester, string subject = "Printer jams on page two", string? description = "It jams every time.") =>
        AsRequester(requester, p => p.CreateAsync(new NewPortalRequest { Subject = subject, Description = description }));

    public Task<Ticket> LoadAsync(Guid id) =>
        Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Tickets.Include(t => t.Status).AsNoTracking().FirstAsync(t => t.Id == id));

    public Task SetAsync<T>(string key, T value) =>
        Host.RunAsSystemAsync(async sp =>
        {
            await sp.GetRequiredService<ISettingsService>().SetAsync(key, value, Host.Workspace.Id);
            await sp.GetRequiredService<SwitchboardDbContext>().SaveChangesAsync();
            return true;
        });

    public Task SetStatusAsync(Guid ticketId, StatusCategory category) =>
        AsAgent(async sp =>
        {
            var status = await sp.GetRequiredService<SwitchboardDbContext>().TicketStatuses.AsNoTracking()
                .Where(s => s.WorkspaceId == Host.Workspace.Id && s.Category == category).OrderBy(s => s.Order).FirstAsync();
            await sp.GetRequiredService<ITicketService>().UpdateAsync(ticketId, new TicketPatch { StatusId = status.Id });
        });

    public Task<Guid> UploadAsync(User uploader, string name, string content) =>
        Host.RunAsync(SwitchboardTestHost.As(uploader), async sp =>
            (await sp.GetRequiredService<IAttachmentService>().UploadAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)), name, "text/plain", AttachmentOwners.Pending, null)).Id);
}
