using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;

namespace Switchboard.Modules.Messaging.Notifications;

/// <summary>Where tickets live in the agent console and the portal. Owned by other modules; kept in one place here.</summary>
public static class TicketLinks
{
    public static string Agent(int number) => Switchboard.Core.Tickets.TicketUrls.Agent(number);
    public static string Portal(int number) => Switchboard.Core.Tickets.TicketUrls.Requester(number);
}

/// <summary>Everything a ticket email needs, loaded once.</summary>
public sealed record TicketFacts(
    Ticket Ticket,
    TicketStatus Status,
    User Requester,
    User? Agent,
    AgentGroup? Group,
    string OrganizationName,
    string BaseUrl,
    bool ShowAgentNames)
{
    public string AgentUrl => BaseUrl + TicketLinks.Agent(Ticket.Number);
    public string PortalUrl => BaseUrl + TicketLinks.Portal(Ticket.Number);
}

/// <summary>The message an email carries (a reply, note or forward).</summary>
public sealed record MessageFacts(string BodyHtml, string? AuthorName, User? Author);

public sealed class TicketFactsLoader(SwitchboardDbContext db, ISettingsService settings, IOptions<SwitchboardOptions> options)
{
    public async Task<TicketFacts?> LoadAsync(Guid ticketId, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null || ticket.PurgedAt is not null)
        {
            return null;
        }
        var status = await db.TicketStatuses.AsNoTracking().FirstAsync(s => s.Id == ticket.StatusId, ct);
        var requester = await db.Users.AsNoTracking().FirstAsync(u => u.Id == ticket.RequesterId, ct);
        var agent = ticket.AgentId is { } a ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == a, ct) : null;
        var group = ticket.GroupId is { } g ? await db.AgentGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == g, ct) : null;
        var org = await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).Select(o => new { o.Name, o.BaseUrl }).FirstOrDefaultAsync(ct);
        var baseUrl = (org?.BaseUrl is { Length: > 0 } b ? b : options.Value.BaseUrl).TrimEnd('/');
        var showAgents = await settings.GetAsync(SettingKeys.ShowAgentNamesToRequesters, true, ticket.WorkspaceId, ct);
        return new TicketFacts(ticket, status, requester, agent, group, org?.Name ?? "Switchboard", baseUrl, showAgents);
    }

    public async Task<string> OrganizationNameAsync(CancellationToken ct) =>
        await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).Select(o => o.Name).FirstOrDefaultAsync(ct) ?? "Switchboard";

    /// <summary>Placeholder values for a ticket email (see <see cref="NotificationCatalog.Placeholders"/>).</summary>
    public static Dictionary<string, TemplateValue> Values(TicketFacts facts, MessageFacts? message, string? actorName, string? recipientName, IHtmlContent html)
    {
        var ticket = facts.Ticket;
        var writer = message?.Author ?? facts.Agent;
        var forRequester = recipientName == facts.Requester.DisplayName;
        return new Dictionary<string, TemplateValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["ticket.id"] = TemplateValue.Text(ticket.DisplayId),
            ["ticket.subject"] = TemplateValue.Text(ticket.Subject),
            ["ticket.status"] = TemplateValue.Text(facts.Status.RequesterLabel is { Length: > 0 } label ? label : facts.Status.Name),
            ["ticket.priority"] = TemplateValue.Text(ticket.Priority.ToString()),
            ["ticket.type"] = TemplateValue.Text(Labels.Type(ticket.Type)),
            ["ticket.url"] = TemplateValue.Text(facts.AgentUrl),
            ["ticket.portal_url"] = TemplateValue.Text(facts.PortalUrl),
            ["ticket.description"] = TemplateValue.Html(html.Sanitize(ticket.DescriptionHtml)),
            ["requester.name"] = TemplateValue.Text(facts.Requester.DisplayName),
            ["requester.first_name"] = TemplateValue.Text(FirstName(facts.Requester.DisplayName)),
            ["requester.email"] = TemplateValue.Text(facts.Requester.Email),
            ["agent.name"] = TemplateValue.Text(writer?.DisplayName ?? ""),
            ["agent.signature"] = TemplateValue.Html(Signature(writer, html)),
            ["group.name"] = TemplateValue.Text(facts.Group?.Name ?? ""),
            ["actor.name"] = TemplateValue.Text(actorName ?? "Someone"),
            ["message.body"] = TemplateValue.Html(message is null ? "" : html.Sanitize(message.BodyHtml)),
            ["message.author"] = TemplateValue.Text(message?.AuthorName ?? (forRequester ? "The service desk" : "")),
            ["organization.name"] = TemplateValue.Text(facts.OrganizationName),
            ["recipient.name"] = TemplateValue.Text(recipientName ?? ""),
        };
    }

    public static string FirstName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Contains('@'))
        {
            return "there";
        }
        return trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    /// <summary>The profile stores a plain-text signature; older data may hold sanitized HTML.</summary>
    public static string Signature(User? agent, IHtmlContent html)
    {
        if (agent?.Signature is not { Length: > 0 } raw)
        {
            return "";
        }
        var body = raw.Contains('<') ? html.Sanitize(raw) : html.FromPlainText(raw);
        return body.Length == 0 ? "" : $"<div style=\"margin-top:16px;color:#5b6763\">{body}</div>";
    }
}
