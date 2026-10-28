using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;

namespace Switchboard.Core.Data;

/// <summary>
/// Development demo data: a small company with agents, requesters and a realistic queue.
/// Runs only when <see cref="SwitchboardOptions.DemoData"/> is on and no demo admin exists yet.
/// </summary>
public static class DemoSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<SwitchboardDbContext>();
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == DemoAccounts.Admin.ToUpperInvariant(), ct))
        {
            return;
        }

        var hasher = services.GetRequiredService<IPasswordHasher<User>>();
        var time = services.GetRequiredService<TimeProvider>();
        var actors = services.GetRequiredService<IActorAccessor>();
        var workspace = await db.Workspaces.FirstAsync(w => w.IsPrimary, ct);
        var roles = await db.Roles.Where(r => r.Key != null).ToDictionaryAsync(r => r.Key!, ct);
        var groups = await db.AgentGroups.Where(g => g.WorkspaceId == workspace.Id).ToDictionaryAsync(g => g.Name, ct);
        const string pw = DemoAccounts.Password;

        var departments = new[] { "Finance", "Sales", "Engineering", "People", "Operations" }
            .Select(n => new Department { Name = n })
            .ToDictionary(d => d.Name);
        db.Departments.AddRange(departments.Values);
        var hq = new Location { Name = "Head office", City = "Chicago", Country = "United States" };
        var remote = new Location { Name = "Remote" };
        db.Locations.AddRange(hq, remote);

        var admin = await CoreSeeder.CreateUserAsync(db, hasher, DemoAccounts.Admin, "Avery Admin", pw, true, true, time, ct);
        var sam = await CoreSeeder.CreateUserAsync(db, hasher, DemoAccounts.Agent, "Sam Rivera", pw, true, false, time, ct);
        var priya = await CoreSeeder.CreateUserAsync(db, hasher, DemoAccounts.Supervisor, "Priya Shah", pw, true, false, time, ct);
        var jordan = await CoreSeeder.CreateUserAsync(db, hasher, DemoAccounts.GroupAgent, "Jordan Lee", pw, true, false, time, ct);
        sam.JobTitle = "Service desk analyst";
        priya.JobTitle = "Service desk lead";
        jordan.JobTitle = "Field technician";
        sam.Skills = ["windows", "m365"];
        priya.Skills = ["network", "vpn"];

        var dana = await CoreSeeder.CreateUserAsync(db, hasher, DemoAccounts.Requester, "Dana Whitfield", pw, false, false, time, ct);
        var marcus = await CoreSeeder.CreateUserAsync(db, hasher, "marcus@switchboard.local", "Marcus Chen", pw, false, false, time, ct);
        var elena = await CoreSeeder.CreateUserAsync(db, hasher, "elena@switchboard.local", "Elena Rossi", pw, false, false, time, ct);
        var tom = await CoreSeeder.CreateUserAsync(db, hasher, "tom@switchboard.local", "Tom Okafor", pw, false, false, time, ct);
        var grace = await CoreSeeder.CreateUserAsync(db, hasher, "grace@switchboard.local", "Grace Kim", pw, false, false, time, ct);
        dana.DepartmentId = departments["Finance"].Id;
        dana.JobTitle = "Senior accountant";
        dana.LocationId = hq.Id;
        marcus.DepartmentId = departments["Sales"].Id;
        marcus.JobTitle = "Account executive";
        marcus.IsVip = true;
        marcus.LocationId = remote.Id;
        elena.DepartmentId = departments["Engineering"].Id;
        elena.JobTitle = "Staff engineer";
        tom.DepartmentId = departments["People"].Id;
        tom.JobTitle = "HR partner";
        grace.DepartmentId = departments["Operations"].Id;
        grace.JobTitle = "Office manager";
        departments["Finance"].HeadUserId = dana.Id;
        dana.ManagerId = grace.Id;

        db.WorkspaceMemberships.AddRange(
            new WorkspaceMembership { UserId = sam.Id, WorkspaceId = workspace.Id, RoleId = roles[RoleKeys.Agent].Id, TicketScope = TicketScope.Global },
            new WorkspaceMembership { UserId = priya.Id, WorkspaceId = workspace.Id, RoleId = roles[RoleKeys.Supervisor].Id, TicketScope = TicketScope.Global },
            new WorkspaceMembership { UserId = jordan.Id, WorkspaceId = workspace.Id, RoleId = roles[RoleKeys.Agent].Id, TicketScope = TicketScope.Group });
        db.AgentGroupMembers.AddRange(
            new AgentGroupMember { GroupId = groups["Service Desk"].Id, UserId = sam.Id },
            new AgentGroupMember { GroupId = groups["Service Desk"].Id, UserId = priya.Id },
            new AgentGroupMember { GroupId = groups["Network"].Id, UserId = priya.Id },
            new AgentGroupMember { GroupId = groups["Hardware"].Id, UserId = jordan.Id },
            new AgentGroupMember { GroupId = groups["Network"].Id, UserId = sam.Id, IsObserver = true });
        await db.SaveChangesAsync(ct);

        var tickets = services.GetRequiredService<ITicketService>();
        var statuses = await db.TicketStatuses.Where(s => s.WorkspaceId == workspace.Id).ToDictionaryAsync(s => s.Name, ct);
        var categories = await db.TicketCategories.Where(c => c.WorkspaceId == workspace.Id).ToListAsync(ct);
        Guid Cat(string name) => categories.First(c => c.Name == name).Id;
        Guid Sub(string parent, string name) => categories.First(c => c.Name == name && c.ParentId == Cat(parent)).Id;

        async Task<Ticket> Request(User requester, string subject, string body, string source, Priority priority, TicketType type = TicketType.Incident,
            string? category = null, string? sub = null, string? group = null, User? agent = null, string[]? tags = null)
        {
            using var _ = actors.BeginScope(Actor.System("Demo data", Channel.Portal));
            return await tickets.CreateAsync(new CreateTicketCommand
            {
                Type = type,
                Subject = subject,
                DescriptionHtml = $"<p>{System.Net.WebUtility.HtmlEncode(body)}</p>",
                RequesterId = requester.Id,
                SourceKey = source,
                Priority = priority,
                CategoryId = category is null ? null : Cat(category),
                SubCategoryId = sub is null ? null : Sub(category!, sub),
                GroupId = group is null ? null : groups[group].Id,
                AgentId = agent?.Id,
                Tags = tags,
            }, ct);
        }

        async Task Reply(Ticket t, User agent, string text, string? status = null)
        {
            using var _ = actors.BeginScope(Actor.ForUser(agent, Channel.Web));
            await tickets.ReplyAsync(t.Id, new ReplyCommand
            {
                BodyHtml = $"<p>{System.Net.WebUtility.HtmlEncode(text)}</p>",
                StatusId = status is null ? null : statuses[status].Id,
            }, ct);
        }

        async Task Note(Ticket t, User agent, string text)
        {
            using var _ = actors.BeginScope(Actor.ForUser(agent, Channel.Web));
            await tickets.AddNoteAsync(t.Id, new NoteCommand { BodyHtml = $"<p>{System.Net.WebUtility.HtmlEncode(text)}</p>" }, ct);
        }

        async Task RequesterSays(Ticket t, User requester, string text)
        {
            using var _ = actors.BeginScope(Actor.ForUser(requester, Channel.Portal));
            await tickets.AddRequesterMessageAsync(t.Id, new RequesterMessageCommand { BodyHtml = $"<p>{System.Net.WebUtility.HtmlEncode(text)}</p>" }, ct);
        }

        async Task SetStatus(Ticket t, User agent, string status)
        {
            using var _ = actors.BeginScope(Actor.ForUser(agent, Channel.Web));
            await tickets.UpdateAsync(t.Id, new TicketPatch { StatusId = statuses[status].Id }, ct: ct);
        }

        var vpn = await Request(marcus, "VPN drops every 20 minutes on hotel Wi-Fi",
            "I'm travelling for the client summit and the VPN disconnects roughly every 20 minutes. Reconnecting works but I lose my CRM session each time.",
            TicketSourceKeys.Email, Priority.High, category: "Network", sub: "VPN", group: "Network", agent: priya, tags: ["vip", "travel"]);
        await Note(vpn, priya, "Likely the hotel's captive portal re-authenticating. Checking whether the always-on profile with TCP fallback fixes it.");
        await Reply(vpn, priya, "Thanks Marcus. I've pushed a VPN profile that falls back to TCP 443. Could you restart the VPN client and tell me if the drops continue?", "Pending");
        await RequesterSays(vpn, marcus, "Restarted it an hour ago and no drops so far. I'll keep an eye on it this afternoon.");

        var laptop = await Request(dana, "Laptop battery swelling, trackpad is raised",
            "The trackpad on my laptop is pushed up and the case doesn't close flat. I think the battery is swelling. Is it safe to keep using?",
            TicketSourceKeys.Portal, Priority.Urgent, category: "Hardware", sub: "Laptop", group: "Hardware", tags: ["safety"]);
        await Note(laptop, jordan, "Swollen lithium battery: stop use immediately. Loaner from stock, then RMA.");

        var mfa = await Request(tom, "Locked out after replacing my phone",
            "I got a new phone over the weekend and the authenticator app didn't transfer. Now I can't sign in to anything.",
            TicketSourceKeys.Phone, Priority.High, category: "Access", sub: "MFA", group: "Service Desk", agent: sam);
        await Reply(mfa, sam, "Hi Tom, I've reset your MFA registration. Sign in from your new phone and you'll be asked to set up the authenticator again.");
        await SetStatus(mfa, sam, "Resolved");

        var printer = await Request(grace, "Third floor printer jams on double-sided jobs",
            "Every duplex job on the 3rd floor printer jams on the second sheet. Single-sided works fine.",
            TicketSourceKeys.Email, Priority.Medium, category: "Hardware", sub: "Printer", group: "Hardware", agent: jordan);
        await Reply(printer, jordan, "I'll be on 3 this afternoon with a duplex roller kit.");

        await Request(elena, "Need access to the production logging dashboard",
            "I'm on call next week and need read access to the production logging dashboard.",
            TicketSourceKeys.Portal, Priority.Medium, TicketType.ServiceRequest, category: "Access", sub: "Permissions", group: "Service Desk");

        var outlook = await Request(dana, "Outlook keeps asking for my password",
            "Since this morning Outlook prompts for my password every few minutes, even after I enter it.",
            TicketSourceKeys.Email, Priority.Medium, category: "Email and collaboration", sub: "Email", group: "Service Desk", agent: sam);
        await Reply(outlook, sam, "Could you try signing out of Office from File, Account, then signing back in? That clears a stale token in most cases.", "Pending");

        await Request(marcus, "New hire starting Monday needs a laptop and accounts",
            "Jamie Ortiz joins the sales team on Monday. They need a laptop, email, CRM and the sales shared drive.",
            TicketSourceKeys.Portal, Priority.High, TicketType.ServiceRequest, category: "Hardware", sub: "Laptop", group: "Service Desk", tags: ["onboarding"]);

        var wifi = await Request(grace, "Guest Wi-Fi password for the board meeting",
            "We have six external board members visiting Thursday. Can we get a guest Wi-Fi code that lasts all day?",
            TicketSourceKeys.Chat, Priority.Low, TicketType.ServiceRequest, category: "Network", sub: "Wi-Fi", group: "Network", agent: priya);
        await Reply(wifi, priya, "Done: the Thursday guest code is in the shared reception mailbox and is valid from 7am to 9pm.", "Resolved");
        await SetStatus(wifi, priya, "Closed");

        await Request(elena, "Docker Desktop license expired",
            "Docker Desktop tells me my license expired and blocks starting containers.",
            TicketSourceKeys.Portal, Priority.Medium, category: "Software", sub: "License");

        var drive = await Request(tom, "Shared drive folder disappeared",
            "The People team's 'Policies 2026' folder is gone from the shared drive. Did someone move it?",
            TicketSourceKeys.Email, Priority.High, category: "Email and collaboration", sub: "Shared drives", group: "Service Desk", agent: sam);
        await Note(drive, sam, "Audit log shows it was moved into Archive by a sync client at 02:14. Restoring.");
        await Reply(drive, sam, "Found it. A sync client moved it into Archive overnight. I've restored it to its original place.");
        await RequesterSays(drive, tom, "It's back, but two documents edited yesterday are missing their latest changes.");

        await Request(dana, "Excel crashes when opening the quarterly model",
            "The Q3 forecast workbook crashes Excel on open. Other workbooks open fine.",
            TicketSourceKeys.Portal, Priority.Medium, category: "Software", sub: "Bug", group: "Service Desk");

        await Request(marcus, "Monitor flickers when connected through the dock",
            "My second monitor flickers through the docking station but is fine with a direct HDMI cable.",
            TicketSourceKeys.WalkUp, Priority.Low, category: "Hardware", sub: "Monitor", group: "Hardware", agent: jordan);

        // A ticket that is overdue, to show SLA states before the SLA module computes real targets.
        db.ChangeTracker.Clear();
        var overdue = await db.Tickets.FirstAsync(t => t.Id == laptop.Id, ct);
        overdue.DueBy = time.GetUtcNow().AddHours(-2);
        overdue.FirstResponseDueBy = time.GetUtcNow().AddHours(-6);
        var soon = await db.Tickets.FirstAsync(t => t.Id == vpn.Id, ct);
        soon.DueBy = time.GetUtcNow().AddHours(3);
        await db.SaveChangesAsync(ct);
        _ = admin;
    }
}
