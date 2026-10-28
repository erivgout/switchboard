using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;

namespace Switchboard.Modules.Messaging.Inbound;

public sealed record EmailCommand(string Name, string Argument);

public sealed record CommandParse(IReadOnlyList<EmailCommand> Commands, string RemainingText);

public sealed record CommandOutcome(IReadOnlyList<string> Applied, IReadOnlyList<string> Problems);

/// <summary>
/// Email commands for agents (FR-CH-4): lines at the top of a reply such as <c>#status Resolved</c>,
/// <c>#priority high</c>, <c>#assign me</c>, <c>#group Network</c>, <c>#tag vip</c> or <c>#type request</c>.
/// They run as the agent through <see cref="ITicketService"/>, so the agent's permissions apply.
/// The pipeline only calls this for agents whose mail passed sender authentication (SR-EML-2).
/// </summary>
public sealed partial class EmailCommands(SwitchboardDbContext db, ITicketService tickets)
{
    public static readonly IReadOnlyList<(string Name, string Example, string Description)> Help =
    [
        ("status", "#status Resolved", "Change the status (by name)"),
        ("priority", "#priority high", "Low, medium, high or urgent"),
        ("assign", "#assign me", "me, none, or an agent's email address"),
        ("group", "#group Network", "A group name, or none"),
        ("tag", "#tag vip", "Add tags, separated by commas"),
        ("type", "#type request", "incident or request"),
    ];

    public static CommandParse Parse(string plainText)
    {
        var lines = (plainText ?? "").Replace("\r", "").Split('\n');
        var commands = new List<EmailCommand>();
        var index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0)
        {
            index++;
        }
        for (; index < lines.Length && commands.Count < 10; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var match = CommandLine().Match(line);
            if (!match.Success)
            {
                break;
            }
            commands.Add(new EmailCommand(match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value.Trim()));
        }
        var remaining = commands.Count == 0 ? plainText ?? "" : string.Join("\n", lines.Skip(index)).Trim();
        return new CommandParse(commands, remaining);
    }

    public async Task<CommandOutcome> ApplyAsync(Guid ticketId, User agent, IReadOnlyList<EmailCommand> commands, CancellationToken ct)
    {
        var applied = new List<string>();
        var problems = new List<string>();
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Tags).FirstAsync(t => t.Id == ticketId, ct);
        var patch = new TicketPatch();
        var tags = ticket.Tags.Select(t => t.Tag).ToList();
        var tagsChanged = false;

        foreach (var command in commands)
        {
            var arg = command.Argument;
            switch (command.Name)
            {
                case "status":
                    var status = await db.TicketStatuses.AsNoTracking()
                        .Where(s => s.WorkspaceId == ticket.WorkspaceId)
                        .FirstOrDefaultAsync(s => s.Name == arg, ct);
                    if (status is null)
                    {
                        problems.Add($"#status {arg}: there's no status called {arg}.");
                        break;
                    }
                    patch = patch with { StatusId = status.Id };
                    applied.Add($"status set to {status.Name}");
                    break;
                case "priority":
                    if (Enum.TryParse<Priority>(arg, ignoreCase: true, out var priority) && Enum.IsDefined(priority))
                    {
                        patch = patch with { Priority = priority };
                        applied.Add($"priority set to {priority}");
                    }
                    else
                    {
                        problems.Add($"#priority {arg}: use low, medium, high or urgent.");
                    }
                    break;
                case "assign":
                    if (arg.Equals("me", StringComparison.OrdinalIgnoreCase))
                    {
                        patch = patch with { AgentId = agent.Id };
                        applied.Add($"assigned to {agent.DisplayName}");
                    }
                    else if (arg.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        patch = patch with { AgentId = (Guid?)null };
                        applied.Add("unassigned");
                    }
                    else
                    {
                        var normalized = arg.Trim().ToUpperInvariant();
                        var target = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedEmail == normalized && u.IsActive && (u.IsAgent || u.IsOrgAdmin), ct);
                        if (target is null)
                        {
                            problems.Add($"#assign {arg}: no active agent has that email address.");
                            break;
                        }
                        patch = patch with { AgentId = target.Id };
                        applied.Add($"assigned to {target.DisplayName}");
                    }
                    break;
                case "group":
                    if (arg.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        patch = patch with { GroupId = (Guid?)null };
                        applied.Add("group cleared");
                        break;
                    }
                    var group = await db.AgentGroups.AsNoTracking().FirstOrDefaultAsync(g => g.WorkspaceId == ticket.WorkspaceId && g.Name == arg, ct);
                    if (group is null)
                    {
                        problems.Add($"#group {arg}: there's no group called {arg}.");
                        break;
                    }
                    patch = patch with { GroupId = group.Id };
                    applied.Add($"group set to {group.Name}");
                    break;
                case "tag":
                    foreach (var tag in arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        {
                            tags.Add(tag);
                            tagsChanged = true;
                        }
                    }
                    applied.Add($"tagged {arg}");
                    break;
                case "type":
                    TicketType? type = arg.ToLowerInvariant() switch
                    {
                        "incident" => TicketType.Incident,
                        "request" or "service request" or "servicerequest" => TicketType.ServiceRequest,
                        _ => null,
                    };
                    if (type is null)
                    {
                        problems.Add($"#type {arg}: use incident or request.");
                        break;
                    }
                    patch = patch with { Type = type.Value };
                    applied.Add($"type set to {(type == TicketType.Incident ? "incident" : "service request")}");
                    break;
            }
        }
        if (tagsChanged)
        {
            patch = patch with { Tags = tags.ToArray() };
        }
        if (patch.IsEmpty)
        {
            return new CommandOutcome(applied, problems);
        }

        try
        {
            await tickets.UpdateAsync(ticketId, patch, ct: ct);
        }
        catch (Exception ex) when (ex is ValidationException or ForbiddenException or ConflictException or NotFoundException)
        {
            problems.Add($"The changes weren't applied: {ex.Message}");
            return new CommandOutcome([], problems);
        }
        return new CommandOutcome(applied, problems);
    }

    [GeneratedRegex(@"^#(status|priority|assign|group|tag|type)\s*:?\s+(.{1,200})$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex CommandLine();
}
