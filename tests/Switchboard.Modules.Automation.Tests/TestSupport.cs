using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Net;
using Switchboard.Modules.Automation.Workflows;
using Switchboard.Testing;

namespace Switchboard.Modules.Automation.Tests;

public sealed class FakeEmailSender : ISystemEmailSender
{
    public ConcurrentQueue<OutboundEmail> Sent { get; } = new();

    public Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        Sent.Enqueue(email);
        return Task.FromResult($"<{Guid.NewGuid():N}@test>");
    }
}

/// <summary>Answers every request with a queued response (default 200 OK) and records it.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    public ConcurrentQueue<(HttpMethod Method, Uri Uri, string? Body, string? Auth)> Requests { get; } = new();
    public Queue<Func<HttpRequestMessage, HttpResponseMessage>> Responses { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        request.Headers.TryGetValues("X-Api-Key", out var keys);
        Requests.Enqueue((request.Method, request.RequestUri!, body, keys?.FirstOrDefault()));
        return Responses.Count > 0 ? Responses.Dequeue()(request) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
    }
}

/// <summary>Maps host names to addresses; each lookup can return a different answer (DNS rebinding).</summary>
public sealed class FakeResolver : IHostResolver
{
    public Dictionary<string, Queue<IPAddress[]>> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Calls;

    public void Set(string host, params string[] answersInOrder) =>
        Answers[host] = new Queue<IPAddress[]>(answersInOrder.Select(a => new[] { IPAddress.Parse(a) }));

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (!Answers.TryGetValue(host, out var queue) || queue.Count == 0)
        {
            return Task.FromResult(new[] { IPAddress.Parse("93.184.215.14") });
        }
        return Task.FromResult(queue.Count > 1 ? queue.Dequeue() : queue.Peek());
    }
}

/// <summary>Counts SQL commands that write, so tests can prove a code path never writes.</summary>
public sealed class WriteCounter : DbCommandInterceptor
{
    public volatile bool Recording;
    public ConcurrentQueue<string> Writes { get; } = new();

    private void Inspect(DbCommand command)
    {
        if (!Recording)
        {
            return;
        }
        var sql = command.CommandText.TrimStart();
        if (sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("MERGE", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("\nINSERT ", StringComparison.OrdinalIgnoreCase) || sql.Contains("\nUPDATE ", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("\nDELETE ", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("SET NOCOUNT ON", StringComparison.OrdinalIgnoreCase))
        {
            Writes.Enqueue(sql);
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Inspect(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Inspect(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Inspect(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Inspect(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Inspect(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Inspect(command);
        return ValueTask.FromResult(result);
    }
}

/// <summary>Core plus the automation module on a throwaway database, with fake email, HTTP and DNS.</summary>
public abstract class AutomationTestBase : IAsyncLifetime
{
    protected SwitchboardTestHost Host = default!;
    protected FakeEmailSender Email = new();
    protected FakeHttpHandler Http = new();
    protected FakeResolver Resolver = new();
    protected WriteCounter Writes = new();
    protected User Admin = default!;
    protected User Agent = default!;
    protected User Requester = default!;

    public virtual async Task InitializeAsync()
    {
        Host = await SwitchboardTestHost.CreateAsync([new AutomationModule()], services =>
        {
            services.AddScoped<ISystemEmailSender>(_ => Email);
            services.AddSingleton<IHostResolver>(Resolver);
            services.AddSingleton(sp => new SafeHttpClient(Resolver, sp.GetRequiredService<OutboundNetworkPolicy>(), sp.GetRequiredService<IOptions<AutomationOptions>>(), Http));
            services.ConfigureDbContext<SwitchboardDbContext>(o => o.AddInterceptors(Writes));
        });
        Admin = await Host.CreateAgentAsync("Ada Admin", RoleKeys.WorkspaceAdmin);
        Agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        Requester = await Host.CreateRequesterAsync("Dana Requester");
    }

    public async Task DisposeAsync() => await Host.DisposeAsync();

    protected Task<T> Db<T>(Func<SwitchboardDbContext, Task<T>> query) =>
        Host.RunAsSystemAsync(sp => query(sp.GetRequiredService<SwitchboardDbContext>()));

    protected Task Db(Func<SwitchboardDbContext, Task> work) =>
        Host.RunAsSystemAsync(async sp =>
        {
            await work(sp.GetRequiredService<SwitchboardDbContext>());
            return true;
        });

    protected async Task<AutWorkflow> PublishAsync(string name, WorkflowDefinition def)
    {
        return await Host.RunAsync(SwitchboardTestHost.As(Admin), async sp =>
        {
            var service = sp.GetRequiredService<WorkflowService>();
            var wf = await service.SaveDraftAsync(null, Host.Workspace.Id, name, null, def);
            await service.PublishAsync(wf.Id);
            return await service.GetAsync(wf.Id);
        });
    }

    protected Task<Ticket> FileAsync(string subject = "Printer jams", string? group = null, User? requester = null, string[]? tags = null) =>
        Host.RunAsync(SwitchboardTestHost.As(Agent), async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            Guid? groupId = group is null ? null : await db.AgentGroups.Where(g => g.Name == group).Select(g => g.Id).FirstAsync();
            return await sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand
            {
                Subject = subject,
                DescriptionHtml = "<p>Help</p>",
                RequesterId = (requester ?? Requester).Id,
                GroupId = groupId,
                Tags = tags,
            });
        });

    protected Task<Ticket> TicketAsync(Guid id) =>
        Db(db => db.Tickets.AsNoTracking().Include(t => t.Tags).Include(t => t.Status).FirstAsync(t => t.Id == id));

    protected Task<List<AutRun>> RunsAsync(Guid? workflowId = null) =>
        Db(db => db.Set<AutRun>().AsNoTracking().Include(r => r.Steps)
            .Where(r => workflowId == null || r.WorkflowId == workflowId).OrderBy(r => r.StartedAt).ToListAsync());

    protected Task<Guid> StatusIdAsync(string name) =>
        Db(db => db.TicketStatuses.Where(s => s.Name == name).Select(s => s.Id).FirstAsync());

    protected Task<Guid> GroupIdAsync(string name) =>
        Db(db => db.AgentGroups.Where(g => g.Name == name).Select(g => g.Id).FirstAsync());

    protected Task SetAvailabilityAsync(User user, AgentAvailability availability) =>
        Db(db => db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Availability, availability)));

    protected Task ProcessAsync() => Host.ProcessOutboxAsync(maxBatches: 50);

    protected static ActionDefinition Act(string type, params (string Key, string Value)[] ps) =>
        new() { Type = type, Params = ps.ToDictionary(p => p.Key, p => (string?)p.Value) };

    protected static ConditionSet When(params Condition[] rules) =>
        new() { Groups = [new ConditionGroup { Rules = [.. rules] }] };

    protected static Condition Rule(string field, string op, string? value = null) => new() { Field = field, Op = op, Value = value };
}
