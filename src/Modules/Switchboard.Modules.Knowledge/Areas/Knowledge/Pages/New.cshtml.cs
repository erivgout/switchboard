using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages;

[Authorize(Policy = "priv:" + KbPrivileges.Write)]
public sealed class NewModel(
    IKnowledgeService knowledge,
    IKnowledgeVisibility visibility,
    SwitchboardDbContext db,
    ITicketService tickets,
    IHtmlContent html) : EditorPage(knowledge, visibility, db)
{
    [BindProperty(SupportsGet = true)] public Guid? Folder { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? Ticket { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadOptionsAsync(ct);
        if (Folders.Count == 0)
        {
            return Page();
        }
        Input.FolderId = Folder is { } f && Folders.Any(o => o.Id == f) ? f : Folders[0].Id;
        Input.OwnerId = Viewer.UserId;
        if (Ticket is { } ticketId)
        {
            await PrefillFromTicketAsync(ticketId, ct);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? intent, CancellationToken ct)
    {
        try
        {
            var id = await SaveAsync(intent, ct);
            return Redirect($"/kb/{id}");
        }
        catch (ValidationException ex)
        {
            ShowErrors(ex);
            await LoadOptionsAsync(ct);
            return Page();
        }
    }

    public Task<IActionResult> OnPostAutosaveAsync(CancellationToken ct) => AutosaveAsync(ct);

    /// <summary>FR-KB-6: the ticket's subject becomes the title; its description and latest agent reply the body.</summary>
    private async Task PrefillFromTicketAsync(Guid ticketId, CancellationToken ct)
    {
        var ticket = await tickets.GetForAgentAsync(ticketId, ct);
        var reply = await Db.TicketConversations.AsNoTracking()
            .Where(c => c.TicketId == ticket.Id && c.Kind == ConversationKind.AgentReply && c.DeletedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => c.BodyHtml)
            .FirstOrDefaultAsync(ct);
        Input.Title = ticket.Subject;
        Input.SourceTicketId = ticket.Id;
        var symptoms = html.FromPlainText(html.ToPlainText(ticket.DescriptionHtml));
        Input.BodyHtml = html.Sanitize(
            (string.IsNullOrWhiteSpace(symptoms) ? "" : $"<h2>Symptoms</h2>{symptoms}")
            + "<h2>Solution</h2>"
            + (string.IsNullOrWhiteSpace(reply) ? "<p></p>" : reply));
        TicketLabel = $"{ticket.DisplayId} {ticket.Subject}";
    }
}
