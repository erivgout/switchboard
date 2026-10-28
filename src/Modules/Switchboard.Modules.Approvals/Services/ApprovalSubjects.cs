using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Approvals.Domain;

namespace Switchboard.Modules.Approvals.Services;

/// <summary>What the approvals UI shows about the record being approved.</summary>
/// <param name="DisplayId">Short reference, e.g. <c>SR-1042</c>.</param>
/// <param name="Href">Link to the record for the current viewer, or null when they can't open it.</param>
public sealed record ApprovalSubjectInfo(string? DisplayId, string? Href);

/// <summary>
/// Access rules for approvals on one record type. Modules that request approvals on their own records
/// register one (<c>services.AddScoped&lt;IApprovalSubjectPolicy, MyPolicy&gt;()</c>); tickets are built in.
/// </summary>
public interface IApprovalSubjectPolicy
{
    string SubjectType { get; }

    /// <summary>The current actor can see the record, and so its approvals.</summary>
    Task<bool> CanViewAsync(Guid subjectId, CancellationToken ct);

    /// <summary>The current actor can request, cancel and send reminders for approvals on the record.</summary>
    Task<bool> CanManageAsync(Guid subjectId, CancellationToken ct);

    Task<ApprovalSubjectInfo> DescribeAsync(Guid subjectId, CancellationToken ct);
}

/// <summary>Approvals on tickets: visible to whoever can see the ticket; managed by agents who can edit it.</summary>
internal sealed class TicketApprovalSubjectPolicy(SwitchboardDbContext db, IPermissionService permissions) : IApprovalSubjectPolicy
{
    public string SubjectType => ApprovalSubjectTypes.Ticket;

    public async Task<bool> CanViewAsync(Guid subjectId, CancellationToken ct) =>
        await (await permissions.AgentVisibleTicketsAsync(ct)).AnyAsync(t => t.Id == subjectId, ct)
        || await (await permissions.RequesterVisibleTicketsAsync(ct)).AnyAsync(t => t.Id == subjectId, ct);

    public async Task<bool> CanManageAsync(Guid subjectId, CancellationToken ct)
    {
        var ticket = await (await permissions.AgentVisibleTicketsAsync(ct))
            .Where(t => t.Id == subjectId && t.DeletedAt == null)
            .Select(t => new { t.WorkspaceId })
            .FirstOrDefaultAsync(ct);
        return ticket is not null && await permissions.HasAsync(Privileges.TicketsEdit, ticket.WorkspaceId, ct);
    }

    public async Task<ApprovalSubjectInfo> DescribeAsync(Guid subjectId, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking()
            .Where(t => t.Id == subjectId)
            .Select(t => new { t.Type, t.Number })
            .FirstOrDefaultAsync(ct);
        if (ticket is null)
        {
            return new ApprovalSubjectInfo(null, null);
        }
        var displayId = Core.Domain.Ticket.FormatDisplayId(ticket.Type, ticket.Number);
        if (await (await permissions.AgentVisibleTicketsAsync(ct)).AnyAsync(t => t.Id == subjectId, ct))
        {
            return new ApprovalSubjectInfo(displayId, Switchboard.Core.Tickets.TicketUrls.Agent(ticket.Number));
        }
        if (await (await permissions.RequesterVisibleTicketsAsync(ct)).AnyAsync(t => t.Id == subjectId, ct))
        {
            return new ApprovalSubjectInfo(displayId, Switchboard.Core.Tickets.TicketUrls.Requester(ticket.Number));
        }
        return new ApprovalSubjectInfo(displayId, null);
    }
}
