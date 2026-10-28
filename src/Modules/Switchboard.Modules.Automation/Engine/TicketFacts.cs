using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;
using Switchboard.Modules.Automation.Definitions;

namespace Switchboard.Modules.Automation.Engine;

/// <summary>
/// A read-only snapshot of one ticket (plus its requester and the triggering message) that
/// conditions are evaluated against and placeholders are filled from.
/// </summary>
public sealed class TicketFacts
{
    public required Guid TicketId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required string DisplayId { get; init; }
    public TicketType Type { get; set; }
    public Guid StatusId { get; set; }
    public StatusCategory StatusCategory { get; set; }
    public Guid? GroupId { get; set; }
    public Guid? AgentId { get; set; }
    public Guid RequesterId { get; init; }
    public Guid? CategoryId { get; set; }
    public Guid? SubCategoryId { get; set; }
    public Guid? ItemCategoryId { get; set; }
    public List<string> Tags { get; set; } = [];
    public string CustomFieldsJson { get; set; } = "{}";
    public bool IsClosedOrDeleted { get; init; }

    /// <summary>Values by condition field key (see <see cref="CatalogLoader"/>).</summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Raw (unencoded) placeholder values by name, e.g. <c>ticket.subject</c>.</summary>
    public Dictionary<string, string> Placeholders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public object? Get(string key) => Values.GetValueOrDefault(key);
}

/// <summary>Loads <see cref="TicketFacts"/> without tracking, so test runs never write.</summary>
public sealed class TicketFactsLoader(SwitchboardDbContext db, IOptions<SwitchboardOptions> options)
{
    public async Task<TicketFacts?> LoadAsync(Guid ticketId, Guid? conversationId = null, CancellationToken ct = default)
    {
        var t = await db.Tickets.AsNoTracking()
            .Include(x => x.Status)
            .Include(x => x.Source)
            .Include(x => x.Tags)
            .FirstOrDefaultAsync(x => x.Id == ticketId && x.PurgedAt == null, ct);
        if (t is null)
        {
            return null;
        }

        var requester = await db.Users.AsNoTracking()
            .Where(u => u.Id == t.RequesterId)
            .Select(u => new { u.DisplayName, u.Email, u.DepartmentId, Department = u.Department!.Name, u.LocationId, u.JobTitle, u.IsVip })
            .FirstOrDefaultAsync(ct);
        var agent = t.AgentId is { } aid
            ? await db.Users.AsNoTracking().Where(u => u.Id == aid).Select(u => new { u.DisplayName, u.Email }).FirstOrDefaultAsync(ct)
            : null;
        var groupName = t.GroupId is { } gid ? await db.AgentGroups.AsNoTracking().Where(g => g.Id == gid).Select(g => g.Name).FirstOrDefaultAsync(ct) : null;
        var categoryIds = new[] { t.CategoryId, t.SubCategoryId, t.ItemCategoryId }.Where(x => x is not null).Select(x => x!.Value).ToList();
        var categoryNames = categoryIds.Count == 0
            ? []
            : await db.TicketCategories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var fieldDefs = await db.CustomFieldDefinitions.AsNoTracking()
            .Where(d => d.EntityType == "ticket" && (d.WorkspaceId == null || d.WorkspaceId == t.WorkspaceId))
            .Select(d => new { d.Key, d.FieldType })
            .ToListAsync(ct);

        TicketConversation? message = null;
        if (conversationId is { } cid)
        {
            message = await db.TicketConversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid && c.TicketId == t.Id, ct);
        }

        var facts = new TicketFacts
        {
            TicketId = t.Id,
            WorkspaceId = t.WorkspaceId,
            DisplayId = t.DisplayId,
            Type = t.Type,
            StatusId = t.StatusId,
            StatusCategory = t.Status?.Category ?? StatusCategory.Open,
            GroupId = t.GroupId,
            AgentId = t.AgentId,
            RequesterId = t.RequesterId,
            CategoryId = t.CategoryId,
            SubCategoryId = t.SubCategoryId,
            ItemCategoryId = t.ItemCategoryId,
            Tags = t.Tags.Select(x => x.Tag).Order(StringComparer.Ordinal).ToList(),
            CustomFieldsJson = t.CustomFieldsJson,
            IsClosedOrDeleted = t.DeletedAt is not null || t.IsSpam || t.MergedIntoId is not null,
        };

        var v = facts.Values;
        v["ticket.subject"] = t.Subject;
        v["ticket.description"] = t.DescriptionText;
        v["ticket.type"] = t.Type.ToString();
        v["ticket.status"] = t.StatusId.ToString();
        v["ticket.status_category"] = facts.StatusCategory.ToString();
        v["ticket.priority"] = t.Priority.ToString();
        v["ticket.urgency"] = t.Urgency.ToString();
        v["ticket.impact"] = t.Impact.ToString();
        v["ticket.source"] = t.Source?.Key;
        v["ticket.category"] = categoryIds.Select(id => id.ToString()).ToList();
        v["ticket.group"] = t.GroupId?.ToString();
        v["ticket.agent"] = t.AgentId?.ToString();
        v["ticket.department"] = t.DepartmentId?.ToString();
        v["ticket.tags"] = facts.Tags;
        v["ticket.major_incident"] = t.IsMajorIncident;
        v["requester.name"] = requester?.DisplayName ?? "";
        v["requester.email"] = requester?.Email ?? "";
        v["requester.department"] = requester?.DepartmentId?.ToString();
        v["requester.location"] = requester?.LocationId?.ToString();
        v["requester.job_title"] = requester?.JobTitle ?? "";
        v["requester.vip"] = requester?.IsVip == true;
        if (message is not null)
        {
            v["conversation.kind"] = message.Kind switch
            {
                ConversationKind.RequesterMessage => "requester",
                ConversationKind.AgentReply => "agent_reply",
                ConversationKind.Forward => "forward",
                _ => "note",
            };
            v["conversation.private"] = message.IsPrivate;
            v["conversation.body"] = message.BodyText;
        }

        var custom = CustomFieldService.Parse(t.CustomFieldsJson);
        foreach (var def in fieldDefs)
        {
            custom.TryGetPropertyValue(def.Key, out var node);
            v[$"ticket.cf.{def.Key}"] = def.FieldType switch
            {
                CustomFieldType.Number or CustomFieldType.Decimal => node is JsonValue n && decimal.TryParse(n.ToJsonString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null,
                CustomFieldType.Checkbox => node is JsonValue b && b.TryGetValue<bool>(out var flag) && flag,
                CustomFieldType.MultiSelect or CustomFieldType.DependentDropdown => node is JsonArray arr ? arr.Select(x => x?.ToString() ?? "").ToList() : new List<string>(),
                _ => node is null ? null : node is JsonValue s && s.TryGetValue<string>(out var str) ? str : node.ToJsonString(),
            };
        }

        var p = facts.Placeholders;
        var baseUrl = options.Value.BaseUrl.TrimEnd('/');
        p["ticket.id"] = t.DisplayId;
        p["ticket.subject"] = t.Subject;
        p["ticket.description"] = t.DescriptionText;
        p["ticket.status"] = t.Status?.Name ?? "";
        p["ticket.priority"] = t.Priority.ToString();
        p["ticket.type"] = t.Type == TicketType.Incident ? "Incident" : "Service request";
        p["ticket.category"] = string.Join(" / ", categoryIds.Where(categoryNames.ContainsKey).Select(id => categoryNames[id]));
        p["ticket.group"] = groupName ?? "";
        p["ticket.tags"] = string.Join(", ", facts.Tags);
        p["ticket.source"] = t.Source?.Name ?? "";
        p["ticket.url"] = $"{baseUrl}/tickets/{t.Number}";
        p["ticket.created_at"] = FormatUtc(t.CreatedAt);
        p["ticket.due_by"] = t.DueBy is { } due ? FormatUtc(due) : "";
        p["requester.name"] = requester?.DisplayName ?? "";
        p["requester.email"] = requester?.Email ?? "";
        p["requester.department"] = requester?.Department ?? "";
        p["agent.name"] = agent?.DisplayName ?? "";
        p["agent.email"] = agent?.Email ?? "";
        foreach (var def in fieldDefs)
        {
            custom.TryGetPropertyValue(def.Key, out var node);
            p[$"ticket.cf.{def.Key}"] = node switch
            {
                null => "",
                JsonArray arr => string.Join(", ", arr.Select(x => x?.ToString())),
                JsonValue s when s.TryGetValue<string>(out var str) => str,
                _ => node.ToJsonString(),
            };
        }
        return facts;
    }

    public static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
