using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Approvals.Services;
using Switchboard.Testing;

namespace Switchboard.Modules.Approvals.Tests;

public sealed class CapturingEmailSender : ISystemEmailSender
{
    public ConcurrentQueue<OutboundEmail> Sent { get; } = new();

    public Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        Sent.Enqueue(email);
        return Task.FromResult($"<{Guid.NewGuid():N}@test>");
    }
}

/// <summary>One database per test class: Core plus approvals, a small org chart and a capturing mail sender.</summary>
public sealed class ApprovalsFixture : IAsyncLifetime
{
    public SwitchboardTestHost Host { get; private set; } = default!;
    public CapturingEmailSender Email { get; } = new();

    public User Agent { get; private set; } = default!;
    public User OtherAgent { get; private set; } = default!;
    public User Requester { get; private set; } = default!;
    public User Manager { get; private set; } = default!;
    public User GrandManager { get; private set; } = default!;
    public User DepartmentHead { get; private set; } = default!;
    public User Outsider { get; private set; } = default!;
    public Guid ServiceDeskId { get; private set; }

    public async Task InitializeAsync()
    {
        Host = await SwitchboardTestHost.CreateAsync([new ApprovalsModule()], s => s.AddSingleton<ISystemEmailSender>(Email));
        Agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        OtherAgent = await Host.CreateAgentAsync("Priya Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        Requester = await Host.CreateRequesterAsync("Dana Requester");
        Manager = await Host.CreateRequesterAsync("Grace Manager");
        GrandManager = await Host.CreateRequesterAsync("Hugo Director");
        DepartmentHead = await Host.CreateRequesterAsync("Fiona Head");
        Outsider = await Host.CreateRequesterAsync("Olive Outsider");

        ServiceDeskId = await Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var finance = new Department { Name = "Finance", HeadUserId = DepartmentHead.Id };
            db.Departments.Add(finance);
            var requester = await db.Users.FirstAsync(u => u.Id == Requester.Id);
            requester.ManagerId = Manager.Id;
            requester.DepartmentId = finance.Id;
            (await db.Users.FirstAsync(u => u.Id == Manager.Id)).ManagerId = GrandManager.Id;
            await db.SaveChangesAsync();
            return await db.AgentGroups.Where(g => g.Name == "Service Desk").Select(g => g.Id).FirstAsync();
        });
    }

    public async Task DisposeAsync() => await Host.DisposeAsync();

    public Task<Ticket> FileTicketAsync(User requester, string subject = "Need a new laptop") =>
        Host.RunAsync(SwitchboardTestHost.As(requester, Channel.Portal), sp =>
            sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand
            {
                Subject = subject,
                Type = TicketType.ServiceRequest,
                AsRequester = true,
                SourceKey = TicketSourceKeys.Portal,
            }));

    public static ApprovalConfig Chain(params (ApprovalRule Rule, ApproverSource[] Sources)[] levels) => new()
    {
        Levels = levels.Select(l => new ApprovalLevelConfig { Rule = l.Rule, Sources = l.Sources.ToList() }).ToList(),
    };

    public static ApproverSource Users(params User[] users) => new() { Kind = ApproverSourceKind.Users, UserIds = users.Select(u => u.Id).ToList() };

    /// <summary>Requests approval on a fresh ticket as the system (like the catalog does).</summary>
    public async Task<AprRequest> RequestAsync(ApprovalConfig config, User? requester = null, IReadOnlyDictionary<string, ApprovalLookupValue>? lookups = null)
    {
        requester ??= Requester;
        var ticket = await FileTicketAsync(requester);
        return await Host.RunAsSystemAsync(sp => sp.GetRequiredService<IApprovalService>().RequestAsync(new RequestApprovalCommand
        {
            SubjectType = ApprovalSubjectTypes.Ticket,
            SubjectId = ticket.Id,
            Title = $"{ticket.DisplayId} {ticket.Subject}",
            RequesterId = requester.Id,
            Config = config,
            Lookups = lookups,
        }));
    }

    public Task<AprDecision> DecideAsync(User user, Guid approvalId, ApprovalVerdict verdict, string? comment = null, Guid? onBehalfOf = null, Channel channel = Channel.Portal) =>
        Host.RunAsync(SwitchboardTestHost.As(user, channel), sp => sp.GetRequiredService<IApprovalService>()
            .DecideAsync(approvalId, new DecideApprovalCommand { Verdict = verdict, Comment = comment, OnBehalfOfId = onBehalfOf }));

    public Task<AprRequest> LoadAsync(Guid id) =>
        Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Set<AprRequest>().AsNoTracking()
            .Include(r => r.Levels).ThenInclude(l => l.Approvers).Include(r => r.Decisions).AsSplitQuery()
            .FirstAsync(r => r.Id == id));

    public Task DelegateAsync(User from, User to, DateTimeOffset? starts = null, DateTimeOffset? ends = null) =>
        Host.RunAsync(SwitchboardTestHost.As(from), sp => sp.GetRequiredService<IApprovalDelegationService>()
            .CreateAsync(to.Id, starts ?? DateTimeOffset.UtcNow.AddMinutes(-1), ends ?? DateTimeOffset.UtcNow.AddDays(3)));
}
