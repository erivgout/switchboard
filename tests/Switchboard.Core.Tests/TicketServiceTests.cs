using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Testing;

namespace Switchboard.Core.Tests;

/// <summary>Integration tests against SQL Server. Each test class gets its own database.</summary>
public sealed class TicketServiceTests : IAsyncLifetime
{
    private SwitchboardTestHost _host = default!;
    private User _agent = default!;
    private User _requester = default!;
    private User _otherRequester = default!;

    public async Task InitializeAsync()
    {
        _host = await SwitchboardTestHost.CreateAsync();
        _agent = await _host.CreateAgentAsync("Sam Agent", RoleKeys.Supervisor, TicketScope.Global, "Service Desk");
        _requester = await _host.CreateRequesterAsync("Dana Requester");
        _otherRequester = await _host.CreateRequesterAsync("Olive Other");
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<Ticket> FileAsRequester(User requester, string subject = "Printer jams") =>
        _host.RunAsync(SwitchboardTestHost.As(requester, Channel.Portal), sp =>
            sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand
            {
                Subject = subject,
                DescriptionHtml = "<p>It jams <script>alert(1)</script>on page two.</p>",
                AsRequester = true,
                SourceKey = TicketSourceKeys.Portal,
            }));

    [Fact]
    public async Task Requester_ticket_lights_the_lamp_and_agent_reply_clears_it()
    {
        var ticket = await FileAsRequester(_requester);
        Assert.NotNull(ticket.AwaitingAgentSince);
        Assert.DoesNotContain("<script", ticket.DescriptionHtml);

        await _host.RunAsync(SwitchboardTestHost.As(_agent), sp =>
            sp.GetRequiredService<ITicketService>().ReplyAsync(ticket.Id, new ReplyCommand { BodyHtml = "<p>On my way.</p>" }));

        var reloaded = await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id));
        Assert.Null(reloaded.AwaitingAgentSince);
        Assert.NotNull(reloaded.FirstRespondedAt);
    }

    [Fact]
    public async Task Display_numbers_are_unique_and_increasing()
    {
        var a = await FileAsRequester(_requester, "First");
        var b = await FileAsRequester(_requester, "Second");
        Assert.True(b.Number > a.Number);
    }

    [Fact]
    public async Task Resolving_sets_resolved_at_and_reopening_clears_it()
    {
        var ticket = await FileAsRequester(_requester);
        var statuses = await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().TicketStatuses.AsNoTracking().ToListAsync());
        var resolved = statuses.First(s => s.Category == StatusCategory.Resolved);

        await _host.RunAsync(SwitchboardTestHost.As(_agent), sp =>
            sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { StatusId = resolved.Id }));
        var afterResolve = await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id));
        Assert.NotNull(afterResolve.ResolvedAt);

        // A requester reply to a resolved ticket reopens it (INV-TKT-4).
        await _host.RunAsync(SwitchboardTestHost.As(_requester, Channel.Portal), sp =>
            sp.GetRequiredService<ITicketService>().AddRequesterMessageAsync(ticket.Id, new RequesterMessageCommand { BodyHtml = "Still broken." }));
        var reopened = await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Tickets.Include(t => t.Status).AsNoTracking().FirstAsync(t => t.Id == ticket.Id));
        Assert.Equal(StatusCategory.Open, reopened.Status!.Category);
        Assert.Null(reopened.ResolvedAt);
        Assert.NotNull(reopened.AwaitingAgentSince);
    }

    [Fact]
    public async Task Requesters_only_see_their_own_tickets()
    {
        var mine = await FileAsRequester(_requester, "Mine");
        await FileAsRequester(_otherRequester, "Not mine");

        var visible = await _host.RunAsync(SwitchboardTestHost.As(_requester, Channel.Portal), async sp =>
            await (await sp.GetRequiredService<IPermissionService>().RequesterVisibleTicketsAsync()).Select(t => t.Id).ToListAsync());
        Assert.Equal([mine.Id], visible);

        await Assert.ThrowsAsync<NotFoundException>(() => _host.RunAsync(SwitchboardTestHost.As(_otherRequester, Channel.Portal), sp =>
            sp.GetRequiredService<ITicketService>().GetForRequesterAsync(mine.Id)));
    }

    [Fact]
    public async Task Restricted_agents_only_see_tickets_assigned_to_them()
    {
        var restricted = await _host.CreateAgentAsync("Rita Restricted", RoleKeys.Agent, TicketScope.Restricted);
        var assigned = await FileAsRequester(_requester, "Assigned to Rita");
        await FileAsRequester(_requester, "Someone else's");
        await _host.RunAsync(SwitchboardTestHost.As(_agent), sp =>
            sp.GetRequiredService<ITicketService>().UpdateAsync(assigned.Id, new TicketPatch { AgentId = restricted.Id }));

        var visible = await _host.RunAsync(SwitchboardTestHost.As(restricted), async sp =>
            await (await sp.GetRequiredService<IPermissionService>().AgentVisibleTicketsAsync()).Select(t => t.Id).ToListAsync());
        Assert.Equal([assigned.Id], visible);
    }

    [Fact]
    public async Task Requesters_cannot_read_private_note_attachments()
    {
        var ticket = await FileAsRequester(_requester);
        var attachmentId = await _host.RunAsync(SwitchboardTestHost.As(_agent), async sp =>
        {
            var files = sp.GetRequiredService<IAttachmentService>();
            var upload = await files.UploadAsync(new MemoryStream("secret"u8.ToArray()), "notes.txt", "text/plain", AttachmentOwners.Pending, null);
            await sp.GetRequiredService<ITicketService>().AddNoteAsync(ticket.Id, new NoteCommand { BodyHtml = "Internal only", IsPrivate = true, AttachmentIds = [upload.Id] });
            return upload.Id;
        });

        var asRequester = await _host.RunAsync(SwitchboardTestHost.As(_requester, Channel.Portal), sp =>
            sp.GetRequiredService<IAttachmentService>().OpenAsync(attachmentId));
        Assert.Null(asRequester);

        var asAgent = await _host.RunAsync(SwitchboardTestHost.As(_agent), sp =>
            sp.GetRequiredService<IAttachmentService>().OpenAsync(attachmentId));
        Assert.NotNull(asAgent);
        await asAgent.Value.Content.DisposeAsync();
    }

    [Fact]
    public async Task Merged_tickets_close_and_become_read_only()
    {
        var primary = await FileAsRequester(_requester, "Primary");
        var secondary = await FileAsRequester(_otherRequester, "Duplicate");

        await _host.RunAsync(SwitchboardTestHost.As(_agent), sp =>
            sp.GetRequiredService<ITicketService>().MergeAsync(primary.Id, [secondary.Id]));

        var merged = await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Tickets.Include(t => t.Status).AsNoTracking().FirstAsync(t => t.Id == secondary.Id));
        Assert.Equal(primary.Id, merged.MergedIntoId);
        Assert.Equal(StatusCategory.Closed, merged.Status!.Category);

        await Assert.ThrowsAsync<ValidationException>(() => _host.RunAsync(SwitchboardTestHost.As(_agent), sp =>
            sp.GetRequiredService<ITicketService>().UpdateAsync(secondary.Id, new TicketPatch { Subject = "Edited" })));
    }

    [Fact]
    public async Task Every_change_writes_one_activity_entry()
    {
        var ticket = await FileAsRequester(_requester);
        await _host.RunAsync(SwitchboardTestHost.As(_agent), sp =>
            sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { Priority = Priority.Urgent, Tags = new[] { "VIP", "vip", "travel" } }));

        var entries = await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().ActivityEntries.AsNoTracking()
            .Where(a => a.SubjectId == ticket.Id && a.EntityType == nameof(Ticket)).OrderBy(a => a.CreatedAt).ToListAsync());
        Assert.Equal(["created", "updated"], entries.Select(e => e.Action));
        Assert.Contains("Priority", entries[1].ChangesJson);

        var tags = await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().TicketTags.Where(t => t.TicketId == ticket.Id).Select(t => t.Tag).OrderBy(t => t).ToListAsync());
        Assert.Equal(["travel", "vip"], tags);
    }

    [Fact]
    public async Task Filter_language_runs_against_sql_server()
    {
        var ticket = await FileAsRequester(_requester, "VPN drops on hotel Wi-Fi");
        await _host.RunAsync(SwitchboardTestHost.As(_agent), sp =>
            sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { Priority = Priority.High }));

        var result = await _host.RunAsync(SwitchboardTestHost.As(_agent), sp =>
            sp.GetRequiredService<ITicketSearch>().SearchAsync(new TicketSearchRequest { Query = "priority>=high is:lit created>-1d state:unresolved \"hotel\"" }));
        var row = Assert.Single(result.Items);
        Assert.Equal(ticket.Id, row.Id);
        Assert.True(row.IsLampLit);
    }

    [Fact]
    public async Task Outbox_delivers_committed_events()
    {
        await FileAsRequester(_requester);
        var delivered = await _host.ProcessOutboxAsync();
        Assert.True(delivered > 0);
        var pending = await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().OutboxMessages.CountAsync(m => m.ProcessedAt == null));
        Assert.Equal(0, pending);
    }
}
