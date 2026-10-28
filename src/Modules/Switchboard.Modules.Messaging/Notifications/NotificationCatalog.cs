namespace Switchboard.Modules.Messaging.Notifications;

public enum NotificationAudience
{
    Requester,
    Agent,
    Other,
}

/// <summary>One notification event: who gets it, whether it is automatic, and its default template (FR-NOT-1, FR-NOT-2).</summary>
public sealed record NotificationEvent(
    string Key,
    string Name,
    string Description,
    NotificationAudience Audience,
    bool Automatic,
    string DefaultSubject,
    string DefaultBody,
    bool EnabledByDefault = true,
    bool DefaultEmail = true,
    bool DefaultInApp = true);

public sealed record PlaceholderInfo(string Key, string Description, bool IsHtml = false);

/// <summary>The built-in event catalog. Template rows only override these defaults.</summary>
public static class NotificationCatalog
{
    public const string RequesterAcknowledgement = "requester.ticket_created";
    public const string RequesterAgentReply = "requester.agent_reply";
    public const string RequesterPublicNote = "requester.public_note";
    public const string RequesterPending = "requester.ticket_pending";
    public const string RequesterResolved = "requester.ticket_resolved";
    public const string RequesterClosed = "requester.ticket_closed";
    public const string Forward = "other.forward";
    public const string AgentAssigned = "agent.ticket_assigned";
    public const string AgentGroupAssigned = "agent.group_assigned";
    public const string AgentNewInGroup = "agent.new_ticket_in_group";
    public const string AgentRequesterReplied = "agent.requester_replied";
    public const string AgentMentioned = "agent.mentioned";

    /// <summary>Ad-hoc mail sent by other modules through <c>ISystemEmailSender</c>.</summary>
    public const string SystemMail = "system";

    private const string PortalLink = """<p><a href="{{ticket.portal_url}}">View your request</a></p>""";
    private const string AgentLink = """<p><a href="{{ticket.url}}">Open {{ticket.id}}</a></p>""";

    public static IReadOnlyList<NotificationEvent> All { get; } =
    [
        new(RequesterAcknowledgement, "Request received", "Sent to the requester when they raise a ticket by email or in the portal.",
            NotificationAudience.Requester, Automatic: true,
            "Request received: {{ticket.subject}}",
            """
            <p>Hi {{requester.first_name}},</p>
            <p>Thanks for getting in touch. We've logged your request as <strong>{{ticket.id}}</strong>, and someone from the team will pick it up soon.</p>
            <p>To add details, reply to this email. Your reply goes straight onto the request.</p>
            """ + PortalLink),
        new(RequesterAgentReply, "Reply from an agent", "Carries an agent's reply to the requester and anyone copied.",
            NotificationAudience.Requester, Automatic: false,
            "Re: {{ticket.subject}}",
            """
            {{message.body}}
            {{agent.signature}}
            """),
        new(RequesterPublicNote, "Public note", "Sent to the requester when an agent adds a note they can see.",
            NotificationAudience.Requester, Automatic: false,
            "Update on {{ticket.subject}}",
            """
            <p>Hi {{requester.first_name}},</p>
            <p>{{message.author}} added an update to your request {{ticket.id}}:</p>
            {{message.body}}
            """ + PortalLink),
        new(RequesterPending, "Waiting on the requester", "Sent when a ticket moves to a pending status, so the requester knows we need something.",
            NotificationAudience.Requester, Automatic: true,
            "We need more information: {{ticket.subject}}",
            """
            <p>Hi {{requester.first_name}},</p>
            <p>We need a little more information to continue with {{ticket.id}}. Reply to this email with the details, and we'll pick it straight back up.</p>
            """ + PortalLink,
            EnabledByDefault: false),
        new(RequesterResolved, "Request resolved", "Sent when a ticket is resolved.",
            NotificationAudience.Requester, Automatic: true,
            "Resolved: {{ticket.subject}}",
            """
            <p>Hi {{requester.first_name}},</p>
            <p>We've marked your request {{ticket.id}} as resolved.</p>
            <p>If something still isn't right, reply to this email and we'll reopen it.</p>
            """ + PortalLink),
        new(RequesterClosed, "Request closed", "Sent when a ticket is closed.",
            NotificationAudience.Requester, Automatic: true,
            "Closed: {{ticket.subject}}",
            """
            <p>Hi {{requester.first_name}},</p>
            <p>Your request {{ticket.id}} is now closed. If you need more help with it, reply to this email.</p>
            """ + PortalLink),
        new(Forward, "Forwarded ticket", "Carries an agent's forward to people outside the ticket.",
            NotificationAudience.Other, Automatic: false,
            "Fwd: {{ticket.subject}}",
            """
            {{message.body}}
            {{agent.signature}}
            <hr>
            <p><strong>{{ticket.id}}: {{ticket.subject}}</strong><br>Raised by {{requester.name}}</p>
            {{ticket.description}}
            """),
        new(AgentAssigned, "Assigned to you", "Sent to an agent when a ticket is assigned to them.",
            NotificationAudience.Agent, Automatic: true,
            "Assigned to you: {{ticket.subject}}",
            """
            <p>{{actor.name}} assigned <strong>{{ticket.id}}</strong> to you.</p>
            <p><strong>{{ticket.subject}}</strong><br>Requester: {{requester.name}}<br>Priority: {{ticket.priority}}</p>
            """ + AgentLink),
        new(AgentGroupAssigned, "Assigned to your group", "Sent to a group's members when a ticket is assigned to the group but nobody in it.",
            NotificationAudience.Agent, Automatic: true,
            "Assigned to {{group.name}}: {{ticket.subject}}",
            """
            <p>{{ticket.id}} is waiting in <strong>{{group.name}}</strong> for someone to take it.</p>
            <p><strong>{{ticket.subject}}</strong><br>Requester: {{requester.name}}<br>Priority: {{ticket.priority}}</p>
            """ + AgentLink),
        new(AgentNewInGroup, "New ticket in your group", "Sent to a group's members when a new ticket arrives in the group unassigned.",
            NotificationAudience.Agent, Automatic: true,
            "New in {{group.name}}: {{ticket.subject}}",
            """
            <p>A new ticket arrived in <strong>{{group.name}}</strong>.</p>
            <p><strong>{{ticket.id}}: {{ticket.subject}}</strong><br>Requester: {{requester.name}}<br>Priority: {{ticket.priority}}</p>
            {{ticket.description}}
            """ + AgentLink,
            DefaultEmail: false),
        new(AgentRequesterReplied, "Requester replied", "Sent to the assigned agent when the requester replies.",
            NotificationAudience.Agent, Automatic: true,
            "{{message.author}} replied: {{ticket.subject}}",
            """
            <p>{{message.author}} replied on <strong>{{ticket.id}}: {{ticket.subject}}</strong></p>
            {{message.body}}
            """ + AgentLink),
        new(AgentMentioned, "Mentioned in a note", "Sent to agents mentioned in a note.",
            NotificationAudience.Agent, Automatic: true,
            "{{actor.name}} mentioned you: {{ticket.subject}}",
            """
            <p>{{actor.name}} mentioned you in a note on <strong>{{ticket.id}}: {{ticket.subject}}</strong></p>
            {{message.body}}
            """ + AgentLink),
    ];

    public static NotificationEvent? Find(string key) => All.FirstOrDefault(e => e.Key == key);

    public static NotificationEvent Get(string key) => Find(key) ?? throw new ArgumentException($"Unknown notification event {key}.", nameof(key));

    public static IReadOnlyList<PlaceholderInfo> Placeholders { get; } =
    [
        new("ticket.id", "Ticket number, such as INC-1042"),
        new("ticket.subject", "Ticket subject"),
        new("ticket.status", "Status, as requesters see it"),
        new("ticket.priority", "Priority"),
        new("ticket.type", "Incident or service request"),
        new("ticket.url", "Link to the ticket for agents"),
        new("ticket.portal_url", "Link to the request in the portal"),
        new("ticket.description", "The original description", IsHtml: true),
        new("requester.name", "Requester's name"),
        new("requester.first_name", "Requester's first name"),
        new("requester.email", "Requester's email address"),
        new("agent.name", "Assigned agent, or the agent who wrote the message"),
        new("agent.signature", "That agent's signature", IsHtml: true),
        new("group.name", "Assigned group"),
        new("actor.name", "Who made the change"),
        new("message.body", "The reply, note or forward text", IsHtml: true),
        new("message.author", "Who wrote the message"),
        new("organization.name", "Your organization's name"),
        new("recipient.name", "Name of the person receiving this email"),
    ];
}
