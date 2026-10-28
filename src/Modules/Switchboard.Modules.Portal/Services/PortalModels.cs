using Switchboard.Core.Domain;

namespace Switchboard.Modules.Portal.Services;

public enum RequestTab
{
    /// <summary>Requested by me or for me.</summary>
    Mine,
    /// <summary>Someone shared it with me (FR-TKT-14).</summary>
    Shared,
    /// <summary>My department's requests, for department heads and prime users (FR-IAM-5).</summary>
    Department,
}

public enum RequestState
{
    /// <summary>Open or waiting on the requester.</summary>
    Open,
    /// <summary>Resolved or closed.</summary>
    Closed,
    All,
}

/// <summary>How the viewer relates to a request they can see in the portal.</summary>
public enum ViewerRelation
{
    Requester,
    RequestedFor,
    Shared,
    Department,
}

/// <summary>One row in a requester-facing list. Carries no internal fields (SR-AUTHZ-7).</summary>
public sealed record PortalRequestRow
{
    public Guid Id { get; init; }
    public int Number { get; init; }
    public TicketType Type { get; init; }
    public string DisplayId => Ticket.FormatDisplayId(Type, Number);
    public string Subject { get; init; } = "";
    public string StatusName { get; init; } = "";
    public string? StatusRequesterLabel { get; init; }
    public StatusCategory StatusCategory { get; init; }
    /// <summary>Requester-facing label, reworded when it addresses "you" but the viewer isn't the requester.</summary>
    public string StatusLabel => PortalText.StatusLabel(StatusName, StatusRequesterLabel, StatusCategory, IsMine ? null : RequesterName);
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string RequesterName { get; init; } = "";
    public bool IsMine { get; init; }
}

public sealed record PortalRequestPage(IReadOnlyList<PortalRequestRow> Items, int Total, int Page, int PageSize)
{
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page * PageSize < Total;
}

public sealed record PortalTabCounts(int Mine, int Shared, int Department, bool IsDepartmentLead);

public enum PortalMessageKind
{
    /// <summary>From the requester or a colleague.</summary>
    Requester,
    /// <summary>A reply or public note from the service desk.</summary>
    Support,
    /// <summary>A public system notice, e.g. a merge.</summary>
    System,
}

public sealed record PortalFile(Guid Id, string FileName, long Size)
{
    public string Url => $"/files/{Id}/{Uri.EscapeDataString(FileName)}";
}

public sealed record PortalMessage(
    Guid Id,
    PortalMessageKind Kind,
    string AuthorName,
    bool IsYou,
    // Null for the service desk when agent names are hidden.
    string? AvatarName,
    string BodyHtml,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PortalFile> Files);

public sealed record PortalFieldValue(string Label, string? Text, DateTimeOffset? Time);

public sealed record PortalPerson(Guid Id, string Name, string Email);

/// <summary>
/// Everything the requester page shows, already trimmed to public content: no private notes,
/// forwards, hidden messages, internal fields, group names or internal attachments (INV-TKT-7).
/// </summary>
public sealed record PortalRequestView
{
    public Guid Id { get; init; }
    public int Number { get; init; }
    public TicketType Type { get; init; }
    public string DisplayId => Ticket.FormatDisplayId(Type, Number);
    public string Subject { get; init; } = "";
    public string DescriptionHtml { get; init; } = "";
    public string StatusLabel { get; init; } = "";
    public StatusCategory StatusCategory { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public string RequesterName { get; init; } = "";
    public string? RequestedForName { get; init; }
    public ViewerRelation Relation { get; init; }
    public bool IsOwn => Relation is ViewerRelation.Requester or ViewerRelation.RequestedFor;
    public string? Category { get; init; }
    /// <summary>Set only when agent names are shown to requesters.</summary>
    public string? HandledBy { get; init; }
    /// <summary>Only for the requester and the person it was requested for.</summary>
    public IReadOnlyList<string> CcEmails { get; init; } = [];
    public IReadOnlyList<PortalFieldValue> Fields { get; init; } = [];
    public IReadOnlyList<PortalFile> Files { get; init; } = [];
    public IReadOnlyList<PortalMessage> Messages { get; init; } = [];
    /// <summary>Only for the requester and the person it was requested for.</summary>
    public IReadOnlyList<PortalPerson> SharedWith { get; init; } = [];

    public bool CanClose { get; init; }
    public bool CanShare { get; init; }
    /// <summary>The viewer can add a message right now (it may reopen the request or open a follow-up).</summary>
    public bool CanReply { get; init; }
    /// <summary>Replying reopens a resolved or closed request.</summary>
    public bool ReplyReopens { get; init; }
    /// <summary>Replying to this closed request opens a new linked request instead.</summary>
    public bool ReplyCreatesFollowUp { get; init; }
    /// <summary>Once closed, the viewer couldn't reopen it or open a follow-up from the portal.</summary>
    public bool ClosingIsFinal { get; init; }
    /// <summary>What the reply box does now: <c>reply</c>, <c>reopen</c>, <c>followup</c> or <c>none</c>.</summary>
    public string ReplyMode => !CanReply ? "none" : ReplyCreatesFollowUp ? "followup" : ReplyReopens ? "reopen" : "reply";

    public bool IsMerged { get; init; }
    /// <summary>The request this one was merged into, when the viewer can see it.</summary>
    public int? MergedIntoNumber { get; init; }
    public string? MergedIntoDisplayId { get; init; }
}

public sealed record PortalCategory(Guid Id, string Name, IReadOnlyList<PortalCategory> Children);

public sealed record PortalFieldOption(string Value, string Label);

/// <summary>A custom field a requester may fill in, with requester-facing wording.</summary>
public sealed record PortalFieldDefinition(
    string Key,
    string Label,
    CustomFieldType FieldType,
    bool Required,
    string? HelpText,
    string? DefaultValue,
    IReadOnlyList<PortalFieldOption> Options);

public sealed record NewPortalRequest
{
    public TicketType Type { get; init; } = TicketType.Incident;
    public string Subject { get; init; } = "";
    /// <summary>Plain text; converted to HTML on save.</summary>
    public string? Description { get; init; }
    /// <summary><c>c:{categoryId}</c> or <c>s:{categoryId}:{subCategoryId}</c>.</summary>
    public string? Category { get; init; }
    public IReadOnlyList<string> CcEmails { get; init; } = [];
    public IReadOnlyList<Guid> AttachmentIds { get; init; } = [];
    /// <summary>Raw form values by field key.</summary>
    public IReadOnlyDictionary<string, string[]> CustomFields { get; init; } = new Dictionary<string, string[]>();
}

public static partial class PortalText
{
    /// <summary>
    /// The status as requesters see it. Labels written to the requester ("Waiting on you") are reworded
    /// for colleagues and department heads, who aren't the "you" in them.
    /// </summary>
    public static string StatusLabel(string name, string? requesterLabel, StatusCategory category, string? requesterNameWhenNotViewer = null)
    {
        var label = string.IsNullOrWhiteSpace(requesterLabel) ? name : requesterLabel;
        if (requesterNameWhenNotViewer is null || !SecondPerson().IsMatch(label))
        {
            return label;
        }
        return category switch
        {
            StatusCategory.Open => "Being worked on",
            StatusCategory.Pending => $"Waiting on {FirstName(requesterNameWhenNotViewer)}",
            StatusCategory.Resolved => "Resolved",
            _ => "Closed",
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\byou(r|rs)?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SecondPerson();

    public static string FileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    public static string FirstName(string? displayName)
    {
        var parts = (displayName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "there" : parts[0];
    }
}
