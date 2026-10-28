using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Engine;

namespace Switchboard.Modules.Automation.Closure;

/// <summary>
/// Admin-configured closure rules (FR-TKT-25). Runs inside the ticket service's status change, on the
/// same unit of work, so it sees changes made in the same save (a reply sent with "resolve").
/// Automations and the system are exempt, as Core runs guards only for people.
/// </summary>
internal sealed class ClosureRuleGuard(SwitchboardDbContext db, ICustomFieldService fields) : ITicketTransitionGuard
{
    public async Task<IReadOnlyList<string>> ValidateAsync(Ticket ticket, TicketStatus target, CancellationToken ct)
    {
        var errors = new List<string>();
        if (target.Category is not (StatusCategory.Resolved or StatusCategory.Closed))
        {
            return errors;
        }
        var rules = await db.Set<AutClosureRule>().AsNoTracking()
            .Where(r => r.WorkspaceId == ticket.WorkspaceId && r.IsEnabled && (r.TicketType == null || r.TicketType == ticket.Type))
            .OrderBy(r => r.CreatedAt).ToListAsync(ct);
        if (rules.Count == 0)
        {
            return errors;
        }

        var verb = target.Category == StatusCategory.Closed ? "closing" : "resolving";
        IReadOnlyList<CustomFieldDefinition>? defs = null;
        foreach (var rule in rules)
        {
            string? problem = null;
            switch (rule.Kind)
            {
                case ClosureRuleKind.AllTasksCompleted:
                    var open = await db.TicketTasks.AsNoTracking().CountAsync(t => t.TicketId == ticket.Id && t.Status != TicketTaskStatus.Completed, ct);
                    if (open > 0)
                    {
                        problem = $"Complete every task before {verb}. {open} {(open == 1 ? "task is" : "tasks are")} still open.";
                    }
                    break;
                case ClosureRuleKind.PublicReplySent:
                    var replied = db.TicketConversations.Local.Any(c => c.TicketId == ticket.Id && c.Kind == ConversationKind.AgentReply && c.DeletedAt == null)
                        || await db.TicketConversations.AsNoTracking().AnyAsync(c => c.TicketId == ticket.Id && c.Kind == ConversationKind.AgentReply && c.DeletedAt == null, ct);
                    if (!replied)
                    {
                        problem = $"Send the requester at least one reply before {verb}.";
                    }
                    break;
                case ClosureRuleKind.CategorySet:
                    if (ticket.CategoryId is null)
                    {
                        problem = $"Set a category before {verb}.";
                    }
                    break;
                case ClosureRuleKind.CustomFieldSet when rule.FieldKey is { } key:
                    defs ??= await fields.GetDefinitionsAsync("ticket", ticket.WorkspaceId, ct);
                    var label = defs.FirstOrDefault(d => d.Key == key)?.Label ?? key;
                    var values = CustomFieldService.Parse(ticket.CustomFieldsJson);
                    if (!values.TryGetPropertyValue(key, out var node) || IsEmpty(node))
                    {
                        problem = $"Fill in {label} before {verb}.";
                    }
                    break;
            }
            if (problem is not null)
            {
                errors.Add(string.IsNullOrWhiteSpace(rule.Message) ? problem : rule.Message!);
            }
        }
        return errors;
    }

    private static bool IsEmpty(System.Text.Json.Nodes.JsonNode? node) => node switch
    {
        null => true,
        System.Text.Json.Nodes.JsonArray a => a.Count == 0,
        System.Text.Json.Nodes.JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s),
        System.Text.Json.Nodes.JsonValue v when v.TryGetValue<bool>(out var b) => !b,
        _ => false,
    };
}

public sealed record ClosureRuleInput(ClosureRuleKind Kind, TicketType? TicketType, string? FieldKey, string? Message);

public sealed class ClosureRuleService(SwitchboardDbContext db, IPermissionService permissions, IAuditLog audit, ICustomFieldService fields)
{
    public async Task<List<AutClosureRule>> ListAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await permissions.DemandAsync(AlertService.ManagePrivilege, workspaceId, ct);
        return await db.Set<AutClosureRule>().AsNoTracking().Where(r => r.WorkspaceId == workspaceId).OrderBy(r => r.CreatedAt).ToListAsync(ct);
    }

    public async Task<AutClosureRule> AddAsync(Guid workspaceId, ClosureRuleInput input, CancellationToken ct = default)
    {
        await permissions.DemandAsync(AlertService.ManagePrivilege, workspaceId, ct);
        if (!Enum.IsDefined(input.Kind))
        {
            throw new ValidationException("kind", "Choose what must be true before resolving.");
        }
        string? key = null;
        if (input.Kind == ClosureRuleKind.CustomFieldSet)
        {
            key = input.FieldKey?.Trim();
            var defs = await fields.GetDefinitionsAsync("ticket", workspaceId, ct);
            if (key is null || defs.All(d => d.Key != key))
            {
                throw new ValidationException("fieldKey", "Choose the field that must be filled in.");
            }
        }
        var message = string.IsNullOrWhiteSpace(input.Message) ? null : input.Message.Trim();
        if (message?.Length > 500)
        {
            throw new ValidationException("message", "Keep the message under 500 characters.");
        }
        if (await db.Set<AutClosureRule>().AnyAsync(r => r.WorkspaceId == workspaceId && r.Kind == input.Kind && r.TicketType == input.TicketType && r.FieldKey == key, ct))
        {
            throw new ValidationException("kind", "That rule already exists for this ticket type.");
        }
        var rule = new AutClosureRule { WorkspaceId = workspaceId, Kind = input.Kind, TicketType = input.TicketType, FieldKey = key, Message = message };
        db.Set<AutClosureRule>().Add(rule);
        audit.Record("automation.closure_rule_added", "closure_rule", rule.Id.ToString(), new { rule.Kind, rule.TicketType, rule.FieldKey });
        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        var rule = await LoadAsync(id, ct);
        rule.IsEnabled = enabled;
        audit.Record(enabled ? "automation.closure_rule_enabled" : "automation.closure_rule_disabled", "closure_rule", rule.Id.ToString(), new { rule.Kind, rule.TicketType });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var rule = await LoadAsync(id, ct);
        db.Set<AutClosureRule>().Remove(rule);
        audit.Record("automation.closure_rule_deleted", "closure_rule", rule.Id.ToString(), new { rule.Kind, rule.TicketType, rule.FieldKey });
        await db.SaveChangesAsync(ct);
    }

    private async Task<AutClosureRule> LoadAsync(Guid id, CancellationToken ct)
    {
        var rule = await db.Set<AutClosureRule>().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException();
        await permissions.DemandAsync(AlertService.ManagePrivilege, rule.WorkspaceId, ct);
        return rule;
    }

    public static string Describe(ClosureRuleKind kind, string? fieldLabel) => kind switch
    {
        ClosureRuleKind.AllTasksCompleted => "All tasks are completed",
        ClosureRuleKind.PublicReplySent => "The requester got at least one reply",
        ClosureRuleKind.CategorySet => "A category is set",
        ClosureRuleKind.CustomFieldSet => $"{fieldLabel ?? "A field"} is filled in",
        _ => kind.ToString(),
    };
}
