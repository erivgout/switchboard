using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Admin.Data;

namespace Switchboard.Modules.Admin.Services;

public sealed record Paged<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
}

public sealed record PeopleQuery
{
    public const int PageSize = 25;
    /// <summary><c>agents</c> or <c>requesters</c>.</summary>
    public string Kind { get; init; } = "agents";
    /// <summary><c>active</c>, <c>inactive</c> or <c>all</c>.</summary>
    public string Status { get; init; } = "active";
    public string? Search { get; init; }
    public Guid? DepartmentId { get; init; }
    /// <summary>Agent group (agents) or requester group (requesters).</summary>
    public Guid? GroupId { get; init; }
    public int Page { get; init; } = 1;
}

public sealed record PersonRow(
    Guid Id, string Name, string? Email, string? JobTitle, string? Department, bool IsAgent, bool IsOrgAdmin,
    bool IsActive, bool IsVip, bool IsForgotten, DateTimeOffset? LastSeenAt, IReadOnlyList<string> Roles);

public sealed record PersonOption(Guid Id, string Name, string? Email, bool IsAgent);

public sealed record PersonInput
{
    public string DisplayName { get; init; } = "";
    public string Email { get; init; } = "";
    public IReadOnlyList<string> SecondaryEmails { get; init; } = [];
    public string? Phone { get; init; }
    public string? Mobile { get; init; }
    public string? JobTitle { get; init; }
    public string? TimeZone { get; init; }
    public string? Language { get; init; }
    public string? Address { get; init; }
    public string? Background { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? LocationId { get; init; }
    public Guid? ManagerId { get; init; }
    public bool IsVip { get; init; }
    /// <summary>Custom user fields to set; a null value clears the field. Omitted keys are unchanged.</summary>
    public IReadOnlyDictionary<string, JsonElement?>? CustomFields { get; init; }
}

/// <summary>An agent's first workspace role, chosen when the agent is created or converted.</summary>
public sealed record NewAgentAccess(Guid WorkspaceId, Guid RoleId, TicketScope Scope);

public sealed record MembershipInput(Guid WorkspaceId, Guid? RoleId, TicketScope Scope);

public enum GroupRole
{
    None = 0,
    Member = 1,
    Observer = 2,
}

public sealed record GroupMembershipInput(Guid GroupId, GroupRole Role);

public sealed record AccessInput
{
    /// <summary>Desired role per workspace listed; a null role removes access to that workspace.</summary>
    public IReadOnlyList<MembershipInput> Memberships { get; init; } = [];
    /// <summary>Desired membership per group listed.</summary>
    public IReadOnlyList<GroupMembershipInput> Groups { get; init; } = [];
    public IReadOnlyList<string>? Skills { get; init; }
}

public sealed record MembershipView(Guid WorkspaceId, string WorkspaceName, Guid RoleId, string RoleName, TicketScope Scope);

public sealed record GroupView(Guid GroupId, string Name, Guid WorkspaceId, bool IsObserver);

public sealed record PersonDetail(
    User User,
    IReadOnlyList<string> SecondaryEmails,
    IReadOnlyList<MembershipView> Memberships,
    IReadOnlyList<GroupView> Groups,
    IReadOnlyList<(Guid Id, string Name)> RequesterGroups,
    string? ManagerName,
    string? DepartmentName,
    string? LocationName,
    int OpenAssignedTickets,
    int ActiveApiKeys,
    (Guid Id, string Name)? MergedInto);

/// <summary>Requester and agent profiles, addresses and access (FR-IAM-1, FR-IAM-3, FR-IAM-10, FR-IAM-11).</summary>
public sealed partial class PeopleService(
    SwitchboardDbContext db,
    AdminAccess access,
    IUserDirectory directory,
    ICustomFieldService customFields,
    IAuditLog audit,
    TimeProvider time)
{
    public const int MaxSecondaryEmails = 10;

    // ---------------------------------------------------------------- reading

    public async Task<Paged<PersonRow>> ListAsync(PeopleQuery query, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersView, ct: ct);
        var users = db.Users.AsNoTracking().AsQueryable();
        users = query.Kind == "requesters" ? users.Where(u => !u.IsAgent && !u.IsOrgAdmin) : users.Where(u => u.IsAgent || u.IsOrgAdmin);
        users = query.Status switch
        {
            "inactive" => users.Where(u => !u.IsActive),
            "all" => users,
            _ => users.Where(u => u.IsActive),
        };
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var q = query.Search.Trim();
            var upper = q.ToUpperInvariant();
            users = users.Where(u => u.DisplayName.Contains(q) || (u.NormalizedEmail != null && u.NormalizedEmail.Contains(upper))
                || (u.JobTitle != null && u.JobTitle.Contains(q))
                || db.UserEmails.Any(e => e.UserId == u.Id && e.NormalizedEmail.Contains(upper)));
        }
        if (query.DepartmentId is { } dept)
        {
            users = users.Where(u => u.DepartmentId == dept);
        }
        if (query.GroupId is { } group)
        {
            users = users.Where(u => db.AgentGroupMembers.Any(m => m.UserId == u.Id && m.GroupId == group)
                || db.RequesterGroupMembers.Any(m => m.UserId == u.Id && m.GroupId == group));
        }

        var total = await users.CountAsync(ct);
        var page = Math.Clamp(query.Page, 1, Math.Max(1, (total + PeopleQuery.PageSize - 1) / PeopleQuery.PageSize));
        var rows = await users
            .OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
            .Skip((page - 1) * PeopleQuery.PageSize).Take(PeopleQuery.PageSize)
            .Select(u => new
            {
                u.Id, u.DisplayName, u.Email, u.JobTitle, Department = u.Department!.Name, u.IsAgent, u.IsOrgAdmin,
                u.IsActive, u.IsVip, u.IsForgotten, u.LastSeenAt,
                Roles = u.Memberships.Select(m => m.Role!.Name).ToList(),
            })
            .ToListAsync(ct);
        return new Paged<PersonRow>(
            rows.Select(r => new PersonRow(r.Id, r.DisplayName, r.Email, r.JobTitle, r.Department, r.IsAgent, r.IsOrgAdmin, r.IsActive,
                r.IsVip, r.IsForgotten, r.LastSeenAt, r.Roles.Distinct().Order().ToList())).ToList(),
            total, page, PeopleQuery.PageSize);
    }

    public async Task<PersonDetail> GetAsync(Guid id, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersView, ct: ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException();
        var secondary = await db.UserEmails.AsNoTracking().Where(e => e.UserId == id).OrderBy(e => e.Email).Select(e => e.Email).ToListAsync(ct);
        var memberships = await db.WorkspaceMemberships.AsNoTracking().Where(m => m.UserId == id)
            .OrderBy(m => m.Workspace!.Name)
            .Select(m => new MembershipView(m.WorkspaceId, m.Workspace!.Name, m.RoleId, m.Role!.Name, m.TicketScope))
            .ToListAsync(ct);
        var groups = await db.AgentGroupMembers.AsNoTracking().Where(m => m.UserId == id)
            .OrderBy(m => m.Group!.Name)
            .Select(m => new GroupView(m.GroupId, m.Group!.Name, m.Group.WorkspaceId, m.IsObserver))
            .ToListAsync(ct);
        var requesterGroups = (await db.RequesterGroups.AsNoTracking()
            .Where(g => g.Members.Any(m => m.UserId == id)).OrderBy(g => g.Name)
            .Select(g => new { g.Id, g.Name }).ToListAsync(ct)).Select(g => (g.Id, g.Name)).ToList();
        var manager = user.ManagerId is { } mid ? await NameAsync(mid, ct) : null;
        var department = user.DepartmentId is { } did ? await db.Departments.Where(d => d.Id == did).Select(d => d.Name).FirstOrDefaultAsync(ct) : null;
        var location = user.LocationId is { } lid ? await db.Locations.Where(l => l.Id == lid).Select(l => l.Name).FirstOrDefaultAsync(ct) : null;
        var open = await OpenAssignedTickets(id).CountAsync(ct);
        var keys = await db.ApiKeys.CountAsync(k => k.UserId == id && k.RevokedAt == null, ct);
        var merge = await db.Set<AdmUserMerge>().AsNoTracking().Where(m => m.SourceUserId == id)
            .Select(m => new { m.SurvivorUserId }).FirstOrDefaultAsync(ct);
        (Guid, string)? mergedInto = merge is null ? null : (merge.SurvivorUserId, await NameAsync(merge.SurvivorUserId, ct) ?? "");
        return new PersonDetail(user, secondary, memberships, groups, requesterGroups, manager, department, location, open, keys, mergedInto);
    }

    /// <summary>Open, live tickets assigned to the user (SR-DEL-3).</summary>
    public IQueryable<Ticket> OpenAssignedTickets(Guid userId) =>
        db.Tickets.Where(t => t.AgentId == userId && t.DeletedAt == null && !t.IsSpam && t.MergedIntoId == null && t.PurgedAt == null
            && (t.Status!.Category == StatusCategory.Open || t.Status.Category == StatusCategory.Pending));

    /// <summary>People search for pickers. <paramref name="kind"/>: <c>agents</c>, <c>requesters</c> or <c>all</c>.</summary>
    public async Task<IReadOnlyList<PersonOption>> LookupAsync(string? search, string kind, Guid? workspaceId = null, int take = 20, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersView, ct: ct);
        var users = db.Users.AsNoTracking().Where(u => u.IsActive);
        users = kind switch
        {
            "agents" => users.Where(u => u.IsOrgAdmin || (u.IsAgent && (workspaceId == null || u.Memberships.Any(m => m.WorkspaceId == workspaceId)))),
            "requesters" => users.Where(u => !u.IsAgent && !u.IsOrgAdmin),
            _ => users,
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            var upper = q.ToUpperInvariant();
            users = users.Where(u => u.DisplayName.Contains(q) || (u.NormalizedEmail != null && u.NormalizedEmail.Contains(upper)));
        }
        return await users.OrderBy(u => u.DisplayName).Take(Math.Clamp(take, 1, 100))
            .Select(u => new PersonOption(u.Id, u.DisplayName, u.Email, u.IsAgent || u.IsOrgAdmin))
            .ToListAsync(ct);
    }

    public async Task<PersonOption?> OptionAsync(Guid? id, CancellationToken ct = default) =>
        id is not { } v ? null : await db.Users.AsNoTracking().Where(u => u.Id == v)
            .Select(u => new PersonOption(u.Id, u.DisplayName, u.Email, u.IsAgent || u.IsOrgAdmin)).FirstOrDefaultAsync(ct);

    // ---------------------------------------------------------------- create and edit

    public async Task<User> CreateAsync(PersonInput input, NewAgentAccess? agent, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, agent?.WorkspaceId, ct);
        Role? role = null;
        if (agent is not null)
        {
            role = await RoleInWorkspaceAsync(agent.WorkspaceId, agent.RoleId, ct);
            await access.EnsureCanAssignRoleAsync(role, agent.WorkspaceId, ct);
        }

        return await db.InTransactionAsync(async () =>
        {
            await db.LockEmailsAsync(ct);
            var user = new User
            {
                CreatedAt = time.GetUtcNow(),
                IsAgent = agent is not null,
                Availability = AgentAvailability.Offline,
                EmailConfirmed = true,
                LockoutEnabled = true,
            };
            await ApplyProfileAsync(user, input, [], ct);
            db.Users.Add(user);
            if (agent is not null)
            {
                db.WorkspaceMemberships.Add(new WorkspaceMembership { UserId = user.Id, WorkspaceId = agent.WorkspaceId, RoleId = agent.RoleId, TicketScope = agent.Scope });
            }
            audit.Record("users.created", "user", user.Id.ToString(), new
            {
                kind = agent is null ? "requester" : "agent",
                role = role?.Name,
                workspaceId = agent?.WorkspaceId,
                scope = agent?.Scope,
            });
            await db.SaveChangesAsync(ct);
            return user;
        }, ct);
    }

    public async Task UpdateProfileAsync(Guid id, PersonInput input, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, ct: ct);
        await access.EnsureCanManageUserAsync(id, ct);
        await db.InTransactionAsync(async () =>
        {
            await db.LockEmailsAsync(ct);
            var user = await db.Users.Include(u => u.SecondaryEmails).FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException();
            if (user.IsForgotten)
            {
                throw new ValidationException("DisplayName", "This person's personal data was erased, so their profile can't be edited.");
            }
            var changed = await ApplyProfileAsync(user, input, user.SecondaryEmails, ct);
            if (changed.Count == 0)
            {
                return;
            }
            // Field names only: personal data stays out of the append-only audit log (SR-PRIV-3).
            audit.Record("users.updated", "user", id.ToString(), new { fields = changed });
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    /// <summary>Validates and applies profile fields. Returns the names of the fields that changed.</summary>
    private async Task<List<string>> ApplyProfileAsync(User user, PersonInput input, List<UserEmail> currentSecondary, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var changed = new List<string>();
        void Error(string key, string message) => errors.TryAdd(key, [message]);
        void Set<T>(string name, T current, T next, Action<T> apply)
        {
            if (!EqualityComparer<T>.Default.Equals(current, next))
            {
                changed.Add(name);
                apply(next);
            }
        }

        var name = (input.DisplayName ?? "").Trim();
        if (name.Length == 0)
        {
            Error(nameof(PersonInput.DisplayName), "Enter the person's name.");
        }
        else if (name.Length > 200)
        {
            Error(nameof(PersonInput.DisplayName), "Keep the name under 200 characters.");
        }

        var email = (input.Email ?? "").Trim();
        if (!IUserDirectory.IsValidEmail(email))
        {
            Error(nameof(PersonInput.Email), "Enter a valid email address, like dana@example.com.");
        }
        else if (!await directory.IsEmailFreeAsync(email, user.Id, ct))
        {
            Error(nameof(PersonInput.Email), $"{email} is already used by {await OwnerOfAsync(email, ct)}.");
        }

        var secondary = new List<string>();
        foreach (var raw in input.SecondaryEmails ?? [])
        {
            var s = raw.Trim();
            if (s.Length == 0)
            {
                continue;
            }
            if (!IUserDirectory.IsValidEmail(s))
            {
                Error(nameof(PersonInput.SecondaryEmails), $"{s} isn't a valid email address.");
            }
            else if (s.Equals(email, StringComparison.OrdinalIgnoreCase) || secondary.Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                Error(nameof(PersonInput.SecondaryEmails), $"{s} is listed twice. Each address can appear once.");
            }
            else if (!await directory.IsEmailFreeAsync(s, user.Id, ct))
            {
                // INV-ID-2: an address, primary or secondary, belongs to one person.
                Error(nameof(PersonInput.SecondaryEmails), $"{s} is already used by {await OwnerOfAsync(s, ct)}.");
            }
            else
            {
                secondary.Add(s);
            }
        }
        if (secondary.Count > MaxSecondaryEmails)
        {
            Error(nameof(PersonInput.SecondaryEmails), $"Add at most {MaxSecondaryEmails} other addresses.");
        }

        string? Clean(string key, string? value, int max, string label)
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (v is { Length: > 0 } && v.Length > max)
            {
                Error(key, $"Keep the {label} under {max} characters.");
            }
            return v;
        }
        var phone = Clean(nameof(PersonInput.Phone), input.Phone, 64, "phone number");
        var mobile = Clean(nameof(PersonInput.Mobile), input.Mobile, 64, "mobile number");
        var jobTitle = Clean(nameof(PersonInput.JobTitle), input.JobTitle, 200, "job title");
        var address = Clean(nameof(PersonInput.Address), input.Address, 500, "address");
        var background = Clean(nameof(PersonInput.Background), input.Background, 4000, "background");
        var timeZone = string.IsNullOrWhiteSpace(input.TimeZone) ? null : input.TimeZone.Trim();
        if (timeZone is not null && timeZone != "UTC" && !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
        {
            Error(nameof(PersonInput.TimeZone), "Choose a time zone from the list.");
        }
        var language = string.IsNullOrWhiteSpace(input.Language) ? "en" : input.Language.Trim();
        if (!LanguagePattern().IsMatch(language))
        {
            Error(nameof(PersonInput.Language), "Choose a language from the list.");
        }

        if (input.DepartmentId is { } dept && !await db.Departments.AnyAsync(d => d.Id == dept, ct))
        {
            Error(nameof(PersonInput.DepartmentId), "That department no longer exists.");
        }
        if (input.LocationId is { } loc && !await db.Locations.AnyAsync(l => l.Id == loc, ct))
        {
            Error(nameof(PersonInput.LocationId), "That location no longer exists.");
        }
        if (input.ManagerId is { } manager && manager != user.ManagerId)
        {
            if (manager == user.Id)
            {
                Error(nameof(PersonInput.ManagerId), "A person can't be their own manager.");
            }
            else if (!await db.Users.AnyAsync(u => u.Id == manager && u.IsActive, ct))
            {
                Error(nameof(PersonInput.ManagerId), "Choose an active person as the manager.");
            }
            else if (await ManagerChainReachesAsync(manager, user.Id, ct))
            {
                Error(nameof(PersonInput.ManagerId), $"{await NameAsync(manager, ct)} already reports to {name}, directly or through others. Choose someone else.");
            }
        }

        string? customJson = null;
        if (input.CustomFields is { Count: > 0 })
        {
            try
            {
                customJson = await customFields.ApplyAsync("user", null, user.CustomFieldsJson, input.CustomFields, FieldAudience.Agent, ct);
            }
            catch (ValidationException ex)
            {
                foreach (var (k, v) in ex.Errors)
                {
                    errors.TryAdd(k, v);
                }
            }
        }
        var missing = await customFields.MissingRequiredAsync("user", null, customJson ?? user.CustomFieldsJson, RequiredMoment.AgentSave, ct);
        if (missing.Count > 0)
        {
            errors.TryAdd("CustomFields", [.. missing]);
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        Set("name", user.DisplayName, name, v => user.DisplayName = v);
        if (!string.Equals(user.Email, email, StringComparison.Ordinal))
        {
            changed.Add("email");
            user.Email = email;
            user.NormalizedEmail = IUserDirectory.Normalize(email);
            user.UserName = email;
            user.NormalizedUserName = IUserDirectory.Normalize(email);
        }
        Set("phone", user.PhoneNumber, phone, v => user.PhoneNumber = v);
        Set("mobile", user.Mobile, mobile, v => user.Mobile = v);
        Set("jobTitle", user.JobTitle, jobTitle, v => user.JobTitle = v);
        Set("address", user.Address, address, v => user.Address = v);
        Set("background", user.Background, background, v => user.Background = v);
        Set("timeZone", user.TimeZone, timeZone, v => user.TimeZone = v);
        Set("language", user.Language, language, v => user.Language = v);
        Set("department", user.DepartmentId, input.DepartmentId, v => user.DepartmentId = v);
        Set("location", user.LocationId, input.LocationId, v => user.LocationId = v);
        Set("manager", user.ManagerId, input.ManagerId, v => user.ManagerId = v);
        Set("vip", user.IsVip, input.IsVip, v => user.IsVip = v);
        if (customJson is not null && customJson != user.CustomFieldsJson)
        {
            changed.Add("customFields");
            user.CustomFieldsJson = customJson;
        }

        // Keep rows whose address stays, so the unique index never sees a delete-and-reinsert of the same address.
        var wanted = secondary.ToDictionary(IUserDirectory.Normalize, s => s);
        var removed = currentSecondary.Where(e => !wanted.ContainsKey(e.NormalizedEmail)).ToList();
        var added = wanted.Where(w => currentSecondary.All(e => e.NormalizedEmail != w.Key)).ToList();
        foreach (var e in removed)
        {
            db.UserEmails.Remove(e);
        }
        foreach (var (normalized, address2) in added)
        {
            db.UserEmails.Add(new UserEmail { UserId = user.Id, Email = address2, NormalizedEmail = normalized });
        }
        if (removed.Count > 0 || added.Count > 0)
        {
            changed.Add("secondaryEmails");
        }
        return changed;
    }

    /// <summary>True when walking up from <paramref name="start"/> through managers reaches <paramref name="target"/>.</summary>
    internal async Task<bool> ManagerChainReachesAsync(Guid start, Guid target, CancellationToken ct)
    {
        var seen = new HashSet<Guid>();
        Guid? current = start;
        while (current is { } c)
        {
            if (c == target)
            {
                return true;
            }
            if (!seen.Add(c) || seen.Count > 500)
            {
                return false;
            }
            var local = db.Users.Local.FirstOrDefault(u => u.Id == c);
            current = local is not null ? local.ManagerId : await db.Users.Where(u => u.Id == c).Select(u => u.ManagerId).FirstOrDefaultAsync(ct);
        }
        return false;
    }

    // ---------------------------------------------------------------- access

    public async Task UpdateAccessAsync(Guid id, AccessInput input, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, ct: ct);
        await access.EnsureNotSelfAsync(id, "You can't change your own access. Ask another admin to do it.", ct);
        await access.EnsureCanManageUserAsync(id, ct);

        var user = await db.Users
            .Include(u => u.Memberships).ThenInclude(m => m.Role)
            .Include(u => u.GroupMemberships).ThenInclude(m => m.Group)
            .FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException();
        if (!user.IsActive)
        {
            throw new ValidationException("Memberships", $"Reactivate {user.DisplayName} before changing their access.");
        }
        if (!user.IsAgent && !user.IsOrgAdmin)
        {
            throw new ValidationException("Memberships", $"{user.DisplayName} is a requester. Convert them to an agent first.");
        }

        var membershipChanges = new List<object>();
        foreach (var m in input.Memberships.DistinctBy(m => m.WorkspaceId))
        {
            var current = user.Memberships.FirstOrDefault(x => x.WorkspaceId == m.WorkspaceId);
            var changed = current?.RoleId != m.RoleId || (m.RoleId is not null && current?.TicketScope != m.Scope);
            if (!changed)
            {
                continue;
            }
            if (!await db.Workspaces.AnyAsync(w => w.Id == m.WorkspaceId && !w.IsArchived, ct))
            {
                throw new ValidationException("Memberships", "That workspace doesn't exist or is archived.");
            }
            await access.DemandAsync(Privileges.UsersManage, m.WorkspaceId, ct);
            if (current?.Role is not null)
            {
                await access.EnsureCanAssignRoleAsync(current.Role, m.WorkspaceId, ct);
            }
            if (m.RoleId is not { } roleId)
            {
                db.WorkspaceMemberships.Remove(current!);
                user.Memberships.Remove(current!);
                membershipChanges.Add(new { workspaceId = m.WorkspaceId, from = current!.Role?.Name, to = (string?)null });
                continue;
            }
            if (!Enum.IsDefined(m.Scope))
            {
                throw new ValidationException("Memberships", "Choose which tickets this agent can see.");
            }
            var role = await RoleInWorkspaceAsync(m.WorkspaceId, roleId, ct);
            await access.EnsureCanAssignRoleAsync(role, m.WorkspaceId, ct);
            if (current is null)
            {
                var added = new WorkspaceMembership { UserId = id, WorkspaceId = m.WorkspaceId, RoleId = roleId, Role = role, TicketScope = m.Scope };
                db.WorkspaceMemberships.Add(added);
                user.Memberships.Add(added);
            }
            else
            {
                current.RoleId = roleId;
                current.Role = role;
                current.TicketScope = m.Scope;
            }
            membershipChanges.Add(new { workspaceId = m.WorkspaceId, from = current?.Role?.Name, to = role.Name, scope = m.Scope });
        }

        if (!user.IsOrgAdmin && user.Memberships.Count == 0)
        {
            throw new ValidationException("Memberships",
                "Agents need a role in at least one workspace. To remove all access, convert them to a requester or deactivate them.");
        }

        var workspaces = user.Memberships.Select(m => m.WorkspaceId).ToHashSet();
        var groupChanges = new List<object>();
        foreach (var g in input.Groups.DistinctBy(g => g.GroupId))
        {
            var group = await db.AgentGroups.FirstOrDefaultAsync(x => x.Id == g.GroupId, ct)
                ?? throw new ValidationException("Groups", "That group no longer exists.");
            var current = user.GroupMemberships.FirstOrDefault(x => x.GroupId == g.GroupId);
            var currentRole = current is null ? GroupRole.None : current.IsObserver ? GroupRole.Observer : GroupRole.Member;
            if (currentRole == g.Role)
            {
                continue;
            }
            if (!await access.HasAsync(Privileges.UsersManage, group.WorkspaceId, ct) && !await access.HasAsync(Privileges.GroupsManage, group.WorkspaceId, ct))
            {
                throw new ForbiddenException($"You can't change who is in {group.Name}.");
            }
            if (g.Role != GroupRole.None && !user.IsOrgAdmin && !workspaces.Contains(group.WorkspaceId))
            {
                throw new ValidationException("Groups", $"{user.DisplayName} needs a role in the group's workspace before joining {group.Name}.");
            }
            if (g.Role == GroupRole.None)
            {
                db.AgentGroupMembers.Remove(current!);
                user.GroupMemberships.Remove(current!);
            }
            else if (current is null)
            {
                var member = new AgentGroupMember { GroupId = group.Id, UserId = id, IsObserver = g.Role == GroupRole.Observer };
                db.AgentGroupMembers.Add(member);
                user.GroupMemberships.Add(member);
            }
            else
            {
                current.IsObserver = g.Role == GroupRole.Observer;
            }
            groupChanges.Add(new { groupId = group.Id, group = group.Name, role = g.Role.ToString().ToLowerInvariant() });
        }

        // Group membership needs a role in the group's workspace; drop memberships left behind by removed roles.
        if (!user.IsOrgAdmin)
        {
            foreach (var orphan in user.GroupMemberships.Where(m => m.Group is not null && !workspaces.Contains(m.Group.WorkspaceId)).ToList())
            {
                db.AgentGroupMembers.Remove(orphan);
                user.GroupMemberships.Remove(orphan);
                groupChanges.Add(new { groupId = orphan.GroupId, group = orphan.Group!.Name, role = "none" });
            }
        }

        var skillsChanged = false;
        if (input.Skills is not null)
        {
            var skills = input.Skills.Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length is > 0 and <= 50).Distinct().Order().ToList();
            if (skills.Count > 30)
            {
                throw new ValidationException("Skills", "List at most 30 skills.");
            }
            if (!skills.SequenceEqual(user.Skills.Order()))
            {
                user.Skills = skills;
                skillsChanged = true;
            }
        }

        if (membershipChanges.Count == 0 && groupChanges.Count == 0 && !skillsChanged)
        {
            return;
        }
        audit.Record("users.access_updated", "user", id.ToString(), new { memberships = membershipChanges, groups = groupChanges, skills = skillsChanged });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Only organization admins grant or revoke organization admin, and one always remains (INV-ID-1).</summary>
    public async Task SetOrgAdminAsync(Guid id, bool grant, CancellationToken ct = default)
    {
        await access.DemandOrgAdminAsync(ct);
        await db.InTransactionAsync(async () =>
        {
            await db.LockOrgAdminsAsync(ct);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException();
            if (user.IsOrgAdmin == grant)
            {
                return;
            }
            if (grant)
            {
                if (!user.IsActive || user.IsForgotten)
                {
                    throw new ValidationException("OrgAdmin", $"Reactivate {user.DisplayName} before making them an organization admin.");
                }
                user.IsOrgAdmin = true;
                user.IsAgent = true;
                audit.Record("users.org_admin_granted", "user", id.ToString());
            }
            else
            {
                await db.EnsureAnotherOrgAdminAsync(id, ct);
                user.IsOrgAdmin = false;
                audit.Record("users.org_admin_revoked", "user", id.ToString());
            }
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    // ---------------------------------------------------------------- helpers

    internal async Task<Role> RoleInWorkspaceAsync(Guid workspaceId, Guid roleId, CancellationToken ct)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId && !w.IsArchived, ct))
        {
            throw new ValidationException("WorkspaceId", "That workspace doesn't exist or is archived.");
        }
        return await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct)
            ?? throw new ValidationException("RoleId", "That role no longer exists.");
    }

    private async Task<string> OwnerOfAsync(string email, CancellationToken ct) =>
        (await directory.FindByEmailAsync(email, ct))?.DisplayName ?? "someone else";

    private Task<string?> NameAsync(Guid id, CancellationToken ct) =>
        db.Users.Where(u => u.Id == id).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z]{2,4})?$")]
    private static partial Regex LanguagePattern();
}
