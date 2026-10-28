using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Messaging.Admin;
using Switchboard.Modules.Messaging.Domain;
using Switchboard.Modules.Messaging.Inbound;
using Switchboard.Modules.Messaging.Outbound;
using Switchboard.UI;

namespace Switchboard.Modules.Messaging.Pages;

[Authorize(Policy = "priv:" + MessagingPrivileges.Manage)]
public abstract class MessagingAdminPage : PageModel
{
    protected void AddErrors(ValidationException ex, string prefix = "")
    {
        foreach (var (key, messages) in ex.Errors)
        {
            foreach (var message in messages)
            {
                ModelState.AddModelError(prefix.Length > 0 ? $"{prefix}.{key}" : key, message);
            }
        }
    }
}

public sealed record WorkspaceOption(Guid Id, string Name);

public sealed record MailboxRow(MsgMailbox Mailbox, string Workspace, string? Group);

public sealed class EmailIndexModel(
    MailboxService mailboxes,
    MessagingAccess access,
    OutboundSettingsStore store,
    InboundAdminService inbound,
    SwitchboardDbContext db,
    IOptions<SwitchboardOptions> options) : MessagingAdminPage
{
    public IReadOnlyList<MailboxRow> Mailboxes { get; private set; } = [];
    public EffectiveMailSettings Outgoing { get; private set; } = new();
    public bool CanManageInstance { get; private set; }
    public bool NonProduction => options.Value.NonProduction;
    public IReadOnlyList<MsgInboundMessage> Recent { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var list = await mailboxes.ListAsync(ct);
        var workspaces = await db.Workspaces.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var groupIds = list.Where(m => m.DefaultGroupId is not null).Select(m => m.DefaultGroupId!.Value).ToList();
        var groups = await db.AgentGroups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        Mailboxes = list.Select(m => new MailboxRow(m, workspaces.GetValueOrDefault(m.WorkspaceId, "Unknown workspace"),
            m.DefaultGroupId is { } g ? groups.GetValueOrDefault(g) : null)).ToList();
        Outgoing = await store.GetEffectiveAsync(ct);
        CanManageInstance = await access.CanManageInstanceAsync(ct);
        Recent = await inbound.ListAsync(null, 5, ct);
    }
}

public sealed class MailboxEditModel(
    MailboxService mailboxes,
    MessagingAccess access,
    SwitchboardDbContext db,
    IOptions<SwitchboardOptions> options) : MessagingAdminPage
{
    public const string SecretKey = "sb.messaging.secret";

    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = "new";

    [BindProperty]
    public MailboxInput Input { get; set; } = new();

    public MsgMailbox? Mailbox { get; private set; }
    public bool IsNew => Mailbox is null;
    public bool HasImapPassword => Mailbox?.ImapPasswordProtected is not null;
    public string? NewSecret { get; private set; }
    public IReadOnlyList<WorkspaceOption> Workspaces { get; private set; } = [];
    public IReadOnlyList<SelectListItem> Groups { get; private set; } = [];
    public string WebhookUrl => Mailbox is null ? "" : $"{options.Value.BaseUrl.TrimEnd('/')}/email/inbound/{Mailbox.Id}";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }
        if (Mailbox is { } m)
        {
            Input = new MailboxInput
            {
                Name = m.Name,
                Address = m.Address,
                WorkspaceId = m.WorkspaceId,
                DefaultGroupId = m.DefaultGroupId,
                TicketType = m.TicketType,
                Connection = m.Connection,
                IsEnabled = m.IsEnabled,
                ImapHost = m.ImapHost,
                ImapPort = m.ImapPort,
                ImapSecurity = m.ImapSecurity,
                ImapUsername = m.ImapUsername,
                ImapFolder = m.ImapFolder,
                PollIntervalSeconds = m.PollIntervalSeconds,
                TrustedAuthServId = m.TrustedAuthServId,
                TrustUnauthenticated = m.TrustUnauthenticated,
                AllowEmailCommands = m.AllowEmailCommands,
            };
        }
        else
        {
            Input.WorkspaceId = Workspaces.FirstOrDefault()?.Id ?? Guid.Empty;
        }
        NewSecret = TempData[SecretKey] as string;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }
        try
        {
            var saved = await mailboxes.SaveAsync(Mailbox?.Id, Input, ct);
            if (saved.NewWebhookSecret is { } secret)
            {
                TempData[SecretKey] = secret;
            }
            TempData.Toast(IsNew ? "Mailbox added" : "Mailbox saved");
            return Redirect($"/admin/email/mailboxes/{saved.Mailbox.Id}");
        }
        catch (ValidationException ex)
        {
            AddErrors(ex, nameof(Input));
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRotateSecretAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct) || Mailbox is null)
        {
            return NotFound();
        }
        TempData[SecretKey] = await mailboxes.RotateSecretAsync(Mailbox.Id, ct);
        TempData.Toast("New secret made");
        return Redirect($"/admin/email/mailboxes/{Mailbox.Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct) || Mailbox is null)
        {
            return NotFound();
        }
        await mailboxes.DeleteAsync(Mailbox.Id, ct);
        TempData.Toast("Mailbox deleted");
        return Redirect("/admin/email");
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        if (!string.Equals(Id, "new", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(Id, out var id))
            {
                return false;
            }
            Mailbox = await mailboxes.GetAsync(id, ct);
        }
        var manageable = await access.ManageableWorkspacesAsync(ct);
        Workspaces = await db.Workspaces.AsNoTracking().Where(w => manageable.Contains(w.Id) && !w.IsArchived)
            .OrderByDescending(w => w.IsPrimary).ThenBy(w => w.Name).Select(w => new WorkspaceOption(w.Id, w.Name)).ToListAsync(ct);
        var names = Workspaces.ToDictionary(w => w.Id, w => w.Name);
        var groups = await db.AgentGroups.AsNoTracking().Where(g => manageable.Contains(g.WorkspaceId)).OrderBy(g => g.Name).ToListAsync(ct);
        var optgroups = names.ToDictionary(n => n.Key, n => new SelectListGroup { Name = n.Value });
        Groups = groups.Where(g => optgroups.ContainsKey(g.WorkspaceId))
            .Select(g => new SelectListItem(g.Name, g.Id.ToString()) { Group = Workspaces.Count > 1 ? optgroups[g.WorkspaceId] : null })
            .ToList();
        return true;
    }
}

public sealed class OutgoingModel(
    OutboundSettingsService service,
    OutboundSettingsStore store,
    MessagingAccess access,
    IActorAccessor actors,
    SwitchboardDbContext db,
    IOptions<SwitchboardOptions> options) : MessagingAdminPage
{
    [BindProperty]
    public OutboundSettingsInput Input { get; set; } = new();

    [BindProperty]
    public string? TestAddress { get; set; }

    public EffectiveMailSettings Effective { get; private set; } = new();
    public bool HasPassword { get; private set; }
    public bool HasDkimKey { get; private set; }
    public bool NonProduction => options.Value.NonProduction;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await access.DemandInstanceAsync(ct);
        await LoadAsync(ct);
        var row = await store.GetRowAsync(ct);
        Input = new OutboundSettingsInput
        {
            Host = row?.Host ?? (Effective.Source is MailSettingsSource.DevelopmentDefault or MailSettingsSource.Configuration ? null : Effective.Host),
            Port = row?.Port ?? (Effective.Source == MailSettingsSource.None ? 587 : Effective.Port),
            Security = row?.Security ?? Effective.Security,
            Username = row?.Username,
            FromName = row?.FromName is { Length: > 0 } name ? name : Effective.FromName,
            FromAddress = row?.FromAddress ?? Effective.FromAddress,
            EnvelopeFrom = row?.EnvelopeFrom,
            DkimDomain = row?.DkimDomain,
            DkimSelector = row?.DkimSelector,
            CaptureAddress = row?.CaptureAddress,
            NonProductionAllowlist = row is null ? "" : string.Join(", ", row.NonProductionAllowlist),
        };
        if (row is null && Effective.Source is MailSettingsSource.DevelopmentDefault or MailSettingsSource.None)
        {
            // Show what a real server usually needs, not the Mailpit fallback.
            Input.Host = null;
            Input.Port = 587;
            Input.Security = SmtpSecurity.Auto;
        }
        TestAddress = await CurrentEmailAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            await service.SaveAsync(Input, ct);
            TempData.Toast("Outgoing mail settings saved");
            return Redirect("/admin/email/outgoing");
        }
        catch (ValidationException ex)
        {
            AddErrors(ex, nameof(Input));
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken ct)
    {
        try
        {
            var result = await service.SendTestAsync(TestAddress ?? "", ct);
            var message = result.Status switch
            {
                DeliveryStatus.Sent => $"Test email sent to {TestAddress}",
                DeliveryStatus.Redirected => $"Test email sent to the capture address instead of {TestAddress}",
                DeliveryStatus.Logged => "Test email written to the log. Outgoing mail isn't set up, or this non-production instance has no capture address.",
                _ => $"The test email wasn't sent: {result.Detail}",
            };
            TempData.Toast(message, result.Status == DeliveryStatus.Failed ? ToastKind.Error : ToastKind.Success);
            return Redirect("/admin/email/outgoing");
        }
        catch (ValidationException ex)
        {
            AddErrors(ex);
            await LoadAsync(ct);
            var row = await store.GetRowAsync(ct);
            Input = new OutboundSettingsInput { Host = row?.Host, Port = row?.Port ?? 587, Security = row?.Security ?? SmtpSecurity.Auto, Username = row?.Username, FromName = Effective.FromName, FromAddress = Effective.FromAddress };
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Effective = await store.GetEffectiveAsync(ct);
        var row = await store.GetRowAsync(ct);
        HasPassword = row?.PasswordProtected is not null;
        HasDkimKey = row?.DkimKeyProtected is not null;
    }

    private async Task<string?> CurrentEmailAsync(CancellationToken ct) =>
        actors.Current.UserId is { } id ? await db.Users.Where(u => u.Id == id).Select(u => u.Email).FirstOrDefaultAsync(ct) : null;
}

public sealed class SenderRulesModel(SenderRuleService rules, MessagingAccess access) : MessagingAdminPage
{
    [BindProperty]
    public string? Pattern { get; set; }

    [BindProperty]
    public AddressRuleAction RuleAction { get; set; } = AddressRuleAction.Block;

    [BindProperty]
    public string? Note { get; set; }

    public IReadOnlyList<MsgAddressRule> Rules { get; private set; } = [];
    public IReadOnlyList<MsgBouncedAddress> Bounced { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        await access.DemandInstanceAsync(ct);
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            await rules.AddAsync(Pattern ?? "", RuleAction, Note, ct);
            TempData.Toast(RuleAction == AddressRuleAction.Block ? "Block rule added" : "Allow rule added");
            return Redirect("/admin/email/rules");
        }
        catch (ValidationException ex)
        {
            AddErrors(ex);
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid id, CancellationToken ct)
    {
        await rules.RemoveAsync(id, ct);
        TempData.Toast("Rule removed");
        return Redirect("/admin/email/rules");
    }

    public async Task<IActionResult> OnPostClearBounceAsync(Guid id, CancellationToken ct)
    {
        await rules.ClearBounceAsync(id, ct);
        TempData.Toast("Address cleared");
        return Redirect("/admin/email/rules");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Rules = await rules.ListAsync(ct);
        Bounced = await rules.BouncedAsync(ct);
    }
}

public sealed record InboundFilter(string Key, string Label, string Icon, InboundOutcome? Outcome);

public sealed class IncomingModel(InboundAdminService inbound, MailboxService mailboxes) : MessagingAdminPage
{
    public static readonly IReadOnlyList<InboundFilter> Filters =
    [
        new("all", "All mail", "bi-inboxes", null),
        new("created", "New tickets", "bi-plus-circle", InboundOutcome.Created),
        new("replied", "Replies", "bi-reply", InboundOutcome.Replied),
        new("noted", "Notes", "bi-sticky", InboundOutcome.Noted),
        new("blocked", "Blocked", "bi-shield-x", InboundOutcome.Blocked),
        new("ignored", "Ignored", "bi-eye-slash", InboundOutcome.Ignored),
        new("bounced", "Bounces", "bi-arrow-counterclockwise", InboundOutcome.Bounced),
        new("rejected", "Rejected", "bi-x-octagon", InboundOutcome.Rejected),
        new("failed", "Failed", "bi-exclamation-octagon", InboundOutcome.Failed),
    ];

    [BindProperty(SupportsGet = true, Name = "show")]
    public string? Show { get; set; }

    public InboundFilter Current => Filters.FirstOrDefault(f => f.Key == Show) ?? Filters[0];
    public IReadOnlyList<MsgInboundMessage> Messages { get; private set; } = [];
    public IReadOnlyDictionary<Guid, string> MailboxNames { get; private set; } = new Dictionary<Guid, string>();
    public IReadOnlyDictionary<Guid, string> TicketIds { get; private set; } = new Dictionary<Guid, string>();

    public async Task OnGetAsync([FromServices] SwitchboardDbContext db, CancellationToken ct)
    {
        Messages = await inbound.ListAsync(Current.Outcome, 200, ct);
        MailboxNames = (await mailboxes.ListAsync(ct)).ToDictionary(m => m.Id, m => m.Name);
        var ticketIds = Messages.Where(m => m.TicketId is not null).Select(m => m.TicketId!.Value).Distinct().ToList();
        TicketIds = (await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).Select(t => new { t.Id, t.Type, t.Number }).ToListAsync(ct))
            .ToDictionary(t => t.Id, t => Core.Domain.Ticket.FormatDisplayId(t.Type, t.Number));
    }
}

public sealed class IncomingMessageModel(InboundAdminService inbound, MailboxService mailboxes, SwitchboardDbContext db) : MessagingAdminPage
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public MsgInboundMessage Message { get; private set; } = default!;
    public IReadOnlyList<TraceStep> Trace { get; private set; } = [];
    public string? MailboxName { get; private set; }
    public string? TicketDisplayId { get; private set; }
    public int? TicketNumber { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Message = await inbound.GetAsync(Id, ct);
        try
        {
            Trace = JsonSerializer.Deserialize<List<TraceStep>>(Message.TraceJson, TraceJson.Options) ?? [];
        }
        catch (JsonException)
        {
            Trace = [];
        }
        MailboxName = Message.MailboxId is { } mailboxId ? (await mailboxes.GetAsync(mailboxId, ct)).Name : null;
        if (Message.TicketId is { } ticketId)
        {
            var ticket = await db.Tickets.AsNoTracking().Where(t => t.Id == ticketId)
                .Select(t => new { t.Type, t.Number }).FirstOrDefaultAsync(ct);
            TicketDisplayId = ticket is null ? null : Core.Domain.Ticket.FormatDisplayId(ticket.Type, ticket.Number);
            TicketNumber = ticket?.Number;
        }
    }

    public async Task<IActionResult> OnPostReprocessAsync(CancellationToken ct)
    {
        try
        {
            var result = await inbound.ReprocessAsync(Id, ct);
            TempData.Toast($"Processed again: {result.OutcomeDetail}");
            return Redirect($"/admin/email/incoming/{result.Id}");
        }
        catch (ValidationException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
            return Redirect($"/admin/email/incoming/{Id}");
        }
    }
}

public sealed class SimulateModel(InboundAdminService inbound, MailboxService mailboxes, SwitchboardDbContext db) : MessagingAdminPage
{
    [BindProperty]
    public Guid MailboxId { get; set; }

    [BindProperty]
    public string Mode { get; set; } = "write";

    [BindProperty]
    public SimulatedEmail Input { get; set; } = new();

    public IReadOnlyList<MsgMailbox> Mailboxes { get; private set; } = [];
    public IReadOnlyList<MsgInboundMessage> Recent { get; private set; } = [];

    public async Task OnGetAsync([FromQuery] string? reply, CancellationToken ct)
    {
        await LoadAsync(ct);
        MailboxId = Mailboxes.FirstOrDefault()?.Id ?? Guid.Empty;
        var requester = await db.Users.AsNoTracking().Where(u => !u.IsAgent && !u.IsOrgAdmin && u.IsActive && u.Email != null).OrderBy(u => u.CreatedAt).FirstOrDefaultAsync(ct);
        Input = new SimulatedEmail
        {
            FromName = requester?.DisplayName ?? "Pat Example",
            FromAddress = requester?.Email ?? "pat@example.com",
            Subject = "Can't connect to the office Wi-Fi",
            Body = "Hi,\n\nSince this morning my laptop won't connect to the office Wi-Fi. It says the password is wrong, but I haven't changed it.\n\nThanks",
            ReplyTo = reply,
        };
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Mode != "raw")
        {
            Input.RawMime = null;
        }
        else if (string.IsNullOrWhiteSpace(Input.RawMime))
        {
            ModelState.AddModelError("Input.RawMime", "Paste the raw message, headers first.");
            await LoadAsync(ct);
            return Page();
        }
        try
        {
            var result = await inbound.SimulateAsync(MailboxId, Input, ct);
            TempData.Toast($"Message processed: {result.OutcomeDetail}", result.Outcome is InboundOutcome.Failed or InboundOutcome.Rejected ? ToastKind.Error : ToastKind.Success);
            return Redirect($"/admin/email/incoming/{result.Id}");
        }
        catch (ValidationException ex)
        {
            AddErrors(ex, nameof(Input));
            await LoadAsync(ct);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Mailboxes = await mailboxes.ListAsync(ct);
        Recent = (await inbound.ListAsync(null, 20, ct)).Where(m => m.Source == InboundSource.Simulated).Take(5).ToList();
    }
}
