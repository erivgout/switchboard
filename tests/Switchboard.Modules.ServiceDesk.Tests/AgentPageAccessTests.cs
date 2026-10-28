using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.ServiceDesk.Services;

namespace Switchboard.Modules.ServiceDesk.Tests;

/// <summary>Requesters never reach agent pages; the ticket page only loads tickets the agent can see.</summary>
public sealed class AgentPageAccessTests : ServiceDeskTestBase
{
    private static IEnumerable<Type> PageModels() =>
        typeof(ServiceDeskModule).Assembly.GetTypes().Where(t => typeof(PageModel).IsAssignableFrom(t) && !t.IsAbstract);

    [Fact]
    public void Every_page_requires_the_agent_policy_or_a_privilege()
    {
        var pages = PageModels().ToList();
        Assert.True(pages.Count >= 7, "Expected the queue, ticket, new ticket, job, canned responses, search and template pages.");
        foreach (var page in pages)
        {
            Assert.Empty(page.GetCustomAttributes<AllowAnonymousAttribute>(true));
            var policies = page.GetCustomAttributes<AuthorizeAttribute>(true).Select(a => a.Policy).ToList();
            Assert.True(
                policies.Any(p => p == SwitchboardPolicies.Agent || p?.StartsWith(SwitchboardPolicies.PrivilegePrefix, StringComparison.Ordinal) == true),
                $"{page.Name} must require the agent policy or a privilege.");
        }
    }

    [Fact]
    public async Task Requesters_fail_the_policies_that_guard_agent_and_admin_pages()
    {
        var agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        var admin = await Host.CreateAgentAsync("Wes Admin", RoleKeys.WorkspaceAdmin, TicketScope.Global, "Service Desk");
        var templates = SwitchboardPolicies.Privilege(Privileges.SettingsManage);

        Assert.False(await AuthorizeAsync(Requester, SwitchboardPolicies.Agent));
        Assert.False(await AuthorizeAsync(Requester, templates));
        Assert.True(await AuthorizeAsync(agent, SwitchboardPolicies.Agent));
        Assert.False(await AuthorizeAsync(agent, templates));
        Assert.True(await AuthorizeAsync(admin, templates));
    }

    private Task<bool> AuthorizeAsync(User user, string policy) =>
        As(user, async sp =>
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "test"));
            return (await sp.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, null, policy)).Succeeded;
        });

    [Fact]
    public async Task The_ticket_page_loads_everything_for_an_agent_and_nothing_for_a_requester()
    {
        var supervisor = await Host.CreateAgentAsync("Priya Supervisor", RoleKeys.Supervisor, TicketScope.Global, "Service Desk");
        var parent = await FileTicketAsync("Office Wi-Fi down", "Service Desk");
        var duplicate = await FileTicketAsync("Wi-Fi not working on 3");
        var related = await FileTicketAsync("Printer offline");
        await As(supervisor, async sp =>
        {
            var tickets = sp.GetRequiredService<ITicketService>();
            await tickets.CreateAsync(new CreateTicketCommand { Subject = "Check access points", RequesterId = Requester.Id, ParentId = parent.Id });
            await tickets.AddNoteAsync(parent.Id, new NoteCommand { BodyHtml = "<p>Rebooting the controller.</p>" });
            await tickets.AddTaskAsync(parent.Id, new TaskCommand { Title = "Call the ISP", AssigneeId = supervisor.Id });
            await tickets.LinkAsync(parent.Id, Ticket.Family, related.Id, TicketLinkTypes.Related);
            await tickets.MergeAsync(parent.Id, [duplicate.Id]);
        });

        var data = await As(supervisor, sp => sp.GetRequiredService<TicketPageLoader>().LoadAsync(parent.Number));
        Assert.Equal(parent.Id, data.Ticket.Id);
        Assert.Single(data.Children);
        Assert.Single(data.MergedTickets);
        Assert.Single(data.Tasks);
        Assert.Contains(data.Links, l => l.Ticket?.Id == related.Id);
        Assert.Contains(data.Thread, e => e.Conversation.BodyText.Contains("Rebooting"));
        Assert.NotEmpty(data.Activity);
        Assert.Contains(data.RequesterRecent, r => r.Id == related.Id);

        Assert.Null(await As(Requester, sp => sp.GetRequiredService<TicketPageLoader>().FindAsync(parent.Number)));
        await Assert.ThrowsAsync<NotFoundException>(() => As(Requester, sp => sp.GetRequiredService<TicketPageLoader>().LoadAsync(parent.Number)));
    }

    [Fact]
    public async Task Merge_and_link_candidates_only_include_visible_open_tickets_in_the_workspace()
    {
        var restricted = await Host.CreateAgentAsync("Rita Restricted", RoleKeys.Agent, TicketScope.Restricted);
        var supervisor = await Host.CreateAgentAsync("Priya Supervisor", RoleKeys.Supervisor, TicketScope.Global, "Service Desk");
        var mine = await FileTicketAsync("VPN drops at home");
        var other = await FileTicketAsync("VPN drops in the office");
        await Host.RunAsSystemAsync(sp => sp.GetRequiredService<ITicketService>().UpdateAsync(mine.Id, new TicketPatch { AgentId = restricted.Id }));

        var asSupervisor = await As(supervisor, sp => sp.GetRequiredService<TicketPageLoader>().FindCandidatesAsync(mine, "VPN"));
        var asRestricted = await As(restricted, sp => sp.GetRequiredService<TicketPageLoader>().FindCandidatesAsync(mine, "VPN"));

        Assert.Contains(asSupervisor, c => c.Id == other.Id);
        Assert.DoesNotContain(asSupervisor, c => c.Id == mine.Id);
        Assert.Empty(asRestricted);
    }
}
