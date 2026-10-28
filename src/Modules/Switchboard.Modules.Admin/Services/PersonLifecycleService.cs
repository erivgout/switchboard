using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Admin.Data;

namespace Switchboard.Modules.Admin.Services;

public enum OpenWorkAction
{
    /// <summary>Leave the tickets assigned and tag them so someone picks them up.</summary>
    Flag = 0,
    ReassignToGroup = 1,
    ReassignToAgent = 2,
}

/// <summary>What happens to a person's open tickets and tasks when they stop being an assignee (SR-DEL-3).</summary>
public sealed record OpenWorkPlan(OpenWorkAction Action, Guid? GroupId = null, Guid? AgentId = null);

public sealed record OpenWorkItem(Guid Id, string DisplayId, string Subject, string Status, StatusCategory Category, Guid WorkspaceId, string? Group);

public sealed record OpenWork(IReadOnlyList<OpenWorkItem> Tickets, int OpenTasks);

/// <summary>
/// Account lifecycle (FR-IAM-2, FR-IAM-3): deactivate, reactivate, convert, merge, forget and export.
/// Every path keeps at least one active organization admin (INV-ID-1).
/// </summary>
public sealed class PersonLifecycleService(
    SwitchboardDbContext db,
    AdminAccess access,
    PeopleService people,
    ITicketService tickets,
    IAuditLog audit,
    TimeProvider time)
{
    /// <summary>Tag added to tickets left with a deactivated assignee.</summary>
    public const string FlagTag = "owner-inactive";

    public async Task<OpenWork> GetOpenWorkAsync(Guid userId, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersView, ct: ct);
        var items = await people.OpenAssignedTickets(userId).AsNoTracking()
            .OrderBy(t => t.Number)
            .Select(t => new { t.Id, t.Type, t.Number, t.Subject, Status = t.Status!.Name, t.Status.Category, t.WorkspaceId, Group = t.Group!.Name })
            .ToListAsync(ct);
        var tasks = await OpenTasks(userId).CountAsync(ct);
        return new OpenWork(items.Select(t => new OpenWorkItem(t.Id, Ticket.FormatDisplayId(t.Type, t.Number), t.Subject, t.Status, t.Category, t.WorkspaceId, t.Group)).ToList(), tasks);
    }

    // ---------------------------------------------------------------- deactivate and reactivate

    /// <summary>
    /// Deactivates an account (INV-ID-4): hands off open work, ends every session by rotating the security
    /// stamp (SR-AUTHN-5), and revokes API keys. All of it commits together or not at all.
    /// </summary>
    public async Task DeactivateAsync(Guid userId, OpenWorkPlan plan, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, ct: ct);
        if (access.ActorUserId == userId)
        {
            throw new ValidationException("User", "You can't deactivate your own account. Ask another admin.");
        }
        await access.EnsureCanManageUserAsync(userId, ct);

        await db.InTransactionAsync(async () =>
        {
            await db.LockOrgAdminsAsync(ct);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException();
            if (!user.IsActive)
            {
                return;
            }
            if (user.IsOrgAdmin)
            {
                await db.EnsureAnotherOrgAdminAsync(userId, ct);
            }
            var handedOff = await HandOffWorkAsync(user, plan, ct);

            user.IsActive = false;
            user.DeactivatedAt = time.GetUtcNow();
            user.Availability = AgentAvailability.Offline;
            var revoked = await EndSessionsAsync(user, ct);
            audit.Record("users.deactivated", "user", userId.ToString(), new
            {
                openTickets = handedOff,
                plan = plan.Action.ToString(),
                groupId = plan.GroupId,
                agentId = plan.AgentId,
                apiKeysRevoked = revoked,
            });
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task ReactivateAsync(Guid userId, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, ct: ct);
        await access.EnsureCanManageUserAsync(userId, ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException();
        if (user.IsForgotten)
        {
            throw new ValidationException("User", "This person's data was erased. Create a new account for them instead.");
        }
        if (await db.Set<AdmUserMerge>().AnyAsync(m => m.SourceUserId == userId, ct))
        {
            throw new ValidationException("User", "This account was merged into another one and can't be reactivated.");
        }
        if (user.IsActive)
        {
            return;
        }
        user.IsActive = true;
        user.DeactivatedAt = null;
        audit.Record("users.reactivated", "user", userId.ToString());
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- convert

    public async Task ConvertToAgentAsync(Guid userId, NewAgentAccess agent, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, agent.WorkspaceId, ct);
        await access.EnsureNotSelfAsync(userId, "You can't change your own access. Ask another admin to do it.", ct);
        var role = await people.RoleInWorkspaceAsync(agent.WorkspaceId, agent.RoleId, ct);
        await access.EnsureCanAssignRoleAsync(role, agent.WorkspaceId, ct);
        if (!Enum.IsDefined(agent.Scope))
        {
            throw new ValidationException("Scope", "Choose which tickets this agent can see.");
        }

        var user = await db.Users.Include(u => u.Memberships).FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException();
        if (user.IsAgent || user.IsOrgAdmin)
        {
            throw new ValidationException("User", $"{user.DisplayName} is already an agent.");
        }
        if (!user.IsActive)
        {
            throw new ValidationException("User", $"Reactivate {user.DisplayName} before making them an agent.");
        }
        user.IsAgent = true;
        if (user.Memberships.All(m => m.WorkspaceId != agent.WorkspaceId))
        {
            db.WorkspaceMemberships.Add(new WorkspaceMembership { UserId = userId, WorkspaceId = agent.WorkspaceId, RoleId = role.Id, TicketScope = agent.Scope });
        }
        audit.Record("users.converted_to_agent", "user", userId.ToString(), new { workspaceId = agent.WorkspaceId, role = role.Name, scope = agent.Scope });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes every agent role and group; open work is handed off first.</summary>
    public async Task ConvertToRequesterAsync(Guid userId, OpenWorkPlan plan, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, ct: ct);
        if (access.ActorUserId == userId)
        {
            throw new ValidationException("User", "You can't convert your own account. Ask another admin.");
        }
        await access.EnsureCanManageUserAsync(userId, ct);

        await db.InTransactionAsync(async () =>
        {
            var user = await db.Users.Include(u => u.Memberships).ThenInclude(m => m.Role).Include(u => u.GroupMemberships)
                .FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException();
            if (user.IsOrgAdmin)
            {
                throw new ValidationException("User", $"{user.DisplayName} is an organization admin. Remove that first.");
            }
            if (!user.IsAgent)
            {
                return;
            }
            foreach (var m in user.Memberships)
            {
                await access.DemandAsync(Privileges.UsersManage, m.WorkspaceId, ct);
                await access.EnsureCanAssignRoleAsync(m.Role!, m.WorkspaceId, ct);
            }
            var handedOff = await HandOffWorkAsync(user, plan, ct);
            db.WorkspaceMemberships.RemoveRange(user.Memberships);
            db.AgentGroupMembers.RemoveRange(user.GroupMemberships);
            user.IsAgent = false;
            user.Availability = AgentAvailability.Offline;
            audit.Record("users.converted_to_requester", "user", userId.ToString(), new { openTickets = handedOff, plan = plan.Action.ToString() });
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    // ---------------------------------------------------------------- merge

    /// <summary>
    /// Merges a duplicate requester into <paramref name="survivorId"/> (FR-IAM-2). Tickets, shares, group
    /// memberships, messages and addresses move to the survivor; the duplicate stays as an inactive record
    /// so every reference remains valid (INV-ID-3).
    /// </summary>
    public async Task MergeAsync(Guid sourceId, Guid survivorId, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, ct: ct);
        if (sourceId == survivorId)
        {
            throw new ValidationException("SurvivorId", "Choose a different person to keep.");
        }
        await access.EnsureCanManageUserAsync(sourceId, ct);
        await access.EnsureCanManageUserAsync(survivorId, ct);

        await db.InTransactionAsync(async () =>
        {
            await db.LockEmailsAsync(ct);
            var source = await db.Users.FirstOrDefaultAsync(u => u.Id == sourceId, ct) ?? throw new NotFoundException();
            var survivor = await db.Users.FirstOrDefaultAsync(u => u.Id == survivorId, ct)
                ?? throw new ValidationException("SurvivorId", "That person no longer exists.");
            foreach (var u in new[] { source, survivor })
            {
                if (u.IsAgent || u.IsOrgAdmin)
                {
                    throw new ValidationException("SurvivorId", $"{u.DisplayName} is an agent. Only requesters can be merged.");
                }
                if (u.IsForgotten)
                {
                    throw new ValidationException("SurvivorId", $"{u.DisplayName}'s data was erased, so they can't be merged.");
                }
            }
            if (!survivor.IsActive)
            {
                throw new ValidationException("SurvivorId", $"Reactivate {survivor.DisplayName} before merging into them.");
            }
            if (await db.Set<AdmUserMerge>().AnyAsync(m => m.SourceUserId == sourceId, ct))
            {
                throw new ValidationException("SurvivorId", $"{source.DisplayName} was already merged.");
            }

            var moved = await MoveTicketsAsync(source, survivor, ct);
            await MoveMembershipsAsync(sourceId, survivorId, ct);
            await MoveReferencesAsync(sourceId, survivor, ct);
            await MoveAddressesAsync(source, survivor, ct);
            FillBlanks(source, survivor);

            source.IsActive = false;
            source.DeactivatedAt = time.GetUtcNow();
            source.PasswordHash = null;
            await EndSessionsAsync(source, ct);
            db.Set<AdmUserMerge>().Add(new AdmUserMerge { SourceUserId = sourceId, SurvivorUserId = survivorId, MergedById = access.ActorUserId, MergedAt = time.GetUtcNow() });
            audit.Record("users.merged", "user", survivorId.ToString(), new { sourceUserId = sourceId, survivorUserId = survivorId, tickets = moved });
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    private async Task<int> MoveTicketsAsync(User source, User survivor, CancellationToken ct)
    {
        var sourceId = source.Id;
        var affected = await db.Tickets.Include(t => t.Shares)
            .Where(t => t.RequesterId == sourceId || t.RequestedForId == sourceId || t.Shares.Any(s => s.UserId == sourceId))
            .ToListAsync(ct);
        foreach (var t in affected)
        {
            var changes = new List<FieldChange>();
            if (t.RequesterId == sourceId)
            {
                t.RequesterId = survivor.Id;
                changes.Add(new FieldChange("Requester", source.DisplayName, survivor.DisplayName));
            }
            if (t.RequestedForId == sourceId)
            {
                t.RequestedForId = survivor.Id == t.RequesterId ? null : survivor.Id;
                changes.Add(new FieldChange("Requested for", source.DisplayName, t.RequestedForId is null ? null : survivor.DisplayName));
            }
            var share = t.Shares.FirstOrDefault(s => s.UserId == sourceId);
            if (share is not null)
            {
                t.Shares.Remove(share);
                if (t.Shares.All(s => s.UserId != survivor.Id) && t.RequesterId != survivor.Id)
                {
                    t.Shares.Add(new TicketShare { TicketId = t.Id, UserId = survivor.Id, SharedById = share.SharedById, CreatedAt = share.CreatedAt });
                }
            }
            if (changes.Count > 0)
            {
                db.Activity(t, "updated", $"Requester merged into {survivor.DisplayName}", changes);
                db.Raise(new TicketUpdated(t.Id, t.WorkspaceId, changes));
            }
        }

        var messages = await db.TicketConversations.Where(c => c.AuthorId == sourceId).ToListAsync(ct);
        foreach (var c in messages)
        {
            c.AuthorId = survivor.Id;
            db.Activity(c, "updated", $"Message author merged into {survivor.DisplayName}", [new FieldChange("Author", source.DisplayName, survivor.DisplayName)]);
        }
        return affected.Count(t => t.RequesterId == survivor.Id || t.RequestedForId == survivor.Id);
    }

    private async Task MoveMembershipsAsync(Guid sourceId, Guid survivorId, CancellationToken ct)
    {
        var survivorGroups = await db.RequesterGroupMembers.Where(m => m.UserId == survivorId).Select(m => m.GroupId).ToListAsync(ct);
        foreach (var m in await db.RequesterGroupMembers.Where(m => m.UserId == sourceId).ToListAsync(ct))
        {
            db.RequesterGroupMembers.Remove(m);
            if (!survivorGroups.Contains(m.GroupId))
            {
                db.RequesterGroupMembers.Add(new RequesterGroupMember { GroupId = m.GroupId, UserId = survivorId });
            }
        }
    }

    private async Task MoveReferencesAsync(Guid sourceId, User survivor, CancellationToken ct)
    {
        foreach (var d in await db.Departments.Where(d => d.HeadUserId == sourceId || d.PrimeUserId == sourceId).ToListAsync(ct))
        {
            d.HeadUserId = d.HeadUserId == sourceId ? survivor.Id : d.HeadUserId;
            d.PrimeUserId = d.PrimeUserId == sourceId ? survivor.Id : d.PrimeUserId;
        }
        foreach (var report in await db.Users.Where(u => u.ManagerId == sourceId && u.Id != survivor.Id).ToListAsync(ct))
        {
            report.ManagerId = await people.ManagerChainReachesAsync(survivor.Id, report.Id, ct) ? null : survivor.Id;
        }
        foreach (var d in await db.UserDelegations.Where(d => d.UserId == sourceId || d.DelegateUserId == sourceId).ToListAsync(ct))
        {
            if (d.UserId == sourceId)
            {
                d.UserId = survivor.Id;
            }
            if (d.DelegateUserId == sourceId)
            {
                d.DelegateUserId = survivor.Id;
            }
            if (d.UserId == d.DelegateUserId)
            {
                db.UserDelegations.Remove(d);
            }
        }
    }

    /// <summary>The duplicate's addresses become the survivor's other addresses, so future email finds the survivor.</summary>
    private async Task MoveAddressesAsync(User source, User survivor, CancellationToken ct)
    {
        var have = await db.UserEmails.Where(e => e.UserId == survivor.Id).Select(e => e.NormalizedEmail).ToListAsync(ct);
        var taken = have.Append(survivor.NormalizedEmail ?? "").ToHashSet();
        // Re-point existing rows instead of deleting and re-adding them, so the unique index never sees the address twice.
        foreach (var e in await db.UserEmails.Where(e => e.UserId == source.Id).ToListAsync(ct))
        {
            if (taken.Add(e.NormalizedEmail))
            {
                e.UserId = survivor.Id;
            }
            else
            {
                db.UserEmails.Remove(e);
            }
        }
        if (source.Email is { Length: > 0 } primary && taken.Add(IUserDirectory.Normalize(primary)))
        {
            db.UserEmails.Add(new UserEmail { UserId = survivor.Id, Email = primary, NormalizedEmail = IUserDirectory.Normalize(primary) });
        }
        source.Email = null;
        source.NormalizedEmail = null;
        source.UserName = $"merged-{source.Id:N}";
        source.NormalizedUserName = source.UserName.ToUpperInvariant();
    }

    private static void FillBlanks(User source, User survivor)
    {
        survivor.PhoneNumber ??= source.PhoneNumber;
        survivor.Mobile ??= source.Mobile;
        survivor.JobTitle ??= source.JobTitle;
        survivor.DepartmentId ??= source.DepartmentId;
        survivor.LocationId ??= source.LocationId;
        survivor.Address ??= source.Address;
        survivor.IsVip |= source.IsVip;
        var target = CustomFieldService.Parse(survivor.CustomFieldsJson);
        foreach (var (key, value) in CustomFieldService.Parse(source.CustomFieldsJson))
        {
            if (!target.ContainsKey(key) && value is not null)
            {
                target[key] = value.DeepClone();
            }
        }
        survivor.CustomFieldsJson = target.ToJsonString();
    }

    // ---------------------------------------------------------------- forget

    /// <summary>
    /// Erases a person's personal data (SR-PRIV-3, INV-ID-3). The row stays as an anonymous placeholder so
    /// every ticket, message and activity reference remains valid. Requires an organization admin and the
    /// person's email address typed as confirmation. The audit log records that it happened, not what was erased.
    /// </summary>
    public async Task ForgetAsync(Guid userId, string confirmation, CancellationToken ct = default)
    {
        await access.DemandOrgAdminAsync(ct);
        if (access.ActorUserId == userId)
        {
            throw new ValidationException("Confirmation", "You can't erase your own account. Ask another organization admin.");
        }

        await db.InTransactionAsync(async () =>
        {
            await db.LockOrgAdminsAsync(ct);
            var user = await db.Users.Include(u => u.SecondaryEmails).FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException();
            if (user.IsForgotten)
            {
                return;
            }
            var expected = user.Email ?? user.DisplayName;
            if (!string.Equals((confirmation ?? "").Trim(), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new ValidationException("Confirmation", $"Type {expected} exactly to confirm.");
            }
            if (user.IsOrgAdmin)
            {
                throw new ValidationException("Confirmation", $"{user.DisplayName} is an organization admin. Remove that first.");
            }
            var open = await people.OpenAssignedTickets(userId).CountAsync(ct) + await OpenTasks(userId).CountAsync(ct);
            if (open > 0)
            {
                throw new ValidationException("Confirmation",
                    $"{user.DisplayName} still has open work assigned. Deactivate the account first and choose who takes it.");
            }

            var identifiers = new List<string> { user.DisplayName };
            identifiers.AddRange(new[] { user.Email, user.PhoneNumber, user.Mobile }.OfType<string>());
            identifiers.AddRange(user.SecondaryEmails.Select(e => e.Email));
            identifiers.AddRange(await FreeTextFieldValuesAsync(user, ct));
            identifiers = identifiers.Where(s => s.Trim().Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(s => s.Length).ToList();
            var emails = new[] { user.Email }.Concat(user.SecondaryEmails.Select(e => e.Email)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var placeholder = $"Forgotten person {user.Id.ToString("N")[^6..]}";

            await ScrubTicketsAsync(user.Id, identifiers, emails, ct);
            await ScrubActivityAsync(user.Id, identifiers, placeholder, ct);

            db.UserEmails.RemoveRange(user.SecondaryEmails);
            db.WorkspaceMemberships.RemoveRange(await db.WorkspaceMemberships.Where(m => m.UserId == userId).ToListAsync(ct));
            db.AgentGroupMembers.RemoveRange(await db.AgentGroupMembers.Where(m => m.UserId == userId).ToListAsync(ct));
            db.RequesterGroupMembers.RemoveRange(await db.RequesterGroupMembers.Where(m => m.UserId == userId).ToListAsync(ct));
            db.UserDelegations.RemoveRange(await db.UserDelegations.Where(d => d.UserId == userId || d.DelegateUserId == userId).ToListAsync(ct));
            db.UserTokens.RemoveRange(await db.UserTokens.Where(t => t.UserId == userId).ToListAsync(ct));
            db.UserLogins.RemoveRange(await db.UserLogins.Where(t => t.UserId == userId).ToListAsync(ct));
            db.UserClaims.RemoveRange(await db.UserClaims.Where(t => t.UserId == userId).ToListAsync(ct));
            foreach (var d in await db.Departments.Where(d => d.HeadUserId == userId || d.PrimeUserId == userId).ToListAsync(ct))
            {
                d.HeadUserId = d.HeadUserId == userId ? null : d.HeadUserId;
                d.PrimeUserId = d.PrimeUserId == userId ? null : d.PrimeUserId;
            }
            foreach (var key in await db.ApiKeys.Where(k => k.UserId == userId).ToListAsync(ct))
            {
                key.RevokedAt ??= time.GetUtcNow();
                key.Name = "Revoked key";
            }

            user.DisplayName = placeholder;
            user.Email = null;
            user.NormalizedEmail = null;
            user.UserName = $"forgotten-{user.Id:N}";
            user.NormalizedUserName = user.UserName.ToUpperInvariant();
            user.EmailConfirmed = false;
            user.PhoneNumber = null;
            user.PhoneNumberConfirmed = false;
            user.Mobile = null;
            user.JobTitle = null;
            user.AvatarUrl = null;
            user.Signature = null;
            user.Address = null;
            user.Background = null;
            user.TimeZone = null;
            user.OutOfOfficeMessage = null;
            user.OutOfOfficeUntil = null;
            user.Skills = [];
            user.CustomFieldsJson = "{}";
            user.PasswordHash = null;
            user.TwoFactorEnabled = false;
            user.ManagerId = null;
            user.DepartmentId = null;
            user.LocationId = null;
            user.IsVip = false;
            user.IsAgent = false;
            user.IsActive = false;
            user.IsForgotten = true;
            user.DeactivatedAt ??= time.GetUtcNow();
            user.Availability = AgentAvailability.Offline;
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.ConcurrencyStamp = Guid.NewGuid().ToString();

            // That it happened, never what was erased.
            audit.Record("users.forgotten", "user", userId.ToString());
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    /// <summary>
    /// Values of the person's single-line text fields (employee numbers, badge IDs, personal addresses). Fields carry no
    /// PII flag, so free text is treated as personal; choice fields hold shared option values and are left alone.
    /// </summary>
    private async Task<IEnumerable<string>> FreeTextFieldValuesAsync(User user, CancellationToken ct)
    {
        var keys = await db.CustomFieldDefinitions.AsNoTracking()
            .Where(d => d.EntityType == FieldEntities.User && d.FieldType == CustomFieldType.Text)
            .Select(d => d.Key).ToListAsync(ct);
        var values = CustomFieldService.Parse(user.CustomFieldsJson);
        return keys
            .Select(k => values.TryGetPropertyValue(k, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s.Trim() : null)
            .OfType<string>()
            .Where(s => s.Length >= 4);
    }

    private async Task ScrubTicketsAsync(Guid userId, IReadOnlyList<string> identifiers, IReadOnlySet<string> emails, CancellationToken ct)
    {
        var mine = await db.Tickets.Where(t => t.RequesterId == userId || t.RequestedForId == userId).Select(t => t.Id).ToListAsync(ct);
        var removedChange = new List<FieldChange> { new("Personal data", null, "removed") };

        foreach (var chunk in mine.Chunk(200))
        {
            var ids = chunk.ToList();
            foreach (var t in await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct))
            {
                var subject = Redact(t.Subject, identifiers, html: false);
                var html = Redact(t.DescriptionHtml, identifiers, html: true);
                var text = Redact(t.DescriptionText, identifiers, html: false);
                var cc = t.CcEmails.Where(e => !emails.Contains(e)).ToList();
                if (subject != t.Subject || html != t.DescriptionHtml || text != t.DescriptionText || cc.Count != t.CcEmails.Count)
                {
                    t.Subject = subject.Length == 0 ? "(removed)" : subject;
                    t.DescriptionHtml = html;
                    t.DescriptionText = text;
                    t.CcEmails = cc;
                    // An explicit hint keeps the erased values out of the activity diff.
                    db.Activity(t, "anonymized", "Personal data removed", removedChange);
                }
            }
        }

        var conversations = await db.TicketConversations
            .Where(c => c.AuthorId == userId || mine.Contains(c.TicketId))
            .ToListAsync(ct);
        foreach (var c in conversations)
        {
            var body = Redact(c.BodyHtml, identifiers, html: true);
            var text = Redact(c.BodyText, identifiers, html: false);
            var from = c.FromEmail is not null && emails.Contains(c.FromEmail) ? null : c.FromEmail;
            var to = c.To.Where(e => !emails.Contains(e)).ToList();
            var ccList = c.Cc.Where(e => !emails.Contains(e)).ToList();
            var bcc = c.Bcc.Where(e => !emails.Contains(e)).ToList();
            if (body != c.BodyHtml || text != c.BodyText || from != c.FromEmail || to.Count != c.To.Count || ccList.Count != c.Cc.Count || bcc.Count != c.Bcc.Count)
            {
                c.BodyHtml = body;
                c.BodyText = text;
                c.FromEmail = from;
                c.To = to;
                c.Cc = ccList;
                c.Bcc = bcc;
                db.Activity(c, "anonymized", "Personal data removed from a message", removedChange);
            }
        }
    }

    private async Task ScrubActivityAsync(Guid userId, IReadOnlyList<string> identifiers, string placeholder, CancellationToken ct)
    {
        await db.ActivityEntries.Where(a => a.ActorUserId == userId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ActorName, placeholder), ct);
        foreach (var id in identifiers)
        {
            var like = $"%{AdminData.LikeLiteral(id)}%";
            var encoded = $"%{AdminData.LikeLiteral(JsonEncodedText.Encode(id).ToString())}%";
            var entries = await db.ActivityEntries
                .Where(a => EF.Functions.Like(a.Summary, like) || EF.Functions.Like(a.ChangesJson, like) || EF.Functions.Like(a.ChangesJson, encoded))
                .ToListAsync(ct);
            foreach (var a in entries)
            {
                a.Summary = Redact(a.Summary, identifiers, html: false);
                a.ChangesJson = RedactChanges(a.ChangesJson, identifiers);
            }
        }
    }

    private static string RedactChanges(string json, IReadOnlyList<string> identifiers)
    {
        try
        {
            var changes = JsonSerializer.Deserialize<List<FieldChange>>(json, JsonDefaults.Options) ?? [];
            return JsonSerializer.Serialize(
                changes.Select(c => c with
                {
                    From = c.From is null ? null : Redact(c.From, identifiers, html: false),
                    To = c.To is null ? null : Redact(c.To, identifiers, html: false),
                }).ToList(),
                JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return "[]";
        }
    }

    /// <summary>Replaces whole-word occurrences of each identifier (and its HTML-encoded form) with a marker.</summary>
    internal static string Redact(string value, IReadOnlyList<string> identifiers, bool html)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }
        foreach (var id in identifiers)
        {
            var encoded = WebUtility.HtmlEncode(id);
            var forms = html && encoded != id ? new[] { id, encoded } : [id];
            foreach (var form in forms)
            {
                value = Regex.Replace(value, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(form)}(?![\p{{L}}\p{{N}}])", "[removed]",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            }
        }
        return value;
    }

    // ---------------------------------------------------------------- export

    /// <summary>Everything Switchboard holds about a person, as JSON (SR-PRIV-2). The export itself is audited.</summary>
    public async Task<byte[]> ExportAsync(Guid userId, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, ct: ct);
        await access.EnsureCanManageUserAsync(userId, ct);
        var detail = await people.GetAsync(userId, ct);
        var u = detail.User;

        var requested = await db.Tickets.AsNoTracking()
            .Where(t => t.RequesterId == userId || t.RequestedForId == userId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new { t.Id, t.Type, t.Number, t.Subject, t.DescriptionText, Status = t.Status!.Name, t.CreatedAt, t.ResolvedAt, t.ClosedAt, t.DeletedAt })
            .ToListAsync(ct);
        var messages = await db.TicketConversations.AsNoTracking()
            .Where(c => c.AuthorId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { c.TicketId, c.Ticket!.Type, c.Ticket.Number, c.Kind, c.IsPrivate, c.Channel, c.BodyText, c.CreatedAt })
            .ToListAsync(ct);
        var activity = await db.ActivityEntries.AsNoTracking()
            .Where(a => a.ActorUserId == userId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new { a.CreatedAt, a.SubjectType, a.SubjectId, a.Action, a.Summary, a.Channel })
            .ToListAsync(ct);
        var idText = userId.ToString();
        var auditEvents = await db.AuditEntries.AsNoTracking()
            .Where(a => a.ActorUserId == userId || (a.ObjectType == "user" && a.ObjectId == idText))
            .OrderByDescending(a => a.Sequence)
            .Select(a => new { a.OccurredAt, a.Action, a.ObjectType, a.ObjectId, a.Channel, a.Ip })
            .ToListAsync(ct);
        var keys = await db.ApiKeys.AsNoTracking().Where(k => k.UserId == userId)
            .Select(k => new { k.Name, k.Prefix, k.Scopes, k.CreatedAt, k.ExpiresAt, k.LastUsedAt, k.RevokedAt })
            .ToListAsync(ct);
        var delegations = await db.UserDelegations.AsNoTracking().Where(d => d.UserId == userId || d.DelegateUserId == userId)
            .Select(d => new { d.UserId, d.DelegateUserId, d.StartsAt, d.EndsAt, d.Scope })
            .ToListAsync(ct);

        var document = new
        {
            format = "switchboard.person-export/1",
            exportedAt = time.GetUtcNow(),
            person = new
            {
                u.Id,
                name = u.DisplayName,
                email = u.Email,
                otherEmails = detail.SecondaryEmails,
                phone = u.PhoneNumber,
                u.Mobile,
                u.JobTitle,
                department = detail.DepartmentName,
                location = detail.LocationName,
                manager = detail.ManagerName,
                u.TimeZone,
                u.Language,
                u.Address,
                u.Background,
                vip = u.IsVip,
                agent = u.IsAgent,
                organizationAdmin = u.IsOrgAdmin,
                active = u.IsActive,
                forgotten = u.IsForgotten,
                u.Skills,
                customFields = JsonNode.Parse(string.IsNullOrWhiteSpace(u.CustomFieldsJson) ? "{}" : u.CustomFieldsJson),
                u.CreatedAt,
                u.LastSeenAt,
                u.DeactivatedAt,
            },
            access = detail.Memberships.Select(m => new { workspace = m.WorkspaceName, role = m.RoleName, ticketScope = m.Scope }),
            agentGroups = detail.Groups.Select(g => new { g.Name, observer = g.IsObserver }),
            requesterGroups = detail.RequesterGroups.Select(g => g.Name),
            ticketsRequested = requested.Select(t => new
            {
                t.Id, displayId = Ticket.FormatDisplayId(t.Type, t.Number), t.Subject, description = t.DescriptionText, t.Status,
                t.CreatedAt, t.ResolvedAt, t.ClosedAt, inTrash = t.DeletedAt != null,
            }),
            messagesWritten = messages.Select(m => new
            {
                m.TicketId, ticket = Ticket.FormatDisplayId(m.Type, m.Number), kind = m.Kind.ToString(), privateNote = m.IsPrivate,
                channel = m.Channel.ToString(), body = m.BodyText, m.CreatedAt,
            }),
            delegations,
            apiKeys = keys,
            activity,
            auditEvents,
        };

        audit.Record("users.exported", "user", userId.ToString());
        await db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true });
    }

    // ---------------------------------------------------------------- shared steps

    /// <summary>Rotates the security stamp (cookies stop validating) and revokes API keys. Returns keys revoked.</summary>
    private async Task<int> EndSessionsAsync(User user, CancellationToken ct)
    {
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        var keys = await db.ApiKeys.Where(k => k.UserId == user.Id && k.RevokedAt == null).ToListAsync(ct);
        foreach (var key in keys)
        {
            key.RevokedAt = time.GetUtcNow();
        }
        // Remembered two-step devices and external tokens are bound to the account; drop them too.
        db.UserTokens.RemoveRange(await db.UserTokens.Where(t => t.UserId == user.Id && t.LoginProvider != "[AspNetUserStore]").ToListAsync(ct));
        return keys.Count;
    }

    /// <summary>
    /// Hands off the person's open tickets and tasks through <see cref="ITicketService"/>, so the acting admin's
    /// ticket permissions apply and every change is recorded. Returns how many tickets were handled.
    /// </summary>
    private async Task<int> HandOffWorkAsync(User user, OpenWorkPlan plan, CancellationToken ct)
    {
        var open = await people.OpenAssignedTickets(user.Id).AsNoTracking()
            .Select(t => new { t.Id, t.WorkspaceId, Tags = t.Tags.Select(x => x.Tag).ToList() })
            .ToListAsync(ct);
        var tasks = await OpenTasks(user.Id).AsNoTracking().Select(t => new { t.Id, t.TicketId }).ToListAsync(ct);
        if (open.Count == 0 && tasks.Count == 0)
        {
            return 0;
        }

        AgentGroup? group = null;
        if (plan.Action == OpenWorkAction.ReassignToGroup)
        {
            group = await db.AgentGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == plan.GroupId, ct)
                ?? throw new ValidationException("GroupId", "Choose the group that takes over the tickets.");
            if (open.Any(t => t.WorkspaceId != group.WorkspaceId))
            {
                throw new ValidationException("GroupId", $"Some tickets are in another workspace than {group.Name}. Choose a different option.");
            }
        }
        if (plan.Action == OpenWorkAction.ReassignToAgent)
        {
            if (plan.AgentId is not { } a || a == user.Id || !await db.Users.AnyAsync(u => u.Id == a && u.IsActive && (u.IsAgent || u.IsOrgAdmin), ct))
            {
                throw new ValidationException("AgentId", "Choose an active agent to take over the tickets.");
            }
        }

        foreach (var t in open)
        {
            var patch = plan.Action switch
            {
                OpenWorkAction.ReassignToGroup => new TicketPatch { GroupId = group!.Id, AgentId = (Guid?)null },
                OpenWorkAction.ReassignToAgent => new TicketPatch { AgentId = plan.AgentId },
                _ => new TicketPatch { Tags = t.Tags.Append(FlagTag).Distinct().ToArray() },
            };
            await tickets.UpdateAsync(t.Id, patch, ct: ct);
        }
        if (plan.Action != OpenWorkAction.Flag)
        {
            foreach (var task in tasks)
            {
                var patch = plan.Action == OpenWorkAction.ReassignToAgent
                    ? new TaskPatch { AssigneeId = plan.AgentId }
                    : new TaskPatch { AssigneeId = (Guid?)null, GroupId = group!.Id };
                await tickets.UpdateTaskAsync(task.Id, patch, ct);
            }
        }
        return open.Count;
    }

    private IQueryable<TicketTask> OpenTasks(Guid userId) =>
        db.TicketTasks.Where(t => t.AssigneeId == userId && t.Status != TicketTaskStatus.Completed);
}
