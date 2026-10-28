using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;

namespace Switchboard.Core.Tickets;

/// <summary>
/// Validates a status transition before it is saved (closure rules, FR-TKT-25). Return one message per
/// unmet condition; any message blocks the transition. Modules may register more guards.
/// </summary>
public interface ITicketTransitionGuard
{
    Task<IReadOnlyList<string>> ValidateAsync(Ticket ticket, TicketStatus target, CancellationToken ct);
}

/// <summary>Built-in rules: required-on-resolve fields, and open children blocking a parent's closure.</summary>
internal sealed class CoreTicketTransitionGuard(SwitchboardDbContext db, ICustomFieldService fields, ISettingsService settings) : ITicketTransitionGuard
{
    public async Task<IReadOnlyList<string>> ValidateAsync(Ticket ticket, TicketStatus target, CancellationToken ct)
    {
        var errors = new List<string>();
        if (target.Category is not (StatusCategory.Resolved or StatusCategory.Closed))
        {
            return errors;
        }

        errors.AddRange(await fields.MissingRequiredAsync("ticket", ticket.WorkspaceId, ticket.CustomFieldsJson, RequiredMoment.Resolve, ct));

        if (await settings.GetAsync(SettingKeys.BlockParentCloseWithOpenChildren, true, ticket.WorkspaceId, ct))
        {
            var openChildren = await db.Tickets
                .Where(c => c.ParentId == ticket.Id && c.DeletedAt == null && !c.IsSpam && c.MergedIntoId == null)
                .Where(c => c.Status!.Category == StatusCategory.Open || c.Status.Category == StatusCategory.Pending)
                .Select(c => new { c.Type, c.Number })
                .ToListAsync(ct);
            if (openChildren.Count > 0)
            {
                errors.Add($"Resolve the child tickets first: {string.Join(", ", openChildren.Select(c => Ticket.FormatDisplayId(c.Type, c.Number)))}.");
            }
        }

        return errors;
    }
}
