using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Messaging.Notifications;
using Switchboard.UI;

namespace Switchboard.Modules.Messaging.Pages;

public sealed record TemplateRowModel(ResolvedTemplate Template, Guid WorkspaceId);

public sealed class NotificationTemplatesModel(TemplateService templates, MessagingAccess access, SwitchboardDbContext db) : MessagingAdminPage
{
    [BindProperty(SupportsGet = true, Name = "ws")]
    public Guid? WorkspaceId { get; set; }

    public IReadOnlyList<WorkspaceOption> Workspaces { get; private set; } = [];
    public IReadOnlyList<ResolvedTemplate> Templates { get; private set; } = [];
    public Guid CurrentWorkspace { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }
        Templates = await templates.ListAsync(CurrentWorkspace, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostToggleAsync(string key, bool enabled, CancellationToken ct)
    {
        if (!await LoadAsync(ct) || NotificationCatalog.Find(key) is null)
        {
            return NotFound();
        }
        await templates.SetEnabledAsync(CurrentWorkspace, key, enabled, ct);
        var name = NotificationCatalog.Get(key).Name;
        if (Request.IsHtmx())
        {
            Response.HxToast(enabled ? $"{name} turned on" : $"{name} turned off");
            return Partial("_TemplateRow", new TemplateRowModel(await templates.ResolveAsync(CurrentWorkspace, key, ct), CurrentWorkspace));
        }
        TempData.Toast(enabled ? $"{name} turned on" : $"{name} turned off");
        return Redirect($"/admin/notifications?ws={CurrentWorkspace}");
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        var manageable = await access.ManageableWorkspacesAsync(ct);
        Workspaces = await db.Workspaces.AsNoTracking().Where(w => manageable.Contains(w.Id) && !w.IsArchived)
            .OrderByDescending(w => w.IsPrimary).ThenBy(w => w.Name).Select(w => new WorkspaceOption(w.Id, w.Name)).ToListAsync(ct);
        if (Workspaces.Count == 0)
        {
            return false;
        }
        CurrentWorkspace = WorkspaceId is { } ws && Workspaces.Any(w => w.Id == ws) ? ws : Workspaces[0].Id;
        return WorkspaceId is null || WorkspaceId == CurrentWorkspace;
    }
}

public sealed record TemplatePreview(string Subject, string FrameHtml, IReadOnlyList<string> Unknown);

public sealed class TemplateEditModel(
    TemplateService templates,
    MessagingAccess access,
    SwitchboardDbContext db,
    TicketFactsLoader loader,
    IHtmlContent html,
    IOptions<SwitchboardOptions> options) : MessagingAdminPage
{
    [BindProperty(SupportsGet = true)]
    public string Key { get; set; } = "";

    [BindProperty(SupportsGet = true, Name = "ws")]
    public Guid? WorkspaceId { get; set; }

    [BindProperty]
    public string Subject { get; set; } = "";

    [BindProperty]
    public string Body { get; set; } = "";

    [BindProperty]
    public bool Enabled { get; set; }

    public NotificationEvent Event { get; private set; } = default!;
    public ResolvedTemplate Current { get; private set; } = default!;
    public Guid CurrentWorkspace { get; private set; }
    public string WorkspaceName { get; private set; } = "";
    public bool ShowWorkspace { get; private set; }
    public TemplatePreview Preview { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }
        Subject = Current.Subject;
        Body = Current.BodyHtml;
        Enabled = Current.IsEnabled;
        Preview = await RenderAsync(Subject, Body, ct);
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
            await templates.SaveAsync(CurrentWorkspace, Key, Subject, Body, Enabled, ct);
            TempData.Toast($"{Event.Name} saved");
            return Redirect($"/admin/notifications/{Key}?ws={CurrentWorkspace}");
        }
        catch (ValidationException ex)
        {
            AddErrors(ex);
            Preview = await RenderAsync(Subject, Body, ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostResetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }
        await templates.ResetAsync(CurrentWorkspace, Key, ct);
        TempData.Toast($"{Event.Name} reset to the default wording");
        return Redirect($"/admin/notifications/{Key}?ws={CurrentWorkspace}");
    }

    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }
        return Partial("_TemplatePreview", await RenderAsync(Subject, Body, ct));
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        if (NotificationCatalog.Find(Key) is not { } definition)
        {
            return false;
        }
        Event = definition;
        var manageable = await access.ManageableWorkspacesAsync(ct);
        var workspaces = await db.Workspaces.AsNoTracking().Where(w => manageable.Contains(w.Id) && !w.IsArchived)
            .OrderByDescending(w => w.IsPrimary).ThenBy(w => w.Name).ToListAsync(ct);
        var workspace = WorkspaceId is { } ws ? workspaces.FirstOrDefault(w => w.Id == ws) : workspaces.FirstOrDefault();
        if (workspace is null)
        {
            return false;
        }
        await access.DemandWorkspaceAsync(workspace.Id, ct);
        CurrentWorkspace = workspace.Id;
        WorkspaceName = workspace.Name;
        ShowWorkspace = workspaces.Count > 1;
        Current = await templates.ResolveAsync(CurrentWorkspace, Key, ct);
        return true;
    }

    /// <summary>Renders against a made-up ticket, so no real requester's data shows in the preview.</summary>
    private async Task<TemplatePreview> RenderAsync(string subject, string body, CancellationToken ct)
    {
        var org = await loader.OrganizationNameAsync(ct);
        var agent = new User { DisplayName = "Sam Rivera", Email = "sam@example.com", Signature = "Sam Rivera\nIT Service Desk" };
        var requester = new User { DisplayName = "Dana Whitfield", Email = "dana@example.com" };
        var ticket = new Ticket
        {
            Number = 1042,
            Type = TicketType.Incident,
            WorkspaceId = CurrentWorkspace,
            Subject = "Laptop won't connect to the VPN",
            DescriptionHtml = "<p>Since this morning my laptop says the VPN certificate has expired. I'm working from home today.</p>",
            Priority = Priority.High,
        };
        var facts = new TicketFacts(ticket, new TicketStatus { Name = "Open", RequesterLabel = "Being worked on", Category = StatusCategory.Open },
            requester, agent, new AgentGroup { Name = "Service Desk" }, org, options.Value.BaseUrl.TrimEnd('/'), true);
        var message = new MessageFacts(
            Event.Key switch
            {
                NotificationCatalog.AgentRequesterReplied => "<p>I restarted it twice and it still says the certificate expired.</p>",
                NotificationCatalog.AgentMentioned => "<p>Sam, can you check whether Dana's VPN certificate was renewed with the others last week?</p>",
                _ => "<p>Thanks, Dana. I've renewed your VPN certificate. Restart the VPN app and try connecting again.</p>",
            },
            Event.Key == NotificationCatalog.AgentRequesterReplied ? requester.DisplayName : agent.DisplayName,
            Event.Key == NotificationCatalog.AgentRequesterReplied ? requester : agent);
        var recipient = Event.Audience == NotificationAudience.Requester ? requester.DisplayName : Event.Audience == NotificationAudience.Agent ? agent.DisplayName : "Alex Vendor";
        var values = TicketFactsLoader.Values(facts, message, "Priya Shah", recipient, html);

        var renderedSubject = TicketMailer.WithToken(TemplateRenderer.RenderSubject(subject ?? "", values), ticket.DisplayId);
        var renderedBody = EmailLayout.AbsolutizeLinks(html.Sanitize(TemplateRenderer.RenderHtml(body ?? "", values)), facts.BaseUrl);
        var footer = Event.Audience == NotificationAudience.Agent
            ? $"Reference {ticket.DisplayId}. Reply to this email to add a private note. Change which emails you get on your profile."
            : $"Reference {ticket.DisplayId}";
        var frame = EmailLayout.Frame(renderedBody, org, footer, Event.Audience == NotificationAudience.Requester);
        return new TemplatePreview(renderedSubject, frame, TemplateRenderer.UnknownPlaceholders((subject ?? "") + " " + (body ?? "")));
    }
}
