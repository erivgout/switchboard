using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;

namespace Switchboard.Modules.Api.Endpoints;

/// <summary>
/// Body of <c>POST /api/v1/tickets</c>. Requesters (and agents with <c>?as=requester</c>) may only send
/// subject, descriptionHtml/descriptionText, type, requestedForId, ccEmails, customFields and attachmentIds.
/// </summary>
public sealed record CreateTicketRequest
{
    public Guid? WorkspaceId { get; init; }
    public TicketType? Type { get; init; }
    public string? Subject { get; init; }
    public string? DescriptionHtml { get; init; }
    /// <summary>Plain-text alternative to <see cref="DescriptionHtml"/>.</summary>
    public string? DescriptionText { get; init; }
    public Guid? RequesterId { get; init; }
    public string? RequesterEmail { get; init; }
    public string? RequesterName { get; init; }
    public Guid? RequestedForId { get; init; }
    public IReadOnlyList<string>? CcEmails { get; init; }
    public Guid? StatusId { get; init; }
    public Priority? Priority { get; init; }
    public Level? Urgency { get; init; }
    public Level? Impact { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? SubCategoryId { get; init; }
    public Guid? ItemCategoryId { get; init; }
    public Guid? GroupId { get; init; }
    public Guid? AgentId { get; init; }
    public Guid? DepartmentId { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public Dictionary<string, JsonElement?>? CustomFields { get; init; }
    /// <summary>Ids returned by <c>POST /api/v1/attachments</c>.</summary>
    public IReadOnlyList<Guid>? AttachmentIds { get; init; }
    public bool? IsMajorIncident { get; init; }
    public DateTimeOffset? PlannedStartAt { get; init; }
    public DateTimeOffset? PlannedEndAt { get; init; }
    public int? PlannedEffortMinutes { get; init; }

    internal IEnumerable<string> AgentOnlyFieldsSet()
    {
        if (RequesterId is not null) yield return "requesterId";
        if (RequesterEmail is not null) yield return "requesterEmail";
        if (RequesterName is not null) yield return "requesterName";
        if (StatusId is not null) yield return "statusId";
        if (Priority is not null) yield return "priority";
        if (Urgency is not null) yield return "urgency";
        if (Impact is not null) yield return "impact";
        if (CategoryId is not null) yield return "categoryId";
        if (SubCategoryId is not null) yield return "subCategoryId";
        if (ItemCategoryId is not null) yield return "itemCategoryId";
        if (GroupId is not null) yield return "groupId";
        if (AgentId is not null) yield return "agentId";
        if (DepartmentId is not null) yield return "departmentId";
        if (Tags is not null) yield return "tags";
        if (IsMajorIncident is not null) yield return "isMajorIncident";
        if (PlannedStartAt is not null) yield return "plannedStartAt";
        if (PlannedEndAt is not null) yield return "plannedEndAt";
        if (PlannedEffortMinutes is not null) yield return "plannedEffortMinutes";
    }
}

/// <summary>
/// Body of <c>PATCH /api/v1/tickets/{id}</c>: any subset of these fields. Omitted fields stay as they are;
/// <c>null</c> clears nullable ones. Unknown or read-only fields are rejected with 400.
/// </summary>
public sealed record TicketPatchRequest
{
    public string? Subject { get; init; }
    public string? DescriptionHtml { get; init; }
    public TicketType? Type { get; init; }
    public Guid? RequesterId { get; init; }
    public Guid? RequestedForId { get; init; }
    public IReadOnlyList<string>? CcEmails { get; init; }
    public Guid? StatusId { get; init; }
    public Priority? Priority { get; init; }
    public Level? Urgency { get; init; }
    public Level? Impact { get; init; }
    public Guid? SourceId { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? SubCategoryId { get; init; }
    public Guid? ItemCategoryId { get; init; }
    public Guid? GroupId { get; init; }
    public Guid? AgentId { get; init; }
    public Guid? DepartmentId { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public Dictionary<string, JsonElement?>? CustomFields { get; init; }
    public bool? IsMajorIncident { get; init; }
    public DateTimeOffset? DueBy { get; init; }
    public DateTimeOffset? FirstResponseDueBy { get; init; }
    /// <summary>Required with dueBy or firstResponseDueBy.</summary>
    public string? DueOverrideReason { get; init; }
    public DateTimeOffset? PlannedStartAt { get; init; }
    public DateTimeOffset? PlannedEndAt { get; init; }
    public int? PlannedEffortMinutes { get; init; }
}

public sealed record ReplyRequest
{
    public string? BodyHtml { get; init; }
    public string? BodyText { get; init; }
    public IReadOnlyList<string>? Cc { get; init; }
    public IReadOnlyList<string>? Bcc { get; init; }
    public IReadOnlyList<Guid>? AttachmentIds { get; init; }
    /// <summary>Set the ticket to this status in the same step.</summary>
    public Guid? StatusId { get; init; }
}

public sealed record NoteRequest
{
    public string? BodyHtml { get; init; }
    public string? BodyText { get; init; }
    /// <summary>Default true. Public notes are visible to the requester and need tickets:reply.</summary>
    public bool? IsPrivate { get; init; }
    public IReadOnlyList<Guid>? NotifyUserIds { get; init; }
    public IReadOnlyList<Guid>? AttachmentIds { get; init; }
}

public sealed record ForwardRequest
{
    public IReadOnlyList<string>? To { get; init; }
    public IReadOnlyList<string>? Cc { get; init; }
    public string? BodyHtml { get; init; }
    public string? BodyText { get; init; }
    public IReadOnlyList<Guid>? AttachmentIds { get; init; }
}

public sealed record RequesterMessageRequest
{
    public string? BodyHtml { get; init; }
    public string? BodyText { get; init; }
    public IReadOnlyList<Guid>? AttachmentIds { get; init; }
}

public sealed record MergeRequest(IReadOnlyList<Guid>? TicketIds);

public sealed record LinkRequest(string? TargetType, Guid? TargetId, string? LinkType);

public sealed record CreateTaskRequest
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public Guid? AssigneeId { get; init; }
    public Guid? GroupId { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public int? NotifyBeforeMinutes { get; init; }
    public DateTimeOffset? PlannedStartAt { get; init; }
    public DateTimeOffset? PlannedEndAt { get; init; }
    public int? PlannedEffortMinutes { get; init; }
}

/// <summary>Body of <c>PATCH /api/v1/tickets/{id}/tasks/{taskId}</c>.</summary>
public sealed record TaskPatchRequest
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public Guid? AssigneeId { get; init; }
    public Guid? GroupId { get; init; }
    public TicketTaskStatus? Status { get; init; }
    public DateTimeOffset? DueAt { get; init; }
}

public sealed record CreateRequesterRequest
{
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? JobTitle { get; init; }
    public string? Phone { get; init; }
    public string? Mobile { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? LocationId { get; init; }
    public string? TimeZone { get; init; }
    public string? Language { get; init; }
}

/// <summary>Maps partial JSON onto <see cref="TicketPatch"/> and <see cref="TaskPatch"/>: absent stays unset, null clears.</summary>
internal static partial class PatchReader
{
    private sealed class FieldError(string message) : Exception(message);

    public static TicketPatch ReadTicketPatch(JsonElement root)
    {
        var patch = new TicketPatch();
        var errors = new Dictionary<string, string[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in root.EnumerateObject())
        {
            if (!seen.Add(p.Name))
            {
                errors[p.Name] = ["This field appears twice."];
                continue;
            }
            var v = p.Value;
            try
            {
                patch = p.Name switch
                {
                    "subject" => patch with { Subject = String(v) },
                    "descriptionHtml" => patch with { DescriptionHtml = String(v) },
                    "type" => patch with { Type = Enum<TicketType>(v) },
                    "requesterId" => patch with { RequesterId = Id(v) },
                    "requestedForId" => patch with { RequestedForId = NullableId(v) },
                    "ccEmails" => patch with { CcEmails = Strings(v) },
                    "statusId" => patch with { StatusId = Id(v) },
                    "priority" => patch with { Priority = Enum<Priority>(v) },
                    "urgency" => patch with { Urgency = Enum<Level>(v) },
                    "impact" => patch with { Impact = Enum<Level>(v) },
                    "sourceId" => patch with { SourceId = Id(v) },
                    "categoryId" => patch with { CategoryId = NullableId(v) },
                    "subCategoryId" => patch with { SubCategoryId = NullableId(v) },
                    "itemCategoryId" => patch with { ItemCategoryId = NullableId(v) },
                    "groupId" => patch with { GroupId = NullableId(v) },
                    "agentId" => patch with { AgentId = NullableId(v) },
                    "departmentId" => patch with { DepartmentId = NullableId(v) },
                    "tags" => patch with { Tags = Strings(v) },
                    "customFields" => patch with { CustomFields = Fields(v) },
                    "isMajorIncident" => patch with { IsMajorIncident = Bool(v) },
                    "dueBy" => patch with { DueBy = NullableDate(v) },
                    "firstResponseDueBy" => patch with { FirstResponseDueBy = NullableDate(v) },
                    "dueOverrideReason" => patch with { DueOverrideReason = NullableString(v) },
                    "plannedStartAt" => patch with { PlannedStartAt = NullableDate(v) },
                    "plannedEndAt" => patch with { PlannedEndAt = NullableDate(v) },
                    "plannedEffortMinutes" => patch with { PlannedEffortMinutes = NullableInt(v) },
                    _ => throw new FieldError("This field doesn't exist or can't be changed."),
                };
            }
            catch (FieldError e)
            {
                errors[p.Name] = [e.Message];
            }
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
        return patch;
    }

    public static TaskPatch ReadTaskPatch(JsonElement root)
    {
        var patch = new TaskPatch();
        var errors = new Dictionary<string, string[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in root.EnumerateObject())
        {
            if (!seen.Add(p.Name))
            {
                errors[p.Name] = ["This field appears twice."];
                continue;
            }
            var v = p.Value;
            try
            {
                patch = p.Name switch
                {
                    "title" => patch with { Title = String(v) },
                    "description" => patch with { Description = NullableString(v) },
                    "assigneeId" => patch with { AssigneeId = NullableId(v) },
                    "groupId" => patch with { GroupId = NullableId(v) },
                    "status" => patch with { Status = Enum<TicketTaskStatus>(v) },
                    "dueAt" => patch with { DueAt = NullableDate(v) },
                    _ => throw new FieldError("This field doesn't exist or can't be changed."),
                };
            }
            catch (FieldError e)
            {
                errors[p.Name] = [e.Message];
            }
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
        return patch;
    }

    private static string String(JsonElement v) =>
        v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new FieldError("Use a string.");

    private static string? NullableString(JsonElement v) =>
        v.ValueKind == JsonValueKind.Null ? null : String(v);

    private static Guid Id(JsonElement v) =>
        v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : throw new FieldError("Use an id (a GUID string).");

    private static Guid? NullableId(JsonElement v) =>
        v.ValueKind == JsonValueKind.Null ? null : Id(v);

    private static bool Bool(JsonElement v) =>
        v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FieldError("Use true or false."),
        };

    private static int? NullableInt(JsonElement v) =>
        v.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number when v.TryGetInt32(out var i) && i >= 0 => i,
            _ => throw new FieldError("Use a whole number of zero or more, or null."),
        };

    private static T Enum<T>(JsonElement v) where T : struct, Enum
    {
        if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s && char.IsLetter(s[0])
            && System.Enum.TryParse<T>(s, ignoreCase: true, out var value) && System.Enum.IsDefined(value))
        {
            return value;
        }
        var names = string.Join(", ", System.Enum.GetNames<T>().Select(JsonNamingPolicy.CamelCase.ConvertName));
        throw new FieldError($"Use one of: {names}.");
    }

    private static string[] Strings(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Array)
        {
            throw new FieldError("Use an array of strings.");
        }
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            list.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new FieldError("Use an array of strings."));
            if (list.Count > 200)
            {
                throw new FieldError("Send at most 200 values.");
            }
        }
        return [.. list];
    }

    private static Dictionary<string, JsonElement?> Fields(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object)
        {
            throw new FieldError("Use an object of field keys and values.");
        }
        var fields = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        foreach (var f in v.EnumerateObject())
        {
            fields[f.Name] = f.Value.ValueKind == JsonValueKind.Null ? null : f.Value.Clone();
            if (fields.Count > 200)
            {
                throw new FieldError("Send at most 200 custom fields.");
            }
        }
        return fields;
    }

    /// <summary>ISO 8601. A time without an offset is taken as UTC, never as the server's local time.</summary>
    private static DateTimeOffset? NullableDate(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s)
        {
            if (HasOffset().IsMatch(s) && v.TryGetDateTimeOffset(out var withOffset))
            {
                return withOffset.ToUniversalTime();
            }
            if (!HasOffset().IsMatch(s) && v.TryGetDateTime(out var plain))
            {
                return new DateTimeOffset(DateTime.SpecifyKind(plain, DateTimeKind.Utc));
            }
        }
        throw new FieldError("Use an ISO 8601 date and time, for example 2026-11-02T17:00:00Z, or null.");
    }

    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HasOffset();

    internal static string FormatInvariant(DateTimeOffset d) => d.ToString("O", CultureInfo.InvariantCulture);
}
