using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Agents;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Mcp.Server;

namespace Switchboard.Modules.Mcp.Tools;

/// <summary>A ticket named by id, display id (INC-1042, SR-7) or number.</summary>
public readonly record struct TicketReference(Guid? Id, TicketType? Type, int? Number)
{
    public string Label => Id?.ToString() ?? (Type is { } t ? Ticket.FormatDisplayId(t, Number!.Value) : $"#{Number}");

    public static bool TryParse(string? raw, out TicketReference reference)
    {
        reference = default;
        var value = (raw ?? "").Trim();
        if (Guid.TryParse(value, out var id))
        {
            reference = new TicketReference(id, null, null);
            return true;
        }
        TicketType? type = null;
        if (value.StartsWith("INC-", StringComparison.OrdinalIgnoreCase))
        {
            type = TicketType.Incident;
            value = value[4..];
        }
        else if (value.StartsWith("SR-", StringComparison.OrdinalIgnoreCase))
        {
            type = TicketType.ServiceRequest;
            value = value[3..];
        }
        else if (value.StartsWith('#'))
        {
            value = value[1..];
        }
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0)
        {
            reference = new TicketReference(null, type, number);
            return true;
        }
        return false;
    }
}

/// <summary>
/// Loading and shaping tickets for agent tools. Everything people wrote goes out wrapped as untrusted
/// data (MCP-60), with hidden content stripped (MCP-63); requesters get only what the portal shows them.
/// </summary>
public sealed class TicketToolSupport(
    SwitchboardDbContext db,
    IPermissionService permissions,
    ITicketService tickets,
    ISettingsService settings,
    IActorAccessor actors)
{
    public const int SummaryDescriptionChars = 2000;
    public const int FullDescriptionChars = 20000;

    public async Task<bool> IsAgentAsync(CancellationToken ct) => (await permissions.CurrentAsync(ct))?.IsAgent == true;

    public Guid CurrentUserId => actors.Current.UserId ?? throw new ForbiddenException();

    /// <summary>The visible ticket, or <see cref="NotFoundException"/> whether it is missing or hidden (SR-AUTHZ-6).</summary>
    public async Task<Ticket> FindAsync(string? raw, bool asAgent, CancellationToken ct)
    {
        if (!TicketReference.TryParse(raw, out var reference))
        {
            throw new ValidationException("ticket_id", "Use a ticket id or display id such as INC-1042.");
        }
        var visible = asAgent ? await permissions.AgentVisibleTicketsAsync(ct) : await permissions.RequesterVisibleTicketsAsync(ct);
        var query = visible.AsNoTracking().Where(t => t.PurgedAt == null);
        query = reference.Id is { } id
            ? query.Where(t => t.Id == id)
            : query.Where(t => t.Number == reference.Number!.Value);
        var ticketId = await query.Where(t => reference.Type == null || t.Type == reference.Type).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException();
        return asAgent ? await tickets.GetForAgentAsync(ticketId, ct) : await tickets.GetForRequesterAsync(ticketId, ct);
    }

    public static string Version(Ticket t) => Convert.ToBase64String(t.RowVersion);

    public static JsonObject Untrusted(string? value, string source, string? author = null) => AgentContent.Untrusted(value, source, author);

    /// <summary>Visible text of stored HTML, hidden parts removed and flagged, truncated to <paramref name="max"/>.</summary>
    public static JsonObject UntrustedHtml(string? html, string source, string? author, int max)
    {
        var (text, hidden) = ContentSafety.VisibleText(html);
        var truncated = text.Length > max;
        var wrapper = AgentContent.Untrusted(truncated ? text[..max] : text, source, author);
        if (hidden)
        {
            wrapper[ContentSafety.HiddenFlag] = true;
        }
        if (truncated)
        {
            wrapper["truncated"] = true;
        }
        return wrapper;
    }

    public static JsonObject Person(Guid? id, string? name, string? email = null)
    {
        var o = new JsonObject { ["id"] = id?.ToString(), ["name"] = Untrusted(name, "user_profile") };
        if (email is not null)
        {
            o["email"] = email;
        }
        return o;
    }

    public static string Label(StatusCategory c) => c.ToString().ToLowerInvariant();

    public static string TypeKey(TicketType t) => t == TicketType.Incident ? "incident" : "service_request";

    public async Task<bool> ShowAgentNamesToRequestersAsync(Guid workspaceId, CancellationToken ct) =>
        await settings.GetAsync(SettingKeys.ShowAgentNamesToRequesters, true, workspaceId, ct);

    /// <summary>One search result row.</summary>
    public static JsonObject Summary(TicketListItem t, bool asAgent, bool showAgentNames)
    {
        var o = new JsonObject
        {
            ["id"] = t.Id.ToString(),
            ["display_id"] = t.DisplayId,
            ["type"] = TypeKey(t.Type),
            ["subject"] = Untrusted(t.Subject, "ticket_subject"),
            ["status"] = asAgent ? t.StatusName : t.StatusRequesterLabel ?? t.StatusName,
            ["state"] = Label(t.StatusCategory),
            ["created_at"] = t.CreatedAt.ToString("O"),
            ["updated_at"] = t.UpdatedAt.ToString("O"),
        };
        if (asAgent)
        {
            o["priority"] = t.Priority.ToString().ToLowerInvariant();
            o["group"] = t.GroupName;
            o["agent"] = t.AgentId is null ? null : Person(t.AgentId, t.AgentName);
            o["requester"] = Person(t.RequesterId, t.RequesterName, t.RequesterEmail);
            o["due_by"] = t.DueBy?.ToString("O");
            o["waiting_on_agent"] = t.IsLampLit;
            o["is_major_incident"] = t.IsMajorIncident;
            if (t.DeletedAt is not null)
            {
                o["in_trash"] = true;
            }
        }
        else if (showAgentNames && t.AgentId is not null)
        {
            o["agent"] = Untrusted(t.AgentName, "user_profile");
        }
        return o;
    }

    /// <summary>One ticket in full, as the caller may see it.</summary>
    public async Task<JsonObject> DetailAsync(Ticket t, bool asAgent, bool full, CancellationToken ct)
    {
        var max = full ? FullDescriptionChars : SummaryDescriptionChars;
        var status = t.Status ?? await db.TicketStatuses.AsNoTracking().FirstAsync(s => s.Id == t.StatusId, ct);
        var requester = await db.Users.AsNoTracking().Where(u => u.Id == t.RequesterId).Select(u => new { u.Id, u.DisplayName, u.Email }).FirstAsync(ct);
        var o = new JsonObject
        {
            ["id"] = t.Id.ToString(),
            ["display_id"] = t.DisplayId,
            ["type"] = TypeKey(t.Type),
            ["subject"] = Untrusted(t.Subject, "ticket_subject"),
            ["description"] = UntrustedHtml(t.DescriptionHtml, "ticket_description", requester.DisplayName, max),
            ["status"] = asAgent ? status.Name : status.RequesterLabel ?? status.Name,
            ["state"] = Label(status.Category),
            ["created_at"] = t.CreatedAt.ToString("O"),
            ["updated_at"] = t.UpdatedAt.ToString("O"),
            ["resolved_at"] = t.ResolvedAt?.ToString("O"),
        };

        var definitions = await db.CustomFieldDefinitions.AsNoTracking()
            .Where(d => d.EntityType == "ticket" && d.IsActive && (d.WorkspaceId == null || d.WorkspaceId == t.WorkspaceId))
            .OrderBy(d => d.Order).ToListAsync(ct);
        var values = CustomFieldService.Parse(t.CustomFieldsJson);
        var custom = new JsonObject();
        foreach (var d in definitions.Where(d => asAgent || d.VisibleToRequesters))
        {
            if (values[d.Key] is { } value)
            {
                var wrapper = Untrusted(value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString(), "custom_field");
                wrapper["label"] = asAgent ? d.Label : d.RequesterLabel ?? d.Label;
                custom[d.Key] = wrapper;
            }
        }
        o["custom_fields"] = custom;

        var attachments = await db.Attachments.AsNoTracking()
            .Where(a => a.OwnerType == AttachmentOwners.Ticket && a.OwnerId == t.Id && !a.IsInline)
            .OrderBy(a => a.CreatedAt).Take(50)
            .Select(a => new { a.Id, a.FileName, a.ContentType, a.Size })
            .ToListAsync(ct);
        o["attachments"] = new JsonArray(attachments.Select(a => (JsonNode)Attachment(a.Id, a.FileName, a.ContentType, a.Size)).ToArray());

        if (!asAgent)
        {
            if (await ShowAgentNamesToRequestersAsync(t.WorkspaceId, ct) && t.AgentId is { } agentId)
            {
                o["agent"] = Untrusted(await db.Users.AsNoTracking().Where(u => u.Id == agentId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct), "user_profile");
            }
            return o;
        }

        o["version"] = Version(t);
        o["priority"] = t.Priority.ToString().ToLowerInvariant();
        o["urgency"] = t.Urgency.ToString().ToLowerInvariant();
        o["impact"] = t.Impact.ToString().ToLowerInvariant();
        o["status_id"] = status.Id.ToString();
        o["source"] = await db.TicketSources.AsNoTracking().Where(s => s.Id == t.SourceId).Select(s => s.Key).FirstOrDefaultAsync(ct);
        o["workspace_id"] = t.WorkspaceId.ToString();
        o["requester"] = Person(requester.Id, requester.DisplayName, requester.Email);
        if (t.RequestedForId is { } rf)
        {
            o["requested_for"] = Person(rf, await db.Users.AsNoTracking().Where(u => u.Id == rf).Select(u => u.DisplayName).FirstOrDefaultAsync(ct));
        }
        o["cc_emails"] = new JsonArray(t.CcEmails.Select(e => (JsonNode)e).ToArray());
        o["group"] = t.GroupId is { } g
            ? new JsonObject { ["id"] = g.ToString(), ["name"] = await db.AgentGroups.AsNoTracking().Where(x => x.Id == g).Select(x => x.Name).FirstOrDefaultAsync(ct) }
            : null;
        o["agent"] = t.AgentId is { } a ? Person(a, await db.Users.AsNoTracking().Where(u => u.Id == a).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)) : null;
        var categoryIds = new[] { t.CategoryId, t.SubCategoryId, t.ItemCategoryId }.OfType<Guid>().ToList();
        if (categoryIds.Count > 0)
        {
            var names = await db.TicketCategories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            o["category"] = string.Join(" / ", categoryIds.Where(names.ContainsKey).Select(id => names[id]));
            o["category_ids"] = new JsonArray(categoryIds.Select(id => (JsonNode)id.ToString()).ToArray());
        }
        o["department"] = t.DepartmentId is { } dep ? await db.Departments.AsNoTracking().Where(d => d.Id == dep).Select(d => d.Name).FirstOrDefaultAsync(ct) : null;
        var tags = await db.TicketTags.AsNoTracking().Where(x => x.TicketId == t.Id).Select(x => x.Tag).OrderBy(x => x).ToListAsync(ct);
        o["tags"] = new JsonArray(tags.Select(x => (JsonNode)Untrusted(x, "tag")).ToArray());
        o["due_by"] = t.DueBy?.ToString("O");
        o["first_response_due_by"] = t.FirstResponseDueBy?.ToString("O");
        o["first_responded_at"] = t.FirstRespondedAt?.ToString("O");
        o["closed_at"] = t.ClosedAt?.ToString("O");
        o["waiting_on_agent"] = t.IsLampLit(status.Category);
        o["is_major_incident"] = t.IsMajorIncident;
        o["is_spam"] = t.IsSpam;
        o["in_trash"] = t.DeletedAt is not null;
        o["merged_into_id"] = t.MergedIntoId?.ToString();
        o["parent_id"] = t.ParentId?.ToString();
        o["conversation_count"] = await db.TicketConversations.CountAsync(c => c.TicketId == t.Id && c.DeletedAt == null, ct);
        o["task_count"] = await db.TicketTasks.CountAsync(x => x.TicketId == t.Id, ct);
        var links = await db.TicketLinks.AsNoTracking().Where(l => l.TicketId == t.Id).OrderBy(l => l.CreatedAt).Take(50)
            .Select(l => new { l.Id, l.TargetType, l.TargetId, l.LinkType }).ToListAsync(ct);
        o["links"] = new JsonArray(links.Select(l => (JsonNode)new JsonObject
        {
            ["id"] = l.Id.ToString(),
            ["target_type"] = l.TargetType,
            ["target_id"] = l.TargetId.ToString(),
            ["link_type"] = l.LinkType,
        }).ToArray());
        return o;
    }

    public static JsonObject Attachment(Guid id, string name, string contentType, long size) => new()
    {
        ["id"] = id.ToString(),
        ["name"] = Untrusted(name, "attachment_name"),
        ["content_type"] = contentType,
        ["size"] = size,
        ["uri"] = $"/files/{id}/{Uri.EscapeDataString(name)}",
    };

    public async Task<TicketStatus> ResolveStatusAsync(string value, Guid workspaceId, CancellationToken ct)
    {
        var statuses = await db.TicketStatuses.AsNoTracking().Where(s => s.WorkspaceId == workspaceId).ToListAsync(ct);
        return statuses.FirstOrDefault(s => Guid.TryParse(value, out var id) && s.Id == id)
            ?? statuses.FirstOrDefault(s => string.Equals(s.Name, value.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ValidationException("status", $"Use one of these statuses: {string.Join(", ", statuses.OrderBy(s => s.Order).Select(s => s.Name))}.");
    }

    public async Task<Guid?> ResolveGroupAsync(string? value, Guid workspaceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var groups = await db.AgentGroups.AsNoTracking().Where(g => g.WorkspaceId == workspaceId).Select(g => new { g.Id, g.Name }).ToListAsync(ct);
        return groups.FirstOrDefault(g => Guid.TryParse(value, out var id) && g.Id == id)?.Id
            ?? groups.FirstOrDefault(g => string.Equals(g.Name, value.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new ValidationException("group", "That group isn't in this workspace. Use search_groups to find one.");
    }

    /// <summary>"me", a user id, or an email address.</summary>
    public async Task<Guid?> ResolveUserAsync(string? value, string field, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        value = value.Trim();
        if (value.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            return CurrentUserId;
        }
        if (Guid.TryParse(value, out var id))
        {
            return await db.Users.AnyAsync(u => u.Id == id && u.IsActive, ct) ? id : throw new ValidationException(field, "That person doesn't exist or is inactive.");
        }
        var normalized = value.ToUpperInvariant();
        return await db.Users.Where(u => u.NormalizedEmail == normalized && u.IsActive).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct)
            ?? throw new ValidationException(field, "Nobody active has that email address. Use search_users to find someone.");
    }

    public static Dictionary<string, JsonElement?>? CustomFields(JsonObject args, string name)
    {
        if (args[name] is not JsonObject custom)
        {
            return null;
        }
        return custom.ToDictionary(p => p.Key, p => p.Value is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(p.Value));
    }

    public static T ParseEnum<T>(string value, string field) where T : struct, Enum =>
        Enum.TryParse<T>(value.Replace("_", ""), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ValidationException(field, $"Use one of: {string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}.");

    public static TicketType ParseType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "incident" => TicketType.Incident,
        "service_request" or "request" => TicketType.ServiceRequest,
        _ => throw new ValidationException("type", "Use incident or service_request."),
    };

    public static string Preview(string text, int max = 1500) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>Who will receive a reply on this ticket: the requester and the existing CC list (MCP-62).</summary>
    public async Task<(string Requester, IReadOnlyList<string> Cc)> RecipientsAsync(Ticket t, CancellationToken ct)
    {
        var requester = await db.Users.AsNoTracking().Where(u => u.Id == t.RequesterId)
            .Select(u => new { u.DisplayName, u.Email }).FirstAsync(ct);
        return ($"{requester.DisplayName} <{requester.Email}>", t.CcEmails);
    }
}
