using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.ServiceDesk.Services;
using Switchboard.UI;

namespace Switchboard.Modules.ServiceDesk.Areas.ServiceDesk.Pages.Tickets;

[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class DetailModel(
    TicketPageLoader loader,
    ITicketService tickets,
    CannedResponseService canned,
    IPermissionService permissions,
    IActorAccessor actors,
    SwitchboardDbContext db,
    IAuditLog audit,
    TimeProvider time) : PageModel
{
    public TicketPageData Data { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(int number, CancellationToken ct)
    {
        Data = await loader.LoadAsync(number, ct);
        ViewData["Title"] = $"{Data.Ticket.DisplayId} {Data.Ticket.Subject}";
        ViewData["WatchTicket"] = Data.Ticket.Id;
        return Page();
    }

    // ---------------------------------------------------------------- properties (htmx)

    public sealed class PropertyInput
    {
        public string Field { get; set; } = "";
        public List<string?> Value { get; set; } = [];
        public string? Version { get; set; }
        public long? SeenAt { get; set; }
        public string? Reason { get; set; }
        /// <summary>Minutes to add to the browser's local time to get UTC (JavaScript getTimezoneOffset).</summary>
        public int? TzOffset { get; set; }
    }

    public async Task<IActionResult> OnPostPropertyAsync(int number, [FromForm] PropertyInput input, CancellationToken ct)
    {
        var ticket = await FindAsync(number, ct);
        var options = await HttpContext.RequestServices.GetRequiredService<TicketFormOptions>().LoadAsync(ticket.WorkspaceId, ct);
        var patch = BuildPatch(input, options, out var label);
        byte[]? version = null;
        if (!string.IsNullOrEmpty(input.Version))
        {
            try
            {
                version = Convert.FromBase64String(input.Version);
            }
            catch (FormatException)
            {
                version = null;
            }
        }

        try
        {
            await tickets.UpdateAsync(ticket.Id, patch, version, ct);
        }
        catch (ConflictException)
        {
            var since = input.SeenAt is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : time.GetUtcNow().AddHours(-1);
            return Conflict(await loader.DescribeChangesSinceAsync(ticket.Id, since, ct), "changed");
        }

        Data = await loader.LoadAsync(number, ct);
        Response.HxToast($"{label} saved.");
        return Partial("_Props", this);
    }

    private static TicketPatch BuildPatch(PropertyInput input, WorkspaceOptions options, out string label)
    {
        var value = input.Value.FirstOrDefault(v => v is not null)?.Trim() ?? "";
        Guid? OptionalId() => value.Length == 0 ? null : Guid.TryParse(value, out var id) ? id : throw new ValidationException(input.Field, "Choose an option from the list.");
        Guid RequiredId() => OptionalId() ?? throw new ValidationException(input.Field, "Choose an option from the list.");
        T Enum<T>() where T : struct, System.Enum =>
            System.Enum.TryParse<T>(value, out var parsed) && System.Enum.IsDefined(parsed) ? parsed : throw new ValidationException(input.Field, "Choose an option from the list.");

        switch (input.Field)
        {
            case "subject": label = "Subject"; return new TicketPatch { Subject = value };
            case "status": label = "Status"; return new TicketPatch { StatusId = RequiredId() };
            case "priority": label = "Priority"; return new TicketPatch { Priority = Enum<Priority>() };
            case "urgency": label = "Urgency"; return new TicketPatch { Urgency = Enum<Level>() };
            case "impact": label = "Impact"; return new TicketPatch { Impact = Enum<Level>() };
            case "type": label = "Type"; return new TicketPatch { Type = Enum<TicketType>() };
            case "source": label = "Source"; return new TicketPatch { SourceId = RequiredId() };
            case "group": label = "Group"; return new TicketPatch { GroupId = OptionalId() };
            case "agent": label = "Agent"; return new TicketPatch { AgentId = OptionalId() };
            case "category": label = "Category"; return new TicketPatch { CategoryId = OptionalId() };
            case "subcategory": label = "Sub-category"; return new TicketPatch { SubCategoryId = OptionalId() };
            case "item": label = "Item"; return new TicketPatch { ItemCategoryId = OptionalId() };
            case "department": label = "Department"; return new TicketPatch { DepartmentId = OptionalId() };
            case "tags":
                label = "Tags";
                return new TicketPatch { Tags = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
            case "cc":
                label = "CC";
                return new TicketPatch { CcEmails = value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
            case "dueBy" or "firstResponseDueBy":
            {
                label = input.Field == "dueBy" ? "Resolution due" : "First response due";
                DateTimeOffset? at = null;
                if (value.Length > 0)
                {
                    if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                    {
                        throw new ValidationException(input.Field, "Enter a date and time.");
                    }
                    at = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.Zero).AddMinutes(input.TzOffset ?? 0);
                }
                return input.Field == "dueBy"
                    ? new TicketPatch { DueBy = at, DueOverrideReason = input.Reason }
                    : new TicketPatch { FirstResponseDueBy = at, DueOverrideReason = input.Reason };
            }
            default:
                if (input.Field.StartsWith(CustomFieldForm.InputPrefix, StringComparison.Ordinal))
                {
                    var key = input.Field[CustomFieldForm.InputPrefix.Length..];
                    var def = options.CustomFields.FirstOrDefault(d => d.Key == key) ?? throw new ValidationException(input.Field, "That field doesn't exist.");
                    label = def.Label;
                    return new TicketPatch { CustomFields = new Dictionary<string, JsonElement?> { [key] = CustomFieldForm.FromForm(def, input.Value) } };
                }
                throw new ValidationException("field", "That field can't be changed here.");
        }
    }

    // ---------------------------------------------------------------- conversation (fetch)

    public sealed class ComposerInput
    {
        /// <summary><c>reply</c>, <c>note</c> (private), <c>public</c> (public note) or <c>forward</c>.</summary>
        public string Mode { get; set; } = "reply";
        public string? BodyHtml { get; set; }
        public string? To { get; set; }
        public string? Cc { get; set; }
        public string? Bcc { get; set; }
        public string? ForwardCc { get; set; }
        public List<Guid> AttachmentIds { get; set; } = [];
        public List<Guid> Mentions { get; set; } = [];
        public Guid? StatusId { get; set; }
        /// <summary>Unix milliseconds of the newest message the agent had seen.</summary>
        public long? SeenUntil { get; set; }
        /// <summary>Send even though someone else posted since the page loaded.</summary>
        public bool Force { get; set; }
    }

    public async Task<IActionResult> OnPostSendAsync(int number, [FromForm] ComposerInput input, CancellationToken ct)
    {
        var ticket = await FindAsync(number, ct);
        var me = actors.Current.UserId;

        // Collision check (FR-TKT-9): warn before posting over messages the agent hasn't seen.
        if (!input.Force && input.SeenUntil is { } seenMs)
        {
            var seen = DateTimeOffset.FromUnixTimeMilliseconds(seenMs);
            var newer = await db.TicketConversations.AsNoTracking()
                .Where(c => c.TicketId == ticket.Id && c.CreatedAt > seen && c.DeletedAt == null && c.Kind != ConversationKind.System && c.AuthorId != me)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new { c.Kind, c.IsPrivate, Author = c.Author != null ? c.Author.DisplayName : c.FromEmail })
                .ToListAsync(ct);
            if (newer.Count > 0)
            {
                var latest = newer[0];
                var what = latest.Kind switch
                {
                    ConversationKind.RequesterMessage => "replied",
                    ConversationKind.Note => latest.IsPrivate ? "added a private note" : "added a public note",
                    ConversationKind.Forward => "forwarded the ticket",
                    _ => "replied",
                };
                var more = newer.Count > 1 ? $" ({newer.Count} new messages)" : "";
                return Conflict($"{latest.Author ?? "Someone"} {what} while you were writing{more}. Read it before you send.", "messages");
            }
        }

        var body = input.BodyHtml ?? "";
        switch (input.Mode)
        {
            case "reply":
                await tickets.ReplyAsync(ticket.Id, new ReplyCommand
                {
                    BodyHtml = body,
                    Cc = Split(input.Cc),
                    Bcc = Split(input.Bcc),
                    AttachmentIds = input.AttachmentIds,
                    StatusId = input.StatusId,
                }, ct);
                TempData.Toast(input.StatusId is null ? "Reply sent." : "Reply sent and status updated.");
                break;
            case "note" or "public":
            {
                // The note and the status change succeed or fail together, so a blocked status can't leave a note behind.
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await tickets.AddNoteAsync(ticket.Id, new NoteCommand
                {
                    BodyHtml = body,
                    IsPrivate = input.Mode == "note",
                    NotifyUserIds = input.Mentions,
                    AttachmentIds = input.AttachmentIds,
                }, ct);
                if (input.StatusId is { } statusId && statusId != ticket.StatusId)
                {
                    await tickets.UpdateAsync(ticket.Id, new TicketPatch { StatusId = statusId }, ct: ct);
                }
                await transaction.CommitAsync(ct);
                TempData.Toast(input.Mode == "note" ? "Private note added." : "Public note added.");
                break;
            }
            case "forward":
                await tickets.ForwardAsync(ticket.Id, new ForwardCommand
                {
                    To = Split(input.To) ?? [],
                    Cc = Split(input.ForwardCc),
                    BodyHtml = body,
                    AttachmentIds = input.AttachmentIds,
                }, ct);
                TempData.Toast("Forwarded.");
                break;
            default:
                throw new ValidationException("mode", "Choose reply, note or forward.");
        }
        return new NoContentResult();
    }

    public async Task<IActionResult> OnGetEditNoteAsync(int number, Guid id, CancellationToken ct)
    {
        var ticket = await FindAsync(number, ct);
        var note = await OwnNoteAsync(ticket.Id, id, ct);
        return Partial("_EditNote", new EditNoteModel(number, note));
    }

    public async Task<IActionResult> OnPostEditNoteAsync(int number, Guid id, [FromForm] string? bodyHtml, CancellationToken ct)
    {
        await FindAsync(number, ct);
        await tickets.EditNoteAsync(id, bodyHtml ?? "", ct);
        Response.HxToast("Note updated.");
        return await MessagePartialAsync(number, id, ct);
    }

    /// <summary>Cancel an edit: the message as it was.</summary>
    public Task<IActionResult> OnGetMessageAsync(int number, Guid id, CancellationToken ct) => MessagePartialAsync(number, id, ct);

    public async Task<IActionResult> OnPostDeleteNoteAsync(int number, Guid id, CancellationToken ct)
    {
        var ticket = await FindAsync(number, ct);
        await OwnNoteAsync(ticket.Id, id, ct);
        await tickets.HideConversationAsync(id, ct);
        Response.HxToast("Note deleted.");
        return Content("");
    }

    /// <summary>Hide a sent message (admins, tickets.delete). Audited by the ticket service.</summary>
    public async Task<IActionResult> OnPostHideMessageAsync(int number, Guid id, CancellationToken ct)
    {
        var ticket = await FindAsync(number, ct);
        if (!await db.TicketConversations.AnyAsync(c => c.Id == id && c.TicketId == ticket.Id, ct))
        {
            throw new NotFoundException();
        }
        await tickets.HideConversationAsync(id, ct);
        Response.HxToast("Message hidden.");
        return await MessagePartialAsync(number, id, ct);
    }

    private async Task<IActionResult> MessagePartialAsync(int number, Guid id, CancellationToken ct)
    {
        Data = await loader.LoadAsync(number, ct);
        var entry = Data.Thread.FirstOrDefault(t => t.Conversation.Id == id);
        return entry is null ? Content("") : Partial("_Message", new MessageModel(Data, entry));
    }

    private async Task<TicketConversation> OwnNoteAsync(Guid ticketId, Guid id, CancellationToken ct)
    {
        var me = actors.Current.UserId;
        return await db.TicketConversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.TicketId == ticketId && c.Kind == ConversationKind.Note && c.AuthorId == me && c.DeletedAt == null, ct)
            ?? throw new NotFoundException();
    }

    // ---------------------------------------------------------------- canned responses

    public async Task<IActionResult> OnGetCannedAsync(int number, string? q, CancellationToken ct)
    {
        await FindAsync(number, ct);
        var items = await canned.SearchAsync(q, 30, ct);
        return Partial("_CannedList", items);
    }

    public async Task<IActionResult> OnGetCannedBodyAsync(int number, Guid id, CancellationToken ct)
    {
        var ticket = await FindAsync(number, ct);
        return new JsonResult(new { html = await canned.RenderAsync(id, ticket.Id, ct) });
    }

    // ---------------------------------------------------------------- tasks (htmx)

    public sealed class TaskInput
    {
        public string? Title { get; set; }
        public Guid? AssigneeId { get; set; }
        public DateTime? DueAt { get; set; }
        public int? TzOffset { get; set; }
    }

    public async Task<IActionResult> OnPostAddTaskAsync(int number, [FromForm] TaskInput input, CancellationToken ct)
    {
        var ticket = await FindAsync(number, ct);
        await tickets.AddTaskAsync(ticket.Id, new TaskCommand
        {
            Title = input.Title ?? "",
            AssigneeId = input.AssigneeId,
            DueAt = input.DueAt is { } due ? new DateTimeOffset(DateTime.SpecifyKind(due, DateTimeKind.Unspecified), TimeSpan.Zero).AddMinutes(input.TzOffset ?? 0) : null,
        }, ct);
        Response.HxToast("Task added.");
        return await TasksPartialAsync(number, ct);
    }

    public async Task<IActionResult> OnPostUpdateTaskAsync(int number, Guid id, [FromForm] string field, [FromForm] string? value, CancellationToken ct)
    {
        var ticket = await FindAsync(number, ct);
        await TaskOfTicketAsync(ticket.Id, id, ct);
        var patch = field switch
        {
            "status" => new TaskPatch
            {
                Status = Enum.TryParse<TicketTaskStatus>(value, out var s) && Enum.IsDefined(s) ? s : throw new ValidationException("status", "Choose a task status."),
            },
            "assignee" => new TaskPatch { AssigneeId = string.IsNullOrEmpty(value) ? null : Guid.TryParse(value, out var a) ? a : throw new ValidationException("assignee", "Choose an agent.") },
            "title" => new TaskPatch { Title = value ?? "" },
            _ => throw new ValidationException("field", "That task field can't be changed here."),
        };
        await tickets.UpdateTaskAsync(id, patch, ct);
        Response.HxToast("Task updated.");
        return await TasksPartialAsync(number, ct);
    }

    public async Task<IActionResult> OnPostDeleteTaskAsync(int number, Guid id, CancellationToken ct)
    {
        var ticket = await FindAsync(number, ct);
        await TaskOfTicketAsync(ticket.Id, id, ct);
        await tickets.DeleteTaskAsync(id, ct);
        Response.HxToast("Task deleted.");
        return await TasksPartialAsync(number, ct);
    }

    private async Task TaskOfTicketAsync(Guid ticketId, Guid taskId, CancellationToken ct)
    {
        if (!await db.TicketTasks.AnyAsync(t => t.Id == taskId && t.TicketId == ticketId, ct))
        {
            throw new NotFoundException();
        }
    }

    private async Task<IActionResult> TasksPartialAsync(int number, CancellationToken ct)
    {
        Data = await loader.LoadAsync(number, ct);
        return Partial("_Tasks", this);
    }

    // ---------------------------------------------------------------- header actions (forms)

    public async Task<IActionResult> OnPostAssignToMeAsync(int number, CancellationToken ct) =>
        await ActAsync(number, async t => await tickets.UpdateAsync(t.Id, new TicketPatch { AgentId = actors.Current.UserId }, ct: ct), "Assigned to you.");

    public async Task<IActionResult> OnPostMajorAsync(int number, [FromForm] bool promote, [FromForm] string? reason, CancellationToken ct) =>
        await ActAsync(number, async t =>
        {
            await permissions.DemandAsync(ServiceDeskModule.MajorIncidentPrivilege, t.WorkspaceId, ct);
            if (t.Type != TicketType.Incident)
            {
                throw new ValidationException("type", "Only incidents can be major incidents.");
            }
            var why = (reason ?? "").Trim();
            if (why.Length == 0)
            {
                throw new ValidationException("reason", promote ? "Say why this is a major incident." : "Say why this is no longer a major incident.");
            }
            if (why.Length > 2000)
            {
                throw new ValidationException("reason", "Keep the reason under 2,000 characters.");
            }
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await tickets.UpdateAsync(t.Id, new TicketPatch { IsMajorIncident = promote }, ct: ct);
            var html = HttpContext.RequestServices.GetRequiredService<IHtmlContent>();
            await tickets.AddNoteAsync(t.Id, new NoteCommand
            {
                BodyHtml = html.FromPlainText((promote ? "Promoted to a major incident. Reason: " : "Demoted from a major incident. Reason: ") + why),
                IsPrivate = true,
            }, ct);
            audit.Record(promote ? "tickets.major_promoted" : "tickets.major_demoted", "ticket", t.Id.ToString(), new { reason = why });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }, promote ? "Promoted to a major incident." : "No longer a major incident.");

    public async Task<IActionResult> OnPostSpamAsync(int number, [FromForm] bool spam, CancellationToken ct)
    {
        var ticket = await FindAsync(number, ct);
        try
        {
            await tickets.SetSpamAsync(ticket.Id, spam, ct);
            TempData.Toast(spam ? $"{ticket.DisplayId} marked as spam." : $"{ticket.DisplayId} is back in the queue.");
            return spam ? Redirect("/tickets") : Redirect($"/tickets/{number}");
        }
        catch (Exception ex) when (ex is ValidationException or ForbiddenException)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
            return Redirect($"/tickets/{number}");
        }
    }

    public async Task<IActionResult> OnPostTrashAsync(int number, CancellationToken ct)
    {
        var ticket = await FindAsync(number, ct);
        try
        {
            await tickets.MoveToTrashAsync(ticket.Id, ct);
            TempData.Toast($"{ticket.DisplayId} moved to trash.");
            return Redirect("/tickets");
        }
        catch (Exception ex) when (ex is ValidationException or ForbiddenException)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
            return Redirect($"/tickets/{number}");
        }
    }

    public async Task<IActionResult> OnPostRestoreAsync(int number, CancellationToken ct) =>
        await ActAsync(number, async t => await tickets.RestoreAsync(t.Id, ct), "Restored from trash.");

    public async Task<IActionResult> OnPostPurgeAsync(int number, CancellationToken ct)
    {
        var ticket = await FindAsync(number, ct);
        try
        {
            await tickets.PurgeAsync(ticket.Id, ct);
            TempData.Toast($"{ticket.DisplayId} was permanently deleted.");
            return Redirect("/tickets?view=trash");
        }
        catch (Exception ex) when (ex is ValidationException or ForbiddenException)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
            return Redirect($"/tickets/{number}");
        }
    }

    public async Task<IActionResult> OnPostMergeAsync(int number, [FromForm] List<Guid> others, [FromForm] Guid? primaryId, CancellationToken ct)
    {
        var ticket = await FindAsync(number, ct);
        try
        {
            var all = others.Append(ticket.Id).Distinct().ToList();
            var primary = primaryId is { } p && all.Contains(p) ? p : ticket.Id;
            await tickets.MergeAsync(primary, all.Where(id => id != primary).ToList(), ct);
            TempData.Toast("Tickets merged.");
            var primaryNumber = await db.Tickets.AsNoTracking().Where(t => t.Id == primary).Select(t => t.Number).FirstAsync(ct);
            return Redirect($"/tickets/{primaryNumber}");
        }
        catch (Exception ex) when (ex is ValidationException or ForbiddenException or NotFoundException)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
            return Redirect($"/tickets/{number}");
        }
    }

    public async Task<IActionResult> OnPostLinkAsync(int number, [FromForm] Guid? targetId, CancellationToken ct) =>
        await ActAsync(number, async t =>
        {
            if (targetId is not { } target)
            {
                throw new ValidationException("targetId", "Choose a ticket to link.");
            }
            await tickets.LinkAsync(t.Id, Ticket.Family, target, TicketLinkTypes.Related, ct);
        }, "Tickets linked.", "related");

    public async Task<IActionResult> OnPostUnlinkAsync(int number, [FromForm] Guid linkId, CancellationToken ct) =>
        await ActAsync(number, async t =>
        {
            var link = await db.TicketLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkId, ct) ?? throw new NotFoundException();
            if (link.TicketId != t.Id && link.TargetId != t.Id)
            {
                throw new NotFoundException();
            }
            await tickets.UnlinkAsync(linkId, ct);
        }, "Link removed.", "related");

    public async Task<IActionResult> OnGetCandidatesAsync(int number, string? q, string mode, CancellationToken ct)
    {
        var ticket = await FindAsync(number, ct);
        var found = await loader.FindCandidatesAsync(ticket, q, ct);
        return Partial("_Candidates", new CandidatesModel(mode == "link" ? "link" : "merge", found, q));
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Ticket> FindAsync(int number, CancellationToken ct) =>
        await loader.FindAsync(number, ct) ?? throw new NotFoundException();

    private async Task<IActionResult> ActAsync(int number, Func<Ticket, Task> action, string success, string? tab = null)
    {
        var ticket = await FindAsync(number, HttpContext.RequestAborted);
        var back = $"/tickets/{number}" + (tab is null ? "" : $"?tab={tab}");
        try
        {
            await action(ticket);
            TempData.Toast(success);
        }
        catch (Exception ex) when (ex is ValidationException or ForbiddenException or ConflictException)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return Redirect(back);
    }

    private ObjectResult Conflict(string message, string kind) =>
        new(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Conflict",
            Detail = message,
            Extensions = { ["kind"] = kind },
        })
        {
            StatusCode = StatusCodes.Status409Conflict,
            ContentTypes = { "application/problem+json" },
        };

    private static List<string>? Split(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

public sealed record MessageModel(TicketPageData Data, ThreadEntry Entry);

public sealed record EditNoteModel(int Number, TicketConversation Note);

public sealed record CandidatesModel(string Mode, IReadOnlyList<TicketSummary> Tickets, string? Query);

public sealed record CustomFieldPropModel(CustomFieldDefinition Definition, System.Text.Json.Nodes.JsonNode? Value, bool Editable, string Url);
