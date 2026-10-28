using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;

namespace Switchboard.Modules.Portal.Services;

/// <summary>
/// The requester side of tickets. Reads go through requester visibility (own, requested-for,
/// shared, led department) and return only public content; writes go through <see cref="ITicketService"/>.
/// </summary>
public sealed class PortalRequests(
    SwitchboardDbContext db,
    IActorAccessor actors,
    IPermissionService permissions,
    ITicketSearch search,
    ITicketService tickets,
    ICustomFieldService customFields,
    ISettingsService settings,
    IHtmlContent html,
    IUserDirectory directory)
{
    /// <summary>Conversation kinds a requester may ever see. Anything new stays hidden until listed here.</summary>
    private static readonly ConversationKind[] PublicKinds =
        [ConversationKind.RequesterMessage, ConversationKind.AgentReply, ConversationKind.Note, ConversationKind.System];

    public const string SupportName = "Service desk";

    // ---------------------------------------------------------------- lists

    public async Task<PortalRequestPage> ListAsync(RequestTab tab, RequestState state, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var me = RequireUser();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var query = await TabQueryAsync(tab, state, me, ct);
        var total = await query.CountAsync(ct);
        var items = await Project(query, me)
            .OrderByDescending(r => r.UpdatedAt).ThenByDescending(r => r.Number)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        return new PortalRequestPage(items, total, page, pageSize);
    }

    public async Task<PortalTabCounts> CountsAsync(RequestState state, CancellationToken ct = default)
    {
        var me = RequireUser();
        var isLead = (await LeadDepartmentsAsync(ct)).Count > 0;
        var mine = await (await TabQueryAsync(RequestTab.Mine, state, me, ct)).CountAsync(ct);
        var shared = await (await TabQueryAsync(RequestTab.Shared, state, me, ct)).CountAsync(ct);
        var department = isLead ? await (await TabQueryAsync(RequestTab.Department, state, me, ct)).CountAsync(ct) : 0;
        return new PortalTabCounts(mine, shared, department, isLead);
    }

    /// <summary>How many of the viewer's own requests wait on them, and the number when there's exactly one.</summary>
    public async Task<(int Count, int? OnlyNumber)> WaitingOnMeAsync(CancellationToken ct = default)
    {
        RequireUser();
        var numbers = await (await search.QueryAsync("requester:me state:pending", asRequester: true, ct))
            .OrderByDescending(t => t.UpdatedAt).Select(t => t.Number).Take(2).ToListAsync(ct);
        var count = numbers.Count < 2 ? numbers.Count : await (await search.QueryAsync("requester:me state:pending", asRequester: true, ct)).CountAsync(ct);
        return (count, count == 1 ? numbers[0] : null);
    }

    /// <summary>The viewer's own pending uploads, to show again when a form is redisplayed.</summary>
    public async Task<IReadOnlyList<PortalFile>> PendingUploadsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        var me = RequireUser();
        if (ids.Count == 0)
        {
            return [];
        }
        return await db.Attachments.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.OwnerType == AttachmentOwners.Pending && a.UploadedById == me)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new PortalFile(a.Id, a.FileName, a.Size))
            .ToListAsync(ct);
    }

    public async Task<bool> IsDepartmentLeadAsync(CancellationToken ct = default) => (await LeadDepartmentsAsync(ct)).Count > 0;

    /// <summary>Text search across every request the viewer can see in the portal.</summary>
    public async Task<IReadOnlyList<PortalRequestRow>> SearchAsync(string? text, int max = 20, CancellationToken ct = default)
    {
        var me = RequireUser();
        var query = PortalQuery.FromUserText(text);
        if (query is null)
        {
            return [];
        }
        var visible = await search.QueryAsync(query, asRequester: true, ct);
        return await Project(visible, me)
            .OrderByDescending(r => r.UpdatedAt).ThenByDescending(r => r.Number)
            .Take(Math.Clamp(max, 1, 50))
            .ToListAsync(ct);
    }

    private async Task<IQueryable<Ticket>> TabQueryAsync(RequestTab tab, RequestState state, Guid me, CancellationToken ct)
    {
        var stateQuery = PortalQuery.ForState(state);
        switch (tab)
        {
            case RequestTab.Shared:
                return (await search.QueryAsync(stateQuery, asRequester: true, ct))
                    .Where(t => t.RequesterId != me && t.RequestedForId != me && t.Shares.Any(s => s.UserId == me));
            case RequestTab.Department:
                var departments = (await LeadDepartmentsAsync(ct)).ToList();
                return (await search.QueryAsync(stateQuery, asRequester: true, ct))
                    .Where(t => t.DepartmentId != null && departments.Contains(t.DepartmentId.Value));
            default:
                return await search.QueryAsync(PortalQuery.Join("requester:me", stateQuery), asRequester: true, ct);
        }
    }

    private static IQueryable<PortalRequestRow> Project(IQueryable<Ticket> query, Guid me) =>
        query.Select(t => new PortalRequestRow
        {
            Id = t.Id,
            Number = t.Number,
            Type = t.Type,
            Subject = t.Subject,
            StatusName = t.Status!.Name,
            StatusRequesterLabel = t.Status.RequesterLabel,
            StatusCategory = t.Status.Category,
            CreatedAt = t.CreatedAt,
            UpdatedAt = t.UpdatedAt,
            RequesterName = t.Requester!.DisplayName,
            IsMine = t.RequesterId == me || t.RequestedForId == me,
        });

    // ---------------------------------------------------------------- one request

    /// <summary>Loads a request the viewer can see, or throws <see cref="NotFoundException"/>.</summary>
    public async Task<PortalRequestView> GetAsync(int number, CancellationToken ct = default)
    {
        var me = RequireUser();
        var visible = await permissions.RequesterVisibleTicketsAsync(ct);
        var t = await visible.AsNoTracking()
            .Where(x => x.Number == number)
            .Select(x => new
            {
                x.Id,
                x.Number,
                x.Type,
                x.WorkspaceId,
                x.Subject,
                x.DescriptionHtml,
                StatusName = x.Status!.Name,
                StatusRequesterLabel = x.Status.RequesterLabel,
                StatusCategory = x.Status.Category,
                x.CreatedAt,
                x.UpdatedAt,
                x.ResolvedAt,
                x.RequesterId,
                x.RequestedForId,
                RequesterName = x.Requester!.DisplayName,
                RequestedForName = x.RequestedFor != null ? x.RequestedFor.DisplayName : null,
                AgentName = x.Agent != null ? x.Agent.DisplayName : null,
                IsShared = x.Shares.Any(s => s.UserId == me),
                x.CcEmails,
                x.CategoryId,
                x.SubCategoryId,
                x.ItemCategoryId,
                x.CustomFieldsJson,
                x.MergedIntoId,
            })
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException();

        var relation = t.RequesterId == me ? ViewerRelation.Requester
            : t.RequestedForId == me ? ViewerRelation.RequestedFor
            : t.IsShared ? ViewerRelation.Shared
            : ViewerRelation.Department;
        var own = relation is ViewerRelation.Requester or ViewerRelation.RequestedFor;
        var showAgentNames = await settings.GetAsync(SettingKeys.ShowAgentNamesToRequesters, true, t.WorkspaceId, ct);

        var conversations = await db.TicketConversations.AsNoTracking()
            .Where(c => c.TicketId == t.Id && !c.IsPrivate && c.DeletedAt == null && PublicKinds.Contains(c.Kind))
            .OrderBy(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.Kind,
                c.AuthorId,
                AuthorName = c.Author != null ? c.Author.DisplayName : null,
                c.FromEmail,
                c.BodyHtml,
                c.CreatedAt,
            })
            .ToListAsync(ct);

        var conversationIds = conversations.Select(c => c.Id).ToList();
        var files = await db.Attachments.AsNoTracking()
            .Where(a => !a.IsInline
                && ((a.OwnerType == AttachmentOwners.Ticket && a.OwnerId == t.Id)
                    || (a.OwnerType == AttachmentOwners.Conversation && a.OwnerId != null && conversationIds.Contains(a.OwnerId.Value))))
            .OrderBy(a => a.CreatedAt)
            .Select(a => new { a.Id, a.OwnerType, a.OwnerId, a.FileName, a.Size })
            .ToListAsync(ct);
        IReadOnlyList<PortalFile> FilesOf(string ownerType, Guid ownerId) =>
            files.Where(f => f.OwnerType == ownerType && f.OwnerId == ownerId).Select(f => new PortalFile(f.Id, f.FileName, f.Size)).ToList();

        var messages = conversations.Select(c =>
        {
            var kind = c.Kind switch
            {
                ConversationKind.RequesterMessage => PortalMessageKind.Requester,
                ConversationKind.System => PortalMessageKind.System,
                _ => PortalMessageKind.Support,
            };
            var isYou = kind == PortalMessageKind.Requester && c.AuthorId == me;
            var name = kind switch
            {
                PortalMessageKind.Requester => isYou ? "You" : c.AuthorName ?? c.FromEmail ?? "Someone",
                PortalMessageKind.Support => showAgentNames && c.AuthorName is not null ? c.AuthorName : SupportName,
                _ => "",
            };
            var avatar = kind switch
            {
                PortalMessageKind.Requester => c.AuthorName ?? c.FromEmail ?? "?",
                PortalMessageKind.Support when showAgentNames => c.AuthorName,
                _ => null,
            };
            return new PortalMessage(c.Id, kind, name, isYou, avatar, c.BodyHtml, c.CreatedAt, FilesOf(AttachmentOwners.Conversation, c.Id));
        }).ToList();

        var category = await CategoryPathAsync(t.CategoryId, t.SubCategoryId, t.ItemCategoryId, ct);
        var fields = await RequesterFieldValuesAsync(t.WorkspaceId, t.CustomFieldsJson, ct);

        IReadOnlyList<PortalPerson> sharedWith = [];
        if (own)
        {
            sharedWith = await db.TicketShares.AsNoTracking()
                .Where(s => s.TicketId == t.Id)
                .Join(db.Users, s => s.UserId, u => u.Id, (s, u) => new { u.Id, u.DisplayName, u.Email, s.CreatedAt })
                .OrderBy(x => x.CreatedAt)
                .Select(x => new PortalPerson(x.Id, x.DisplayName, x.Email ?? ""))
                .ToListAsync(ct);
        }

        int? mergedNumber = null;
        string? mergedDisplayId = null;
        if (t.MergedIntoId is { } mergedId)
        {
            var primary = await visible.AsNoTracking().Where(x => x.Id == mergedId).Select(x => new { x.Number, x.Type }).FirstOrDefaultAsync(ct);
            if (primary is not null)
            {
                mergedNumber = primary.Number;
                mergedDisplayId = Ticket.FormatDisplayId(primary.Type, primary.Number);
            }
        }

        var policy = await PolicyAsync(t.WorkspaceId, t.StatusCategory, own, t.MergedIntoId is not null, ct);

        return new PortalRequestView
        {
            Id = t.Id,
            Number = t.Number,
            Type = t.Type,
            Subject = t.Subject,
            DescriptionHtml = t.DescriptionHtml,
            StatusLabel = PortalText.StatusLabel(t.StatusName, t.StatusRequesterLabel, t.StatusCategory, own ? null : t.RequesterName),
            StatusCategory = t.StatusCategory,
            CreatedAt = t.CreatedAt,
            UpdatedAt = t.UpdatedAt,
            ResolvedAt = t.ResolvedAt,
            RequesterName = t.RequesterName,
            RequestedForName = t.RequestedForName,
            Relation = relation,
            Category = category,
            HandledBy = showAgentNames ? t.AgentName : null,
            CcEmails = own ? t.CcEmails : [],
            Fields = fields,
            Files = FilesOf(AttachmentOwners.Ticket, t.Id),
            Messages = messages,
            SharedWith = sharedWith,
            CanClose = policy.CanClose,
            CanShare = own && t.MergedIntoId is null,
            CanReply = policy.CanReply,
            ReplyReopens = policy.ReplyReopens,
            ReplyCreatesFollowUp = policy.ReplyCreatesFollowUp,
            ClosingIsFinal = policy.ClosingIsFinal,
            IsMerged = t.MergedIntoId is not null,
            MergedIntoNumber = mergedNumber,
            MergedIntoDisplayId = mergedDisplayId,
        };
    }

    private sealed record Policy(bool CanClose, bool CanReply, bool ReplyReopens, bool ReplyCreatesFollowUp, bool ClosingIsFinal);

    /// <summary>
    /// Close and reopen rules (FR-TKT-26). Only the requester and the person it was requested for may
    /// close or reopen. A requester reply to a resolved or closed request reopens it in Core, so replying
    /// there is offered only when reopening is allowed, or when the reply opens a follow-up instead.
    /// </summary>
    private async Task<Policy> PolicyAsync(Guid workspaceId, StatusCategory category, bool own, bool merged, CancellationToken ct)
    {
        if (merged)
        {
            return new Policy(false, false, false, false, true);
        }
        var canClose = own && category != StatusCategory.Closed
            && await settings.GetAsync(SettingKeys.RequesterCanClose, true, workspaceId, ct);
        var canReopen = own && await settings.GetAsync(SettingKeys.RequesterCanReopen, true, workspaceId, ct);
        var followUp = await settings.GetAsync(SettingKeys.ClosedReplyBehavior, "reopen", workspaceId, ct) == "followup";
        var closingIsFinal = !canReopen && !followUp;
        return category switch
        {
            StatusCategory.Resolved => new Policy(canClose, canReopen, canReopen, false, closingIsFinal),
            StatusCategory.Closed when followUp => new Policy(false, own, false, own, closingIsFinal),
            StatusCategory.Closed => new Policy(false, canReopen, canReopen, false, closingIsFinal),
            _ => new Policy(canClose, true, false, false, closingIsFinal),
        };
    }

    // ---------------------------------------------------------------- writes

    /// <summary>Files a new request as the current user (portal source, requester field rules).</summary>
    public async Task<Ticket> CreateAsync(NewPortalRequest request, CancellationToken ct = default)
    {
        RequireUser();
        var workspace = await PrimaryWorkspaceAsync(ct);

        // Core stops at the first problem; check the form as a whole first so people fix everything
        // in one go. Core still validates everything again.
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string key, IEnumerable<string> messages)
        {
            if (!errors.TryGetValue(key, out var list))
            {
                errors[key] = list = [];
            }
            list.AddRange(messages);
        }
        void Collect(ValidationException ex)
        {
            foreach (var (key, messages) in ex.Errors)
            {
                Add(key, messages);
            }
        }

        if (string.IsNullOrWhiteSpace(request.Subject))
        {
            Add("subject", ["Add a subject."]);
        }
        (Guid? categoryId, Guid? subCategoryId) = (null, null);
        try
        {
            (categoryId, subCategoryId) = ParseCategory(request.Category);
        }
        catch (ValidationException ex)
        {
            Collect(ex);
        }
        var defs = await RequesterFieldsAsync(ct);
        var custom = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        try
        {
            custom = ToCustomValues(defs, request.CustomFields);
            if (custom.Count > 0)
            {
                await customFields.ApplyAsync(Ticket.Family, workspace, "{}", custom, FieldAudience.Requester, ct);
            }
        }
        catch (ValidationException ex)
        {
            Collect(ex);
        }
        foreach (var def in defs.Where(d => d.Required && !custom.ContainsKey(d.Key) && !errors.ContainsKey($"custom.{d.Key}")))
        {
            Add($"custom.{def.Key}", [$"Answer “{def.Label}”."]);
        }
        foreach (var cc in request.CcEmails.Where(c => !IUserDirectory.IsValidEmail(c)))
        {
            Add("cc", [$"{cc} isn't a valid email address."]);
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors.ToDictionary(e => e.Key, e => e.Value.Distinct().ToArray()));
        }

        return await tickets.CreateAsync(new CreateTicketCommand
        {
            WorkspaceId = workspace,
            Type = request.Type,
            Subject = request.Subject,
            DescriptionHtml = string.IsNullOrWhiteSpace(request.Description) ? null : html.FromPlainText(request.Description.Trim()),
            CcEmails = request.CcEmails,
            CategoryId = categoryId,
            SubCategoryId = subCategoryId,
            CustomFields = custom.Count > 0 ? custom : null,
            AttachmentIds = request.AttachmentIds,
            AsRequester = true,
            SourceKey = TicketSourceKeys.Portal,
        }, ct);
    }

    /// <summary>Adds the viewer's message. On a resolved or closed request this reopens it or opens a follow-up.</summary>
    public async Task<RequesterMessageResult> ReplyAsync(int number, string? body, IReadOnlyList<Guid>? attachmentIds, CancellationToken ct = default)
    {
        var view = await GetAsync(number, ct);
        if (view.IsMerged)
        {
            throw new ValidationException("body", view.MergedIntoDisplayId is null
                ? "This request was merged into another one, so it can't take new messages."
                : $"This request was merged into {view.MergedIntoDisplayId}. Reply there instead.");
        }
        if (!view.CanReply)
        {
            throw new ForbiddenException(view.StatusCategory is StatusCategory.Resolved or StatusCategory.Closed
                ? "This request can't be reopened from the portal. Report a new problem if you still need help."
                : "You can't reply to this request.");
        }
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ValidationException("body", "Write a message first.");
        }
        return await tickets.AddRequesterMessageAsync(view.Id, new RequesterMessageCommand
        {
            BodyHtml = html.FromPlainText(body.Trim()),
            AttachmentIds = attachmentIds,
        }, ct);
    }

    /// <summary>Closes the viewer's own request when <see cref="SettingKeys.RequesterCanClose"/> allows it.</summary>
    public async Task CloseAsync(int number, CancellationToken ct = default)
    {
        var view = await GetAsync(number, ct);
        if (view.StatusCategory == StatusCategory.Closed)
        {
            return;
        }
        if (!view.CanClose)
        {
            throw new ForbiddenException(view.IsOwn
                ? "Requests can't be closed from the portal. Reply to ask the service desk to close it."
                : "Only the person who raised this request can close it.");
        }

        var workspaceId = await db.Tickets.AsNoTracking().Where(x => x.Id == view.Id).Select(x => x.WorkspaceId).FirstAsync(ct);
        var closed = await db.TicketStatuses.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.Category == StatusCategory.Closed)
            .OrderBy(s => s.Order).Select(s => s.Id).FirstAsync(ct);

        // Core has no requester close yet: requesters lack tickets.edit. Visibility, ownership and the
        // setting are checked above; the status change itself runs with the requester's identity but
        // internal trust, the same pattern Core uses for merges. Activity still names the requester.
        using (actors.BeginScope(actors.Current with { Kind = ActorKind.System }))
        {
            await tickets.UpdateAsync(view.Id, new TicketPatch { StatusId = closed }, ct: ct);
        }
    }

    /// <summary>Shares the viewer's own request with a colleague by email (FR-TKT-14). Returns their name.</summary>
    public async Task<string> ShareAsync(int number, string? email, CancellationToken ct = default)
    {
        var me = RequireUser();
        var view = await GetAsync(number, ct);
        if (!view.CanShare)
        {
            throw new ForbiddenException("Only the person who raised this request can share it.");
        }
        var address = (email ?? "").Trim();
        if (!IUserDirectory.IsValidEmail(address))
        {
            throw new ValidationException("share", "Enter your colleague's email address.");
        }
        var user = await directory.FindByEmailAsync(address, ct);
        if (user is null || !user.IsActive || user.IsForgotten)
        {
            throw new ValidationException("share", $"No one with the address {address} can be added. Check the spelling.");
        }
        if (user.Id == me)
        {
            throw new ValidationException("share", "You can already see this request.");
        }
        await tickets.ShareAsync(view.Id, user.Id, ct);
        return user.DisplayName;
    }

    public async Task UnshareAsync(int number, Guid userId, CancellationToken ct = default)
    {
        var view = await GetAsync(number, ct);
        if (!view.IsOwn)
        {
            throw new ForbiddenException("Only the person who raised this request can change who it's shared with.");
        }
        await tickets.UnshareAsync(view.Id, userId, ct);
    }

    // ---------------------------------------------------------------- form data

    public async Task<IReadOnlyList<PortalCategory>> CategoriesAsync(CancellationToken ct = default)
    {
        var workspace = await PrimaryWorkspaceAsync(ct);
        var all = await db.TicketCategories.AsNoTracking()
            .Where(c => c.WorkspaceId == workspace && c.Level <= 2)
            .OrderBy(c => c.Order).ThenBy(c => c.Name)
            .Select(c => new { c.Id, c.ParentId, c.Name, c.Level })
            .ToListAsync(ct);
        return all.Where(c => c.Level == 1)
            .Select(c => new PortalCategory(c.Id, c.Name, all.Where(s => s.ParentId == c.Id && s.Level == 2).Select(s => new PortalCategory(s.Id, s.Name, [])).ToList()))
            .ToList();
    }

    /// <summary>Custom fields a requester can fill in on a new request, with requester labels.</summary>
    public async Task<IReadOnlyList<PortalFieldDefinition>> RequesterFieldsAsync(CancellationToken ct = default)
    {
        var workspace = await PrimaryWorkspaceAsync(ct);
        var defs = await customFields.GetDefinitionsAsync(Ticket.Family, workspace, ct);
        return defs
            .Where(d => d.VisibleToRequesters && (d.EditableByRequesters || d.FieldType == CustomFieldType.Content))
            .Where(d => d.FieldType != CustomFieldType.Lookup)
            .Select(d => new PortalFieldDefinition(
                d.Key,
                string.IsNullOrWhiteSpace(d.RequesterLabel) ? d.Label : d.RequesterLabel,
                d.FieldType,
                d.RequiredForRequesters && d.FieldType != CustomFieldType.Content,
                d.HelpText,
                d.DefaultValue,
                FieldOptions(d)))
            .ToList();
    }

    /// <summary>Active colleagues matching a name or email, limited to names and emails.</summary>
    public async Task<IReadOnlyList<PortalPerson>> FindPeopleAsync(string? text, CancellationToken ct = default)
    {
        var me = RequireUser();
        var q = (text ?? "").Trim();
        if (q.Length < 2)
        {
            return [];
        }
        if (q.Length > 100)
        {
            q = q[..100];
        }
        var normalized = q.ToUpperInvariant();
        return await db.Users.AsNoTracking()
            .Where(u => u.IsActive && !u.IsForgotten && u.Id != me && u.Email != null
                && (u.DisplayName.Contains(q) || u.NormalizedEmail!.StartsWith(normalized)))
            .OrderBy(u => u.DisplayName)
            .Take(8)
            .Select(u => new PortalPerson(u.Id, u.DisplayName, u.Email!))
            .ToListAsync(ct);
    }

    // ---------------------------------------------------------------- helpers

    private Guid RequireUser() =>
        actors.Current is { IsInternal: false, UserId: { } id } ? id : throw new ForbiddenException("Sign in to see your requests.");

    private async Task<IReadOnlySet<Guid>> LeadDepartmentsAsync(CancellationToken ct) =>
        (await permissions.CurrentAsync(ct))?.LeadDepartmentIds ?? new HashSet<Guid>();

    private async Task<Guid> PrimaryWorkspaceAsync(CancellationToken ct) =>
        await db.Workspaces.AsNoTracking().Where(w => w.IsPrimary).Select(w => w.Id).FirstAsync(ct);

    private static (Guid? Category, Guid? SubCategory) ParseCategory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }
        var parts = value.Split(':');
        if (parts is ["c", var c] && Guid.TryParse(c, out var cat))
        {
            return (cat, null);
        }
        if (parts is ["s", var c2, var s] && Guid.TryParse(c2, out var parent) && Guid.TryParse(s, out var sub))
        {
            return (parent, sub);
        }
        throw new ValidationException("category", "Choose one of the listed topics.");
    }

    private static Dictionary<string, JsonElement?> ToCustomValues(IReadOnlyList<PortalFieldDefinition> defs, IReadOnlyDictionary<string, string[]> raw)
    {
        var result = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        if (raw.Count == 0)
        {
            return result;
        }
        foreach (var def in defs.Where(d => d.FieldType != CustomFieldType.Content))
        {
            if (!raw.TryGetValue(def.Key, out var values))
            {
                continue;
            }
            var filled = values.Select(v => v?.Trim() ?? "").Where(v => v.Length > 0).ToArray();
            if (filled.Length == 0)
            {
                continue;
            }
            switch (def.FieldType)
            {
                case CustomFieldType.Checkbox:
                    if (filled.Contains("true", StringComparer.OrdinalIgnoreCase))
                    {
                        result[def.Key] = JsonSerializer.SerializeToElement(true);
                    }
                    break;
                case CustomFieldType.MultiSelect:
                    result[def.Key] = JsonSerializer.SerializeToElement(filled);
                    break;
                case CustomFieldType.DependentDropdown:
                    try
                    {
                        result[def.Key] = JsonSerializer.SerializeToElement(JsonSerializer.Deserialize<string[]>(filled[0]) ?? []);
                    }
                    catch (JsonException)
                    {
                        throw new ValidationException($"custom.{def.Key}", $"Choose a valid {def.Label}.");
                    }
                    break;
                default:
                    result[def.Key] = JsonSerializer.SerializeToElement(filled[0]);
                    break;
            }
        }
        return result;
    }

    private async Task<IReadOnlyList<PortalFieldValue>> RequesterFieldValuesAsync(Guid workspaceId, string json, CancellationToken ct)
    {
        var values = CustomFieldService.Parse(json);
        if (values.Count == 0)
        {
            return [];
        }
        var defs = await customFields.GetDefinitionsAsync(Ticket.Family, workspaceId, ct);
        var list = new List<PortalFieldValue>();
        foreach (var def in defs.Where(d => d.VisibleToRequesters && d.FieldType is not (CustomFieldType.Content or CustomFieldType.Lookup)))
        {
            if (!values.TryGetPropertyValue(def.Key, out var node) || node is null)
            {
                continue;
            }
            var label = string.IsNullOrWhiteSpace(def.RequesterLabel) ? def.Label : def.RequesterLabel;
            if (def.FieldType == CustomFieldType.DateTime && node is JsonValue dv && dv.TryGetValue<string>(out var s)
                && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
            {
                list.Add(new PortalFieldValue(label, null, when));
                continue;
            }
            var text = node switch
            {
                JsonArray a => string.Join(def.FieldType == CustomFieldType.DependentDropdown ? " / " : ", ", a.Select(x => x?.ToString())),
                JsonValue v when v.TryGetValue<bool>(out var b) => b ? "Yes" : "No",
                _ => node.ToString(),
            };
            if (!string.IsNullOrWhiteSpace(text))
            {
                list.Add(new PortalFieldValue(label, text, null));
            }
        }
        return list;
    }

    private async Task<string?> CategoryPathAsync(Guid? category, Guid? sub, Guid? item, CancellationToken ct)
    {
        var ids = new[] { category, sub, item }.Where(x => x is not null).Select(x => x!.Value).ToList();
        if (ids.Count == 0)
        {
            return null;
        }
        var names = await db.TicketCategories.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return string.Join(" / ", ids.Where(names.ContainsKey).Select(id => names[id]));
    }

    private static IReadOnlyList<PortalFieldOption> FieldOptions(CustomFieldDefinition def)
    {
        if (def.FieldType is not (CustomFieldType.Dropdown or CustomFieldType.MultiSelect or CustomFieldType.DependentDropdown))
        {
            return [];
        }
        try
        {
            if (JsonNode.Parse(def.OptionsJson) is not JsonArray array)
            {
                return [];
            }
            if (def.FieldType == CustomFieldType.DependentDropdown)
            {
                var paths = new List<PortalFieldOption>();
                void Walk(JsonArray level, List<string> prefix)
                {
                    foreach (var o in level.OfType<JsonObject>())
                    {
                        var value = o["value"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(value) || prefix.Count >= 3)
                        {
                            continue;
                        }
                        var path = new List<string>(prefix) { value };
                        paths.Add(new PortalFieldOption(JsonSerializer.Serialize(path), string.Join(" / ", path)));
                        if (o["children"] is JsonArray children)
                        {
                            Walk(children, path);
                        }
                    }
                }
                Walk(array, []);
                return paths;
            }
            return array
                .Select(n => n is JsonObject o
                    ? (o["value"]?.GetValue<string>(), o["label"]?.GetValue<string>())
                    : (n?.GetValue<string>(), null))
                .Where(x => !string.IsNullOrEmpty(x.Item1))
                .Select(x => new PortalFieldOption(x.Item1!, x.Item2 ?? x.Item1!))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }
}
