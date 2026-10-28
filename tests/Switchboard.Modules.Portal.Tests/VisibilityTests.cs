using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Portal.Services;
using Switchboard.Testing;

namespace Switchboard.Modules.Portal.Tests;

/// <summary>Who sees which requests in the portal: own, requested-for, shared, department head (SR-ISO-1, FR-IAM-5).</summary>
public sealed class VisibilityTests(PortalFixture f) : IClassFixture<PortalFixture>
{
    [Fact]
    public async Task Another_requesters_request_is_not_found_for_reads_and_writes()
    {
        var dana = await f.RequesterAsync("Dana Owner");
        var olive = await f.RequesterAsync("Olive Outsider");
        var ticket = await f.FileAsync(dana);

        await Assert.ThrowsAsync<NotFoundException>(() => f.AsRequester(olive, p => p.GetAsync(ticket.Number)));
        await Assert.ThrowsAsync<NotFoundException>(() => f.AsRequester(olive, p => p.ReplyAsync(ticket.Number, "Me too", null)));
        await Assert.ThrowsAsync<NotFoundException>(() => f.AsRequester(olive, p => p.CloseAsync(ticket.Number)));
        await Assert.ThrowsAsync<NotFoundException>(() => f.AsRequester(olive, p => p.ShareAsync(ticket.Number, olive.Email)));

        var lists = await f.AsRequester(olive, async p => new[]
        {
            await p.ListAsync(RequestTab.Mine, RequestState.All),
            await p.ListAsync(RequestTab.Shared, RequestState.All),
            await p.ListAsync(RequestTab.Department, RequestState.All),
        });
        Assert.All(lists, l => Assert.DoesNotContain(l.Items, r => r.Id == ticket.Id));
        Assert.Empty(await f.AsRequester(olive, p => p.SearchAsync("printer")));
    }

    [Fact]
    public async Task Own_requests_are_listed_under_mine_with_requester_labels()
    {
        var dana = await f.RequesterAsync("Dana Lister");
        var ticket = await f.FileAsync(dana, "Monitor flickers");

        var mine = await f.AsRequester(dana, p => p.ListAsync(RequestTab.Mine, RequestState.Open));
        var row = Assert.Single(mine.Items);
        Assert.Equal(ticket.Id, row.Id);
        Assert.Equal("Being worked on", row.StatusLabel);
        Assert.True(row.IsMine);

        var stored = await f.LoadAsync(ticket.Id);
        Assert.Equal(dana.Id, stored.RequesterId);
        Assert.NotNull(stored.AwaitingAgentSince);

        var view = await f.AsRequester(dana, p => p.GetAsync(ticket.Number));
        Assert.Equal(ViewerRelation.Requester, view.Relation);

        await f.SetStatusAsync(ticket.Id, StatusCategory.Resolved);
        Assert.Empty((await f.AsRequester(dana, p => p.ListAsync(RequestTab.Mine, RequestState.Open))).Items);
        Assert.Single((await f.AsRequester(dana, p => p.ListAsync(RequestTab.Mine, RequestState.Closed))).Items);
    }

    [Fact]
    public async Task Requested_for_person_sees_the_request_as_their_own()
    {
        var manager = await f.RequesterAsync("Mona Manager");
        var hire = await f.RequesterAsync("Hank Hire");
        var ticket = await f.AsAgent(sp => sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand
        {
            Subject = "Laptop for new hire",
            RequesterId = manager.Id,
            RequestedForId = hire.Id,
            Type = TicketType.ServiceRequest,
        }));

        var view = await f.AsRequester(hire, p => p.GetAsync(ticket.Number));
        Assert.Equal(ViewerRelation.RequestedFor, view.Relation);
        Assert.True(view.IsOwn);
        Assert.Contains((await f.AsRequester(hire, p => p.ListAsync(RequestTab.Mine, RequestState.All))).Items, r => r.Id == ticket.Id);
    }

    [Fact]
    public async Task Sharing_grants_and_removing_revokes_visibility()
    {
        var dana = await f.RequesterAsync("Dana Sharer");
        var colin = await f.RequesterAsync("Colin Colleague");
        var ticket = await f.FileAsync(dana);

        await Assert.ThrowsAsync<NotFoundException>(() => f.AsRequester(colin, p => p.GetAsync(ticket.Number)));

        var name = await f.AsRequester(dana, p => p.ShareAsync(ticket.Number, colin.Email));
        Assert.Equal("Colin Colleague", name);

        var view = await f.AsRequester(colin, p => p.GetAsync(ticket.Number));
        Assert.Equal(ViewerRelation.Shared, view.Relation);
        Assert.False(view.CanShare);
        Assert.False(view.CanClose);
        Assert.Empty(view.SharedWith);
        Assert.Empty(view.CcEmails);
        Assert.Contains((await f.AsRequester(colin, p => p.ListAsync(RequestTab.Shared, RequestState.All))).Items, r => r.Id == ticket.Id);
        Assert.DoesNotContain((await f.AsRequester(colin, p => p.ListAsync(RequestTab.Mine, RequestState.All))).Items, r => r.Id == ticket.Id);

        // A colleague it was shared with can reply but can't share further or close.
        await f.AsRequester(colin, p => p.ReplyAsync(ticket.Number, "Same problem on my machine.", null));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.AsRequester(colin, p => p.ShareAsync(ticket.Number, dana.Email)));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.AsRequester(colin, p => p.CloseAsync(ticket.Number)));

        var owner = await f.AsRequester(dana, p => p.GetAsync(ticket.Number));
        Assert.Equal([colin.Id], owner.SharedWith.Select(s => s.Id));
        Assert.Contains(owner.Messages, m => m.AuthorName == "Colin Colleague");

        await f.AsRequester(dana, p => p.UnshareAsync(ticket.Number, colin.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => f.AsRequester(colin, p => p.GetAsync(ticket.Number)));
    }

    [Fact]
    public async Task Sharing_needs_an_active_colleague()
    {
        var dana = await f.RequesterAsync("Dana Typo");
        var ticket = await f.FileAsync(dana);

        var unknown = await Assert.ThrowsAsync<ValidationException>(() => f.AsRequester(dana, p => p.ShareAsync(ticket.Number, "nobody@example.com")));
        Assert.True(unknown.Errors.ContainsKey("share"));
        await Assert.ThrowsAsync<ValidationException>(() => f.AsRequester(dana, p => p.ShareAsync(ticket.Number, dana.Email)));
    }

    [Fact]
    public async Task Department_heads_see_their_departments_requests_and_no_one_elses()
    {
        var head = await f.RequesterAsync("Hattie Head");
        var member = await f.RequesterAsync("Mel Member");
        var peer = await f.RequesterAsync("Pat Peer");
        var outsider = await f.RequesterAsync("Otto Outside");
        await f.Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var finance = new Department { Name = $"Finance {Guid.NewGuid():N}", HeadUserId = head.Id };
            var sales = new Department { Name = $"Sales {Guid.NewGuid():N}" };
            db.Departments.AddRange(finance, sales);
            foreach (var u in new[] { head, member, peer })
            {
                db.Users.Attach(u);
                u.DepartmentId = finance.Id;
            }
            db.Users.Attach(outsider);
            outsider.DepartmentId = sales.Id;
            await db.SaveChangesAsync();
            return true;
        });

        var memberTicket = await f.FileAsync(member, "Expense tool won't load");
        var outsiderTicket = await f.FileAsync(outsider, "CRM is slow");

        var counts = await f.AsRequester(head, p => p.CountsAsync(RequestState.All));
        Assert.True(counts.IsDepartmentLead);
        var department = await f.AsRequester(head, p => p.ListAsync(RequestTab.Department, RequestState.All));
        Assert.Contains(department.Items, r => r.Id == memberTicket.Id && r.RequesterName == "Mel Member");
        Assert.DoesNotContain(department.Items, r => r.Id == outsiderTicket.Id);
        Assert.Equal(ViewerRelation.Department, (await f.AsRequester(head, p => p.GetAsync(memberTicket.Number))).Relation);
        await Assert.ThrowsAsync<NotFoundException>(() => f.AsRequester(head, p => p.GetAsync(outsiderTicket.Number)));

        // Department heads can follow but not close someone else's request.
        await Assert.ThrowsAsync<ForbiddenException>(() => f.AsRequester(head, p => p.CloseAsync(memberTicket.Number)));

        // Being in the department isn't enough.
        Assert.False((await f.AsRequester(peer, p => p.CountsAsync(RequestState.All))).IsDepartmentLead);
        Assert.Empty((await f.AsRequester(peer, p => p.ListAsync(RequestTab.Department, RequestState.All))).Items);
        await Assert.ThrowsAsync<NotFoundException>(() => f.AsRequester(peer, p => p.GetAsync(memberTicket.Number)));
    }

    [Fact]
    public async Task Agents_see_only_their_own_requests_in_the_portal()
    {
        var dana = await f.RequesterAsync("Dana Other");
        var someoneElse = await f.FileAsync(dana);
        var own = await f.AsAgent(sp => sp.GetRequiredService<PortalRequests>().CreateAsync(new NewPortalRequest { Subject = "My keyboard is sticky" }));

        var mine = await f.AsAgent(sp => sp.GetRequiredService<PortalRequests>().ListAsync(RequestTab.Mine, RequestState.All));
        Assert.Contains(mine.Items, r => r.Id == own.Id);
        Assert.DoesNotContain(mine.Items, r => r.Id == someoneElse.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => f.AsAgent(sp => sp.GetRequiredService<PortalRequests>().GetAsync(someoneElse.Number)));
    }

    [Fact]
    public async Task Signed_out_callers_get_nothing()
    {
        var dana = await f.RequesterAsync("Dana Anon");
        var ticket = await f.FileAsync(dana);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            f.Host.RunAsync(Actor.Anonymous, sp => sp.GetRequiredService<PortalRequests>().GetAsync(ticket.Number)));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            f.Host.RunAsync(Actor.Anonymous, sp => sp.GetRequiredService<PortalRequests>().FindPeopleAsync("Dana")));
    }

    [Fact]
    public async Task People_search_returns_only_names_and_emails_of_active_colleagues()
    {
        var dana = await f.RequesterAsync("Dana Finder");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var active = await f.RequesterAsync($"Quinn {tag}");
        var gone = await f.RequesterAsync($"Quincy {tag}");
        await f.Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            db.Users.Attach(gone);
            gone.IsActive = false;
            await db.SaveChangesAsync();
            return true;
        });

        var people = await f.AsRequester(dana, p => p.FindPeopleAsync(tag));
        var person = Assert.Single(people);
        Assert.Equal(active.Id, person.Id);
        Assert.Equal(active.Email, person.Email);
        Assert.Empty(await f.AsRequester(dana, p => p.FindPeopleAsync("Q")));
    }
}
