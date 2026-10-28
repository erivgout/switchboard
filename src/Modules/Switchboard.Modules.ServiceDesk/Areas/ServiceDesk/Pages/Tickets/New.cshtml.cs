using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.ServiceDesk.Data;
using Switchboard.Modules.ServiceDesk.Services;
using Switchboard.UI;

namespace Switchboard.Modules.ServiceDesk.Areas.ServiceDesk.Pages.Tickets;

[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class NewModel(
    ITicketService tickets,
    TicketFormOptions formOptions,
    TicketTemplateService templates,
    TicketPageLoader loader,
    IPermissionService permissions,
    IActorAccessor actors,
    IHtmlContent html,
    SwitchboardDbContext db) : PageModel
{
    public sealed class TicketInput
    {
        public Guid WorkspaceId { get; set; }
        public TicketType Type { get; set; } = TicketType.Incident;
        public Guid? TemplateId { get; set; }
        public Guid? RequesterId { get; set; }
        public string? RequesterEmail { get; set; }
        public string? RequesterName { get; set; }
        /// <summary>Shown in the picker after a failed submit.</summary>
        public string? RequesterLabel { get; set; }
        public string Subject { get; set; } = "";
        public string? DescriptionHtml { get; set; }
        public Guid? CategoryId { get; set; }
        public Guid? SubCategoryId { get; set; }
        public Guid? ItemCategoryId { get; set; }
        public Level Urgency { get; set; } = Level.Low;
        public Level Impact { get; set; } = Level.Low;
        /// <summary>Null: derive from impact and urgency.</summary>
        public Priority? Priority { get; set; }
        public Guid? StatusId { get; set; }
        public string SourceKey { get; set; } = TicketSourceKeys.Agent;
        public Guid? GroupId { get; set; }
        public Guid? AgentId { get; set; }
        public string? Tags { get; set; }
        public string? Cc { get; set; }
        public List<Guid> AttachmentIds { get; set; } = [];
        public string? Parent { get; set; }
    }

    [BindProperty]
    public TicketInput Input { get; set; } = new();

    public WorkspaceOptions Options { get; private set; } = default!;
    public IReadOnlyList<NamedOption> Workspaces { get; private set; } = [];
    public IReadOnlyList<SdTicketTemplate> Templates { get; private set; } = [];
    public SdTicketTemplate? AppliedTemplate { get; private set; }
    public TicketSummary? ParentTicket { get; private set; }
    public bool CanAssign { get; private set; }
    public Dictionary<string, IReadOnlyList<string>> CustomValues { get; private set; } = [];
    public IReadOnlyList<(string Name, string Id)> PendingFiles { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string? type, Guid? template, int? parent, Guid? ws, CancellationToken ct)
    {
        Input.Type = type == "request" ? TicketType.ServiceRequest : TicketType.Incident;
        var workspaces = await formOptions.CreatableWorkspacesAsync(ct);
        if (workspaces.Count == 0)
        {
            throw new ForbiddenException("Your role can't create tickets.");
        }
        Input.WorkspaceId = ws is { } w && workspaces.Any(x => x.Id == w) ? w : workspaces[0].Id;

        if (parent is { } parentNumber && await loader.FindAsync(parentNumber, ct) is { } p)
        {
            Input.WorkspaceId = p.WorkspaceId;
            Input.Parent = p.DisplayId;
            Input.RequesterId = p.RequesterId;
            Input.RequesterLabel = p.Requester?.DisplayName;
            Input.CategoryId = p.CategoryId;
            Input.SubCategoryId = p.SubCategoryId;
            Input.ItemCategoryId = p.ItemCategoryId;
            Input.GroupId = p.GroupId;
        }

        if (template is { } templateId && await templates.FindAvailableAsync(templateId, ct) is { } tpl)
        {
            ApplyTemplate(tpl);
        }

        await LoadAsync(ct);
        return Page();
    }

    private void ApplyTemplate(SdTicketTemplate tpl)
    {
        AppliedTemplate = tpl;
        Input.TemplateId = tpl.Id;
        Input.WorkspaceId = tpl.WorkspaceId;
        Input.Type = tpl.Type;
        Input.Subject = tpl.Subject;
        Input.DescriptionHtml = tpl.DescriptionHtml;
        Input.CategoryId = tpl.CategoryId ?? Input.CategoryId;
        Input.SubCategoryId = tpl.CategoryId is null ? Input.SubCategoryId : tpl.SubCategoryId;
        Input.ItemCategoryId = tpl.CategoryId is null ? Input.ItemCategoryId : tpl.ItemCategoryId;
        Input.Priority = tpl.Priority;
        Input.Urgency = tpl.Urgency ?? Input.Urgency;
        Input.Impact = tpl.Impact ?? Input.Impact;
        Input.GroupId = tpl.GroupId ?? Input.GroupId;
        Input.Tags = string.Join(", ", tpl.Tags);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var options = await formOptions.LoadAsync(Input.WorkspaceId, ct);
        if (Input.RequesterId is null && string.IsNullOrWhiteSpace(Input.RequesterEmail))
        {
            ModelState.AddModelError("Input.RequesterId", "Choose the requester, or enter their email address.");
        }

        Guid? parentId = null;
        if (!string.IsNullOrWhiteSpace(Input.Parent))
        {
            var text = Input.Parent.Trim();
            if ((TicketReference.TryParse(text, out var n) || int.TryParse(text, out n)) && await loader.FindAsync(n, ct) is { } parent)
            {
                parentId = parent.Id;
            }
            else
            {
                ModelState.AddModelError("Input.Parent", "No ticket you can see has that number.");
            }
        }

        var custom = new Dictionary<string, JsonElement?>();
        foreach (var def in options.CustomFields.Where(CustomFieldForm.IsEditable))
        {
            var posted = Request.Form[CustomFieldForm.InputPrefix + def.Key];
            var value = CustomFieldForm.FromForm(def, posted.ToArray());
            if (value is not null && !(def.FieldType == CustomFieldType.Checkbox && value.Value.ValueKind == JsonValueKind.False))
            {
                custom[def.Key] = value;
            }
        }

        if (ModelState.ErrorCount == 0)
        {
            try
            {
                var assign = await permissions.HasAsync(Privileges.TicketsAssign, Input.WorkspaceId, ct);
                var ticket = await tickets.CreateAsync(new CreateTicketCommand
                {
                    WorkspaceId = Input.WorkspaceId,
                    Type = Input.Type,
                    Subject = Input.Subject,
                    DescriptionHtml = Input.DescriptionHtml,
                    RequesterId = Input.RequesterId,
                    RequesterEmail = Input.RequesterId is null ? Input.RequesterEmail?.Trim() : null,
                    RequesterName = Input.RequesterId is null ? Input.RequesterName?.Trim() : null,
                    CcEmails = Split(Input.Cc),
                    StatusId = Input.StatusId,
                    Priority = Input.Priority,
                    Urgency = Input.Urgency,
                    Impact = Input.Impact,
                    SourceKey = Input.SourceKey,
                    CategoryId = Input.CategoryId,
                    SubCategoryId = Input.CategoryId is null ? null : Input.SubCategoryId,
                    ItemCategoryId = Input.SubCategoryId is null ? null : Input.ItemCategoryId,
                    GroupId = assign ? Input.GroupId : null,
                    AgentId = assign ? Input.AgentId : null,
                    Tags = Split(Input.Tags),
                    CustomFields = custom,
                    AttachmentIds = Input.AttachmentIds,
                    ParentId = parentId,
                }, ct);
                TempData.Toast($"{ticket.DisplayId} created.");
                return Redirect($"/tickets/{ticket.Number}");
            }
            catch (ValidationException ex)
            {
                foreach (var (key, messages) in ex.Errors)
                {
                    foreach (var message in messages)
                    {
                        ModelState.AddModelError(FieldKey(key), message);
                    }
                }
            }
        }

        // Re-render only sanitized HTML, never what was posted.
        Input.DescriptionHtml = html.Sanitize(Input.DescriptionHtml);
        foreach (var def in options.CustomFields)
        {
            CustomValues[def.Key] = Request.Form[CustomFieldForm.InputPrefix + def.Key].Select(v => v ?? "").ToList();
        }
        if (Input.TemplateId is { } templateId)
        {
            AppliedTemplate = await templates.FindAvailableAsync(templateId, ct);
        }
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>Requester typeahead: people matching the text, plus "new requester" for an unknown email.</summary>
    public async Task<IActionResult> OnGetPeopleAsync(string? q, CancellationToken ct)
    {
        var people = await formOptions.SearchPeopleAsync(q, 8, ct);
        return Partial("_PeopleOptions", new PeopleOptionsModel(q?.Trim() ?? "", people));
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Workspaces = await formOptions.CreatableWorkspacesAsync(ct);
        if (!Workspaces.Any(w => w.Id == Input.WorkspaceId))
        {
            throw new ForbiddenException("Your role can't create tickets in that workspace.");
        }
        Options = await formOptions.LoadAsync(Input.WorkspaceId, ct);
        Templates = await templates.AvailableAsync(Input.WorkspaceId, null, ct);
        CanAssign = await permissions.HasAsync(Privileges.TicketsAssign, Input.WorkspaceId, ct);

        if (!string.IsNullOrWhiteSpace(Input.Parent))
        {
            var text = Input.Parent.Trim();
            if ((TicketReference.TryParse(text, out var n) || int.TryParse(text, out n)) && await loader.FindAsync(n, ct) is { } parent)
            {
                ParentTicket = new TicketSummary(parent.Id, parent.Number, parent.Type, parent.Subject, parent.Status!.Name, parent.Status.Category, parent.Agent?.DisplayName, parent.CreatedAt);
            }
        }

        if (Input.RequesterId is { } rid && string.IsNullOrEmpty(Input.RequesterLabel))
        {
            Input.RequesterLabel = await db.Users.AsNoTracking().Where(u => u.Id == rid).Select(u => u.DisplayName + (u.Email != null ? " <" + u.Email + ">" : "")).FirstOrDefaultAsync(ct);
        }
        if (Input.AttachmentIds.Count > 0)
        {
            var ids = Input.AttachmentIds;
            var me = actors.Current.UserId;
            PendingFiles = await db.Attachments.AsNoTracking()
                .Where(a => ids.Contains(a.Id) && a.OwnerType == AttachmentOwners.Pending && a.UploadedById == me)
                .Select(a => new ValueTuple<string, string>(a.FileName, a.Id.ToString()))
                .ToListAsync(ct);
        }
    }

    private static string FieldKey(string key) => key switch
    {
        "subject" => "Input.Subject",
        "requester" => "Input.RequesterId",
        "category" => "Input.CategoryId",
        "group" => "Input.GroupId",
        "agent" => "Input.AgentId",
        "status" => "Input.StatusId",
        "cc" => "Input.Cc",
        "parent" => "Input.Parent",
        "body" => "Input.DescriptionHtml",
        _ when key.StartsWith("custom.", StringComparison.Ordinal) => CustomFieldForm.InputPrefix + key["custom.".Length..],
        _ => "",
    };

    private static List<string>? Split(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

public sealed record PeopleOptionsModel(string Query, IReadOnlyList<PersonOption> People)
{
    public bool OfferNew => People.Count == 0 && Switchboard.Core.Services.IUserDirectory.IsValidEmail(Query)
        || People.Count > 0 && Switchboard.Core.Services.IUserDirectory.IsValidEmail(Query) && !People.Any(p => string.Equals(p.Email, Query, StringComparison.OrdinalIgnoreCase));
}

public sealed record CustomFieldInputModel(CustomFieldDefinition Definition, IReadOnlyList<string> Values, string? Error);
