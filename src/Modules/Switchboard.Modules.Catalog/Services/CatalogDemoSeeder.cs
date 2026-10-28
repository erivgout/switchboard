using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Approvals.Services;
using Switchboard.Modules.Catalog.Domain;

namespace Switchboard.Modules.Catalog.Services;

/// <summary>
/// Development demo catalog: categories, items with forms and approval chains, and a few requests in different states.
/// Runs once after startup (the host's demo people exist by then), only in Development with demo data on.
/// </summary>
internal sealed class CatalogDemoSeeder(
    IServiceScopeFactory scopes,
    IHostEnvironment environment,
    IOptions<SwitchboardOptions> options,
    ILogger<CatalogDemoSeeder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!environment.IsDevelopment() || !options.Value.DemoData)
        {
            return;
        }
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await SeedAsync(scope.ServiceProvider, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Demo catalog data failed");
        }
    }

    internal static async Task SeedAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<SwitchboardDbContext>();
        var actors = sp.GetRequiredService<IActorAccessor>();
        if (await db.Set<CatCategory>().AnyAsync(ct))
        {
            return;
        }
        var users = await db.Users.Where(u => u.Email != null && u.Email.EndsWith("@switchboard.local")).ToDictionaryAsync(u => u.Email!.Split('@')[0], ct);
        if (!users.ContainsKey("admin") || !users.ContainsKey("dana"))
        {
            return;
        }
        var groups = await db.AgentGroups.ToDictionaryAsync(g => g.Name, g => g.Id, ct);
        var engineering = await db.Departments.Where(d => d.Name == "Engineering").Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        var now = DateTimeOffset.UtcNow;

        using (actors.BeginScope(Actor.System("Demo data")))
        {
            var devices = new CatCategory { Name = "Devices", Icon = "bi-laptop", Order = 1, Description = "Laptops, monitors, phones and accessories.", CreatedAt = now };
            var software = new CatCategory { Name = "Software", Icon = "bi-window-stack", Order = 2, Description = "Apps and licenses for your work.", CreatedAt = now };
            var access = new CatCategory { Name = "Access", Icon = "bi-key", Order = 3, Description = "Accounts, permissions and shared resources.", CreatedAt = now };
            var workplace = new CatCategory { Name = "Workplace", Icon = "bi-building", Order = 4, Description = "Starting, moving and everything around your desk.", CreatedAt = now };
            db.Set<CatCategory>().AddRange(devices, software, access, workplace);

            var reason = new CatSharedField { Key = "business_reason", Label = "Why do you need it?", FieldType = CatFieldType.Paragraph,
                HelpText = "A sentence or two helps approvers decide quickly.", CreatedAt = now };
            var neededBy = new CatSharedField { Key = "needed_by", Label = "Needed by", FieldType = CatFieldType.Date, CreatedAt = now };
            db.Set<CatSharedField>().AddRange(reason, neededBy);

            string Chain(params ApprovalLevelConfig[] levels) => JsonSerializer.Serialize(new ApprovalConfig { Levels = [.. levels] }, JsonDefaults.Options);
            ApproverSource People(params string[] keys) => new() { Kind = ApproverSourceKind.Users, UserIds = keys.Select(k => users[k].Id).ToList() };

            CatItem Item(string name, string summary, string icon, CatItemKind kind, long? cost, int? days, CatCategory category, string? group,
                string? description = null, int maxQuantity = 1, bool requestedFor = false, string? approval = null, string? message = null)
            {
                var item = new CatItem
                {
                    Name = name, ShortDescription = summary, Description = description, Icon = icon, Kind = kind, CostMinor = cost, DeliveryDays = days,
                    MaxQuantity = maxQuantity, AllowRequestedFor = requestedFor, FulfillmentGroupId = group is null ? null : groups[group],
                    ApprovalConfigJson = approval, PostSubmitMessage = message, State = CatItemState.Published, PublishedAt = now, CreatedAt = now,
                };
                item.Categories.Add(new CatItemCategory { ItemId = item.Id, CategoryId = category.Id });
                db.Set<CatItem>().Add(item);
                return item;
            }

            void Field(CatItem item, string key, string label, CatFieldType type, bool required, string? help = null, params string[] options) =>
                item.Fields.Add(new CatItemField { ItemId = item.Id, Key = key, Label = label, FieldType = type, Required = required, HelpText = help, Options = [.. options], Order = item.Fields.Count + 1 });

            void Shared(CatItem item, CatSharedField shared, bool required) =>
                item.Fields.Add(new CatItemField { ItemId = item.Id, SharedFieldId = shared.Id, Key = shared.Key, Label = shared.Label, FieldType = shared.FieldType, Required = required, Order = item.Fields.Count + 1 });

            var managerOrLead = new ApprovalLevelConfig { Name = "Manager approval", Rule = ApprovalRule.Any, Sources = [new() { Kind = ApproverSourceKind.Manager, ManagerLevel = 1 }, People("priya")] };

            var laptop = Item("Standard laptop", "A 14-inch laptop with our standard apps, set up and ready to sign in.", "bi-laptop", CatItemKind.Hardware,
                129900, 5, devices, "Hardware",
                "Comes with Windows 11 or macOS, a charger and a sleeve. Your old laptop goes back to the service desk within two weeks of the new one arriving.\n\nNeed something more powerful for engineering work? Say so in the reason and the hardware team will suggest a model.",
                requestedFor: true, approval: Chain(managerOrLead), message: "Your manager approves laptop requests. We'll email you when it's ready to collect.");
            Field(laptop, "operating_system", "Operating system", CatFieldType.Dropdown, true, null, "Windows 11", "macOS");
            Field(laptop, "keyboard_layout", "Keyboard layout", CatFieldType.Dropdown, false, null, "US English", "UK English", "German", "French");
            Shared(laptop, neededBy, false);
            Shared(laptop, reason, true);

            var monitor = Item("Monitor", "A second screen for your desk, with the cables you need.", "bi-display", CatItemKind.Hardware, 24900, 3, devices, "Hardware", maxQuantity: 2);
            Field(monitor, "size", "Size", CatFieldType.Dropdown, true, null, "24-inch", "27-inch");
            var headset = Item("Headset", "Noise-cancelling headset for calls, with USB-C and Bluetooth.", "bi-headset", CatItemKind.Hardware, 8900, 2, devices, "Hardware");

            var acrobat = Item("Adobe Acrobat Pro", "Edit, sign and combine PDFs. Annual license.", "bi-file-earmark-text", CatItemKind.Software, 23988, 1, software, "Service Desk",
                approval: Chain(new ApprovalLevelConfig { Name = "Budget", Rule = ApprovalRule.Any, Sources = [People("dana")] }));
            Shared(acrobat, reason, true);
            var ide = Item("IDE license", "A JetBrains All Products license for engineers.", "bi-code-slash", CatItemKind.Software, 77900, 1, software, "Service Desk");
            if (engineering is { } eng)
            {
                ide.VisibleToDepartmentIds = [eng];
            }

            var mailbox = Item("Shared mailbox access", "Read or send from a team mailbox, such as finance@ or hello@.", "bi-envelope", CatItemKind.Standard, null, 0, access, "Service Desk",
                requestedFor: true, approval: Chain(new ApprovalLevelConfig { Name = "Mailbox owner", Rule = ApprovalRule.Any, Sources = [new() { Kind = ApproverSourceKind.LookupField, FieldKey = "mailbox_owner" }] }));
            Field(mailbox, "mailbox", "Mailbox address", CatFieldType.Text, true, "For example finance@northwind.example");
            Field(mailbox, "access_level", "Access", CatFieldType.Dropdown, true, null, "Read only", "Read and send as");
            Field(mailbox, "mailbox_owner", "Mailbox owner", CatFieldType.UserLookup, true, "They approve who gets access.");

            var admin = Item("Local admin rights", "Temporary administrator rights on your computer, for installing developer tools.", "bi-shield-lock", CatItemKind.Standard, null, 1, access, "Service Desk",
                approval: Chain(
                    new ApprovalLevelConfig { Name = "Service desk review", Rule = ApprovalRule.Any, Sources = [new() { Kind = ApproverSourceKind.AgentGroup, GroupId = groups["Service Desk"] }] },
                    new ApprovalLevelConfig { Name = "Security", Rule = ApprovalRule.Any, Sources = [People("admin")] }));
            Field(admin, "duration", "For how long?", CatFieldType.Dropdown, true, null, "1 day", "1 week", "30 days");
            Shared(admin, reason, true);

            var starter = Item("New starter kit", "Everything a new team member needs on day one: laptop, monitor and headset.", "bi-gift", CatItemKind.Bundle, 159900, 5, workplace, "Hardware",
                requestedFor: true, approval: Chain(managerOrLead));
            starter.BundleItemIds = [laptop.Id, monitor.Id, headset.Id];
            Field(starter, "start_date", "Start date", CatFieldType.Date, true);
            Field(starter, "extras", "Also set up", CatFieldType.MultiSelect, false, null, "Desk phone", "Building badge", "Parking permit");

            var phone = Item("Mobile phone", "A company phone with a data plan.", "bi-phone", CatItemKind.Hardware, 69900, 5, devices, "Hardware");
            phone.State = CatItemState.Draft;
            phone.PublishedAt = null;

            await db.SaveChangesAsync(ct);

            // Requests in different states, made the way people make them.
            var requests = sp.GetRequiredService<ICatalogRequestService>();
            var approvals = sp.GetRequiredService<IApprovalService>();
            async Task<SubmitResult> Ask(string who, CatItem item, Dictionary<string, string[]> answers, int quantity = 1, string? forWhom = null)
            {
                using var _ = actors.BeginScope(Actor.ForUser(users[who], Channel.Portal));
                return await requests.SubmitAsync(new SubmitRequestCommand
                {
                    ItemId = item.Id,
                    Quantity = quantity,
                    Answers = answers.ToDictionary(a => a.Key, a => (IReadOnlyList<string>)a.Value),
                    RequestedForId = forWhom is null ? null : users[forWhom].Id,
                }, ct);
            }
            async Task Decide(string who, AprRequest approval, ApprovalVerdict verdict, string? comment)
            {
                using var _ = actors.BeginScope(Actor.ForUser(users[who], users[who].IsAgent ? Channel.Web : Channel.Portal));
                await approvals.DecideAsync(approval.Id, new DecideApprovalCommand { Verdict = verdict, Comment = comment }, ct);
            }

            var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10).ToString("yyyy-MM-dd");
            await Ask("dana", laptop, new() { ["operating_system"] = ["Windows 11"], ["keyboard_layout"] = ["US English"], ["needed_by"] = [day],
                ["business_reason"] = ["My laptop's battery is swelling (see my other ticket). I need a replacement before quarter close."] });
            await Ask("elena", acrobat, new() { ["business_reason"] = ["I review and sign vendor security questionnaires every week."] });
            await Ask("marcus", admin, new() { ["duration"] = ["1 week"], ["business_reason"] = ["I need to install the new CRM plug-in for the client demo."] });
            // A single-approver item, so one "no" rejects it; Dana can then change it and send it again.
            var rejected = await Ask("dana", mailbox, new() { ["mailbox"] = ["finance-reports@northwind.example"], ["access_level"] = ["Read and send as"],
                ["mailbox_owner"] = [users["grace"].Id.ToString()] });
            await Decide("grace", rejected.Approval!, ApprovalVerdict.Rejected,
                "Read only is enough for the month-end reports; sending as the mailbox is limited to the finance leads. Ask again for read-only access.");
            var monitors = await Ask("marcus", monitor, new() { ["size"] = ["27-inch"] }, quantity: 2);
            await Ask("grace", headset, []);
            await Ask("tom", mailbox, new() { ["mailbox"] = ["people@northwind.example"], ["access_level"] = ["Read and send as"], ["mailbox_owner"] = [users["grace"].Id.ToString()] });

            // The rejection follow-up normally runs from the outbox; run it now so the demo ticket is resolved on first view.
            using (actors.BeginScope(Actor.System("Demo data")))
            {
                var latest = await db.Set<AprRequest>().AsNoTracking().Include(r => r.Decisions).FirstAsync(r => r.Id == rejected.Approval!.Id, ct);
                var decision = latest.Decisions.Single();
                await ActivatorUtilities.CreateInstance<CatalogApprovalFollowUp>(sp).HandleAsync(new ApprovalDecided(latest.Id, latest.SubjectType, latest.SubjectId,
                    latest.WorkspaceId, decision.Id, decision.ApproverId, decision.DecidedById, decision.OnBehalfOfId, decision.Verdict, latest.Status, decision.Comment), ct);
                var monitorRow = await db.Set<CatRequestedItem>().FirstAsync(r => r.Id == monitors.Item.Id, ct);
                monitorRow.Stage = RequestedItemStage.InProgress;
                monitorRow.StageChangedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
    }
}
