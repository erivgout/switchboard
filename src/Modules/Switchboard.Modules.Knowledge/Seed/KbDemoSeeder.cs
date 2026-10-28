using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;

namespace Switchboard.Modules.Knowledge.Seed;

/// <summary>
/// Development demo content. Runs once after startup because the demo people are created after
/// module seeding (see the requested shared change in the module report).
/// </summary>
internal sealed class KbDemoSeeder(
    IServiceScopeFactory scopes,
    IOptions<SwitchboardOptions> options,
    IHostEnvironment environment,
    ILogger<KbDemoSeeder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.DemoData || !environment.IsDevelopment())
        {
            return;
        }
        try
        {
            await SeedAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Knowledge demo data could not be created");
        }
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SwitchboardDbContext>();
        if (await db.Set<KbCategory>().AnyAsync(ct))
        {
            return;
        }
        var users = await db.Users.Where(u => u.Email != null && u.Email.EndsWith("@switchboard.local")).ToDictionaryAsync(u => u.Email!, ct);
        if (!users.TryGetValue(DemoAccounts.Agent, out var sam) || !users.TryGetValue(DemoAccounts.Supervisor, out var priya)
            || !users.TryGetValue(DemoAccounts.Admin, out var admin))
        {
            return;
        }
        var workspace = await db.Workspaces.FirstAsync(w => w.IsPrimary, ct);
        var actors = sp.GetRequiredService<IActorAccessor>();
        var finance = await db.Departments.FirstOrDefaultAsync(d => d.Name == "Finance", ct);
        var serviceDesk = await db.AgentGroups.FirstOrDefaultAsync(g => g.Name == "Service Desk" && g.WorkspaceId == workspace.Id, ct);

        Guid itHelp, policies, internalCat;
        Guid connected, accounts, devices, email, finSystems, runbooks, security;
        using (actors.BeginScope(Actor.ForUser(admin, Channel.Web)))
        {
            var structure = sp.GetRequiredService<IKbStructureService>();
            itHelp = (await structure.CreateCategoryAsync(workspace.Id, "IT help", "Fixes and how-tos for the tools you use every day.", ct)).Id;
            policies = (await structure.CreateCategoryAsync(workspace.Id, "Policies", "How we keep people, devices and data safe.", ct)).Id;
            internalCat = (await structure.CreateCategoryAsync(workspace.Id, "Service desk", "Runbooks and notes for the team.", ct)).Id;

            connected = (await structure.CreateFolderAsync(new KbFolderInput { CategoryId = itHelp, Name = "Getting connected", Description = "VPN, Wi-Fi and working from anywhere." }, ct)).Id;
            accounts = (await structure.CreateFolderAsync(new KbFolderInput { CategoryId = itHelp, Name = "Accounts and sign-in", Description = "Passwords, two-step verification and access." }, ct)).Id;
            devices = (await structure.CreateFolderAsync(new KbFolderInput { CategoryId = itHelp, Name = "Printers and devices", Description = "Printers, laptops, monitors and docks." }, ct)).Id;
            email = (await structure.CreateFolderAsync(new KbFolderInput { CategoryId = itHelp, Name = "Email and calendar", Description = "Outlook, shared mailboxes and meetings." }, ct)).Id;
            finSystems = (await structure.CreateFolderAsync(new KbFolderInput
            {
                CategoryId = itHelp,
                Name = "Finance systems",
                Description = "Month-end tools for the Finance team.",
                Visibility = finance is null ? KbFolderVisibility.SignedIn : KbFolderVisibility.Restricted,
                DepartmentIds = finance is null ? [] : [finance.Id],
            }, ct)).Id;
            security = (await structure.CreateFolderAsync(new KbFolderInput
            {
                CategoryId = policies,
                Name = "Security",
                Description = "Policies reviewed by the service desk lead before publishing.",
                Visibility = KbFolderVisibility.SignedIn,
                RequiresApproval = true,
                ApproverIds = [priya.Id],
            }, ct)).Id;
            runbooks = (await structure.CreateFolderAsync(new KbFolderInput
            {
                CategoryId = internalCat,
                Name = "Runbooks",
                Description = "Step-by-step procedures for agents.",
                Visibility = KbFolderVisibility.AgentsOnly,
                AgentGroupIds = serviceDesk is null ? [] : [serviceDesk.Id],
            }, ct)).Id;
        }

        var created = new Dictionary<string, Guid>();
        async Task<Guid> Write(User author, User? publisher, Guid folder, string key, string title, string body, string[] tags,
            KbArticleType type = KbArticleType.Permanent, int views = 0, DateOnly? reviewBy = null, bool submit = false)
        {
            Guid id;
            using (actors.BeginScope(Actor.ForUser(author, Channel.Web)))
            {
                var knowledge = scope.ServiceProvider.GetRequiredService<IKnowledgeService>();
                id = (await knowledge.CreateDraftAsync(new KbDraftInput
                {
                    FolderId = folder,
                    Title = title,
                    BodyHtml = body,
                    Tags = tags,
                    Type = type,
                    ReviewBy = reviewBy,
                    OwnerId = author.Id,
                }, ct)).ArticleId;
                if (submit)
                {
                    await knowledge.SubmitForReviewAsync(id, "New policy text agreed with Security. Please check the wording on personal phones.", ct);
                }
            }
            if (publisher is not null)
            {
                using (actors.BeginScope(Actor.ForUser(publisher, Channel.Web)))
                {
                    await scope.ServiceProvider.GetRequiredService<IKnowledgeService>().PublishAsync(id, ct: ct);
                }
            }
            if (views > 0)
            {
                await db.Set<KbArticle>().Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ViewCount, views), ct);
            }
            created[key] = id;
            return id;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await Write(priya, priya, connected, "vpn", "Connect to the VPN from home or while travelling", DemoArticles.Vpn, ["vpn", "remote", "travel"], views: 412, reviewBy: today.AddMonths(4));
        await Write(sam, priya, connected, "vpn-drops", "VPN keeps disconnecting on hotel or café Wi-Fi", DemoArticles.VpnDrops, ["vpn", "wi-fi", "travel"], KbArticleType.Workaround, views: 187, reviewBy: today.AddDays(-3));
        await Write(priya, priya, connected, "guest-wifi", "Get a guest Wi-Fi code for visitors", DemoArticles.GuestWifi, ["wi-fi", "guests"], views: 96);
        await Write(sam, priya, accounts, "mfa", "Set up two-step verification on a new phone", DemoArticles.Mfa, ["mfa", "authenticator", "phone"], views: 358, reviewBy: today.AddMonths(6));
        await Write(sam, priya, accounts, "password", "Reset your password", DemoArticles.Password, ["password", "account"], views: 521);
        await Write(sam, priya, devices, "printer", "Add the office printer to your laptop", DemoArticles.Printer, ["printer", "printing"], views: 233);
        await Write(sam, priya, devices, "duplex", "Printer jams on double-sided jobs", DemoArticles.Duplex, ["printer", "jam", "duplex"], KbArticleType.Workaround, views: 64);
        await Write(sam, priya, devices, "battery", "My laptop battery is swollen", DemoArticles.Battery, ["laptop", "battery", "safety"], views: 41);
        await Write(sam, priya, email, "outlook", "Outlook keeps asking for my password", DemoArticles.Outlook, ["outlook", "password", "m365"], views: 298);
        await Write(sam, priya, email, "shared-mailbox", "Open a shared mailbox in Outlook", DemoArticles.SharedMailbox, ["outlook", "shared mailbox"], views: 120);
        await Write(priya, priya, finSystems, "erp", "Request access to the finance reporting workspace", DemoArticles.FinanceAccess, ["finance", "access"], views: 18);
        await Write(priya, priya, runbooks, "mfa-runbook", "Runbook: reset a person's two-step verification", DemoArticles.MfaRunbook, ["mfa", "runbook"], views: 77);
        await Write(sam, null, security, "byod", "Using a personal phone for work email", DemoArticles.Byod, ["policy", "phone", "security"], submit: true);
        await Write(sam, null, email, "calendar-draft", "Share your calendar with an assistant", DemoArticles.CalendarDraft, ["calendar", "outlook"]);

        // Related articles and a few votes, so the article pages look lived in.
        db.Set<KbArticleRelation>().AddRange(
            new KbArticleRelation { ArticleId = created["vpn"], RelatedArticleId = created["vpn-drops"] },
            new KbArticleRelation { ArticleId = created["mfa"], RelatedArticleId = created["password"] },
            new KbArticleRelation { ArticleId = created["outlook"], RelatedArticleId = created["password"] },
            new KbArticleRelation { ArticleId = created["printer"], RelatedArticleId = created["duplex"] });
        var requesters = users.Values.Where(u => !u.IsAgent).ToList();
        var now = DateTimeOffset.UtcNow;
        var votes = new (string Key, bool Helpful, string? Comment)[]
        {
            ("vpn", true, null), ("vpn", true, null), ("vpn", false, "The screenshots show the old client."),
            ("outlook", true, "Signing out and back in fixed it straight away."), ("outlook", true, null),
            ("mfa", true, null), ("printer", false, "I'm on a Mac and the steps are only for Windows."),
        };
        for (var i = 0; i < votes.Length && requesters.Count > 0; i++)
        {
            var voter = requesters[i % requesters.Count];
            if (db.Set<KbArticleFeedback>().Local.Any(f => f.ArticleId == created[votes[i].Key] && f.UserId == voter.Id))
            {
                continue;
            }
            db.Set<KbArticleFeedback>().Add(new KbArticleFeedback
            {
                ArticleId = created[votes[i].Key],
                UserId = voter.Id,
                Helpful = votes[i].Helpful,
                Comment = votes[i].Comment,
                CreatedAt = now.AddHours(-i * 7),
                UpdatedAt = now.AddHours(-i * 7),
            });
        }
        await db.SaveChangesAsync(ct);

        using (actors.BeginScope(Actor.ForUser(admin, Channel.Web)))
        {
            var announcements = sp.GetRequiredService<IKbAnnouncementService>();
            var saturday = now.Date.AddDays(((int)DayOfWeek.Saturday - (int)now.DayOfWeek + 7) % 7 + 0.0);
            await announcements.SaveAsync(null, new KbAnnouncementInput
            {
                WorkspaceId = workspace.Id,
                Title = "VPN maintenance this Saturday, 22:00 to 23:00",
                BodyHtml = $"<p>We're upgrading the VPN gateway on Saturday {saturday:MMMM d}. You'll be disconnected for up to an hour from 22:00. Save your work before then; you won't need to change anything afterwards.</p>",
                Audience = KbAnnouncementAudience.Everyone,
                ActiveFrom = now.AddDays(-1),
                ActiveTo = now.AddDays(6),
                IsPublished = true,
            }, ct);
            await announcements.SaveAsync(null, new KbAnnouncementInput
            {
                WorkspaceId = workspace.Id,
                Title = "Suggested articles now appear on tickets",
                BodyHtml = "<p>The ticket sidebar lists knowledge articles that match the subject. Copy the link into your reply, and if nothing fits, use <strong>Create article from ticket</strong> in the More menu once you've solved it.</p>",
                Audience = KbAnnouncementAudience.Agents,
                ActiveFrom = now.AddHours(-3),
                ActiveTo = now.AddDays(14),
                IsPublished = true,
            }, ct);
        }
        logger.LogInformation("Knowledge demo data ready: {Count} articles", created.Count);
    }
}

/// <summary>Demo article bodies. Written the way a good service desk writes: short steps, plain words.</summary>
internal static class DemoArticles
{
    public const string Vpn = """
        <p>The VPN connects your laptop to the office network so you can reach shared drives, the intranet and internal tools from anywhere. You need it for anything that says "only available on the company network".</p>
        <h2>Before you start</h2>
        <ul>
        <li>Your laptop must be a company laptop. Personal computers can't connect.</li>
        <li>Have your phone nearby: you'll approve the sign-in with two-step verification.</li>
        </ul>
        <h2>Connect</h2>
        <ol>
        <li>Click the shield icon in the system tray (Windows) or menu bar (Mac) and choose <strong>Northwind VPN</strong>.</li>
        <li>Sign in with your work email and password.</li>
        <li>Approve the request on your phone. The shield turns green when you're connected.</li>
        </ol>
        <p>The first connection can take up to a minute while your laptop downloads the latest settings.</p>
        <h2>If it doesn't connect</h2>
        <ul>
        <li><strong>"Server unreachable"</strong>: check that ordinary websites load. Hotel and café networks often need you to accept their terms in a browser first.</li>
        <li><strong>"Authentication failed"</strong>: your password may have expired. <a href="/portal/kb">Reset your password</a>, then try again.</li>
        <li><strong>It connects, then drops</strong>: see <em>VPN keeps disconnecting on hotel or café Wi-Fi</em>.</li>
        </ul>
        <blockquote><p>Travelling abroad? Some countries block VPN traffic. Tell the service desk a week before you go and we'll set you up with an alternative.</p></blockquote>
        """;

    public const string VpnDrops = """
        <p>Some public networks reset long-running connections every 15 to 30 minutes. The VPN notices, drops, and reconnects, and anything you had open over the VPN (the CRM, remote desktop) loses its session.</p>
        <h2>Workaround</h2>
        <ol>
        <li>Right-click the VPN shield and choose <strong>Disconnect</strong>.</li>
        <li>Choose <strong>Settings</strong>, then <strong>Connection</strong>.</li>
        <li>Turn on <strong>Use TCP 443 when UDP is blocked</strong>.</li>
        <li>Connect again.</li>
        </ol>
        <p>The connection is a little slower over TCP but survives the network resets. Switch the setting off again when you're back on a home or office network.</p>
        <h2>Still dropping?</h2>
        <p>Raise a ticket and include the name of the network and roughly how often it drops. We're working with our VPN provider on a permanent fix and these details help.</p>
        """;

    public const string GuestWifi = """
        <p>Visitors use the <strong>Northwind-Guest</strong> network. It reaches the internet but nothing internal, so it's safe to share.</p>
        <h2>Get a code</h2>
        <ol>
        <li>Ask reception, or request <strong>Guest Wi-Fi access</strong> in the portal.</li>
        <li>Say how many guests and which day. Codes last from 07:00 to 21:00 on the day you choose.</li>
        </ol>
        <p>For events with more than 20 guests, ask at least two working days ahead so we can add capacity.</p>
        """;

    public const string Mfa = """
        <p>Two-step verification protects your account with a code from your phone. When you get a new phone, the authenticator app doesn't move across on its own, so you need to register the new phone.</p>
        <h2>If you still have the old phone</h2>
        <ol>
        <li>On a computer, sign in to <strong>My account</strong> and open <strong>Security info</strong>.</li>
        <li>Choose <strong>Add sign-in method</strong>, then <strong>Authenticator app</strong>.</li>
        <li>Install the authenticator app on the new phone and scan the QR code shown on screen.</li>
        <li>Approve the test notification, then delete the old phone from the list.</li>
        </ol>
        <h2>If the old phone is lost, wiped or traded in</h2>
        <p>You won't be able to sign in on your own. Call the service desk on <strong>extension 4357</strong> (HELP) or ask a colleague to raise a ticket for you. We'll confirm who you are and reset your registration, usually within 15 minutes during office hours.</p>
        <h2>Tips</h2>
        <ul>
        <li>Add a second method, such as a text message to your mobile, so a lost phone never locks you out.</li>
        <li>We will never ask you to read out a code. If someone does, hang up and call us.</li>
        </ul>
        """;

    public const string Password = """
        <p>You can reset your own password at any time, from any device, as long as two-step verification is set up.</p>
        <ol>
        <li>Go to the sign-in page and choose <strong>Forgot password</strong>.</li>
        <li>Enter your work email and approve the request on your phone.</li>
        <li>Choose a new password: at least 14 characters. A short sentence you'll remember works well.</li>
        </ol>
        <p>Your laptop, phone and Outlook will ask for the new password the next time they connect. On a laptop away from the office, connect to the VPN first so the laptop learns the new password.</p>
        <h2>Locked out?</h2>
        <p>After ten wrong attempts your account locks for 15 minutes. Wait, then try again, or call the service desk if it's urgent.</p>
        """;

    public const string Printer = """
        <p>Every floor has a shared printer called <strong>Floor-N-Printer</strong>, where N is the floor number. Print jobs wait until you tap your badge on the printer, so nobody picks up your pages by mistake.</p>
        <h2>Windows</h2>
        <ol>
        <li>Open <strong>Settings</strong>, then <strong>Bluetooth and devices</strong>, then <strong>Printers and scanners</strong>.</li>
        <li>Choose <strong>Add device</strong> and pick <strong>Northwind Follow-Me</strong>.</li>
        </ol>
        <h2>Releasing a print job</h2>
        <ol>
        <li>Print as usual and choose <strong>Northwind Follow-Me</strong>.</li>
        <li>Tap your badge on any office printer. Your jobs appear on the screen.</li>
        <li>Choose <strong>Print all</strong>.</li>
        </ol>
        <p>Jobs you don't release are deleted after 24 hours.</p>
        """;

    public const string Duplex = """
        <p>Some floor printers jam on the second sheet of a double-sided job while we wait for replacement rollers.</p>
        <h2>Workaround</h2>
        <ul>
        <li>Print single-sided, or release the job at another floor's printer.</li>
        <li>If a sheet is stuck, open the side door marked <strong>B</strong> and pull the paper towards you. Don't pull it backwards through the rollers.</li>
        </ul>
        <p>Raise a ticket with the floor number if a printer jams more than once a day so we can prioritise it.</p>
        """;

    public const string Battery = """
        <p>A swollen battery pushes up the trackpad or keyboard, or stops the laptop closing flat. It can catch fire if it's punctured or keeps charging.</p>
        <h2>Do this now</h2>
        <ol>
        <li>Save what you can and shut the laptop down.</li>
        <li>Unplug the charger and don't plug it back in.</li>
        <li>Put the laptop on a hard surface away from anything flammable. Don't press on the case.</li>
        <li>Raise an urgent ticket. We'll bring a loan laptop and collect the damaged one.</li>
        </ol>
        <p>Your files are backed up to OneDrive, so you'll pick up where you left off on the loan laptop.</p>
        """;

    public const string Outlook = """
        <p>Outlook asks for your password over and over when the sign-in token it saved has expired or been revoked. Typing the password into the box usually doesn't help, because the box is asking for the old token.</p>
        <h2>Fix it</h2>
        <ol>
        <li>In Outlook, go to <strong>File</strong>, then <strong>Office Account</strong>.</li>
        <li>Under your name, choose <strong>Sign out</strong>, and confirm.</li>
        <li>Close every Office app: Outlook, Word, Excel and Teams.</li>
        <li>Open Outlook again and sign in when asked. Approve the request on your phone.</li>
        </ol>
        <p>This fixes it for nearly everyone. If you changed your password today, make sure you sign in with the new one.</p>
        <h2>Still prompting?</h2>
        <p>Open Windows <strong>Credential Manager</strong>, choose <strong>Windows Credentials</strong>, and remove every entry that starts with <code>MicrosoftOffice16_Data</code>. Restart the laptop and sign in again.</p>
        <p>If the prompts continue after that, raise a ticket and tell us whether webmail works in a browser.</p>
        """;

    public const string SharedMailbox = """
        <p>If you've been given access to a shared mailbox, such as <strong>reception@</strong> or <strong>invoices@</strong>, it appears in Outlook on its own within an hour.</p>
        <h2>It hasn't appeared</h2>
        <ol>
        <li>Restart Outlook.</li>
        <li>Still missing? Go to <strong>File</strong>, <strong>Account Settings</strong>, <strong>Account Settings</strong> again, then <strong>Change</strong>, <strong>More Settings</strong>, <strong>Advanced</strong>.</li>
        <li>Choose <strong>Add</strong> and type the mailbox name.</li>
        </ol>
        <p>To send as the shared mailbox, click <strong>From</strong> in a new message and choose its address.</p>
        """;

    public const string FinanceAccess = """
        <p>The finance reporting workspace holds month-end packs and forecasts. Access is limited to the Finance team and approved by the department head.</p>
        <ol>
        <li>In the portal, choose <strong>Request a service</strong>, then <strong>Finance reporting access</strong>.</li>
        <li>Say which reports you need and for how long.</li>
        </ol>
        <p>Your department head approves the request, and access is usually live the same day.</p>
        """;

    public const string MfaRunbook = """
        <p>Use this when someone can't approve sign-ins because their phone is lost, wiped or replaced. Resetting the wrong person's verification hands their account to an attacker, so the identity check is not optional.</p>
        <h2>1. Confirm who you're talking to</h2>
        <ul>
        <li>Call them back on the mobile number in the directory, not a number they give you.</li>
        <li>Or confirm on a video call that matches their directory photo.</li>
        <li>Their manager's confirmation by chat also counts.</li>
        </ul>
        <h2>2. Reset</h2>
        <ol>
        <li>In the identity admin centre, open the user and choose <strong>Authentication methods</strong>.</li>
        <li>Choose <strong>Require re-register multifactor authentication</strong>.</li>
        <li>Revoke their sessions so lost devices are signed out.</li>
        </ol>
        <h2>3. Close the loop</h2>
        <p>Add a private note saying how you verified the person, then reply with a link to <em>Set up two-step verification on a new phone</em>.</p>
        """;

    public const string Byod = """
        <p>You can read work email and Teams on your own phone if it meets a few rules that keep company data safe.</p>
        <h2>Your phone must</h2>
        <ul>
        <li>run a version of iOS or Android that still gets security updates;</li>
        <li>lock with a PIN, fingerprint or face, after no more than five minutes;</li>
        <li>use the Outlook and Teams apps, not the built-in mail app.</li>
        </ul>
        <h2>What we can and can't see</h2>
        <p>We can see the phone's model and operating system version, and we can remove work data from the Outlook and Teams apps. We can't see your photos, messages, browsing or location, and we can't wipe your personal data.</p>
        """;

    public const string CalendarDraft = """
        <p>Give an assistant or colleague access to manage your meetings.</p>
        <ol>
        <li>In Outlook, open <strong>Calendar</strong>.</li>
        <li>Choose <strong>Share</strong>, then pick your calendar.</li>
        </ol>
        """;
}
