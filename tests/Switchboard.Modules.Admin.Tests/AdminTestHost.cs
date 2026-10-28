using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Admin;
using Switchboard.Testing;

namespace Switchboard.Modules.Admin.Tests;

/// <summary>Captures system email instead of sending it.</summary>
public sealed class CapturingEmailSender : ISystemEmailSender
{
    public List<OutboundEmail> Sent { get; } = [];

    public Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        lock (Sent)
        {
            Sent.Add(email);
        }
        return Task.FromResult($"<{Guid.NewGuid():N}@test>");
    }
}

/// <summary>Core plus the admin module, with Identity (for invitations) and captured email.</summary>
public abstract class AdminTestBase : IAsyncLifetime
{
    protected SwitchboardTestHost Host { get; private set; } = default!;
    protected CapturingEmailSender Email { get; } = new();

    public virtual async Task InitializeAsync()
    {
        Host = await SwitchboardTestHost.CreateAsync([new AdminModule()], services =>
        {
            services.AddDataProtection();
            services.AddIdentityCore<User>(o => o.User.RequireUniqueEmail = true)
                .AddDefaultTokenProviders()
                .AddEntityFrameworkStores<SwitchboardDbContext>();
            services.AddSingleton<ISystemEmailSender>(Email);
        });
    }

    public async Task DisposeAsync() => await Host.DisposeAsync();

    protected Task<T> As<T>(User user, Func<IServiceProvider, Task<T>> action) => Host.RunAsync(SwitchboardTestHost.As(user), action);

    protected Task As(User user, Func<IServiceProvider, Task> action) => Host.RunAsync(SwitchboardTestHost.As(user), action);

    protected Task AsSystem(Func<IServiceProvider, Task> action) => Host.RunAsync(Actor.System("Test"), action);

    protected Task<T> AsSystem<T>(Func<IServiceProvider, Task<T>> action) => Host.RunAsSystemAsync(action);

    protected Task<User> ReloadAsync(Guid id) =>
        AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().Users.AsNoTracking().FirstAsync(u => u.Id == id));

    protected async Task<User> CreateOrgAdminAsync(string name)
    {
        var user = await Host.CreateAgentAsync(name);
        await AsSystem(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var tracked = await db.Users.FirstAsync(u => u.Id == user.Id);
            tracked.IsOrgAdmin = true;
            await db.SaveChangesAsync();
        });
        return await ReloadAsync(user.Id);
    }

    /// <summary>An agent whose only role is a custom role with exactly these privileges.</summary>
    protected async Task<(User User, Role Role)> CreateAgentWithPrivilegesAsync(string name, params string[] privileges)
    {
        var role = await AsSystem(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var r = new Role { Name = $"{name} role", Privileges = [.. privileges] };
            db.Roles.Add(r);
            await db.SaveChangesAsync();
            return r;
        });
        var user = await Host.CreateAgentAsync(name, roleKey: RoleKeys.ReadOnly);
        await AsSystem(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var m = await db.WorkspaceMemberships.FirstAsync(x => x.UserId == user.Id);
            m.RoleId = role.Id;
            await db.SaveChangesAsync();
        });
        return (user, role);
    }

    protected Task<Ticket> CreateTicketAsync(User requester, User? agent = null, string subject = "Printer jams") =>
        AsSystem(sp => sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand
        {
            Subject = subject,
            DescriptionHtml = $"<p>Hello, this is {requester.DisplayName} ({requester.Email}). It jams on page two.</p>",
            RequesterId = requester.Id,
            AgentId = agent?.Id,
            SourceKey = TicketSourceKeys.Portal,
        }));

    protected Task<Role> RoleAsync(string key) =>
        AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().Roles.AsNoTracking().FirstAsync(r => r.Key == key));
}
