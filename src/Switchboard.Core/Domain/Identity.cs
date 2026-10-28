using Microsoft.AspNetCore.Identity;

namespace Switchboard.Core.Domain;

/// <summary>
/// Every person in the instance. Requesters and agents are the same entity;
/// <see cref="IsAgent"/> marks users with an agent role in at least one workspace.
/// </summary>
public class User : IdentityUser<Guid>, IHasTimestamps
{
    public User()
    {
        Id = Guid.CreateVersion7();
        SecurityStamp = Guid.NewGuid().ToString();
    }

    public string DisplayName { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? Mobile { get; set; }
    public string? TimeZone { get; set; }
    public string Language { get; set; } = "en";
    public string? AvatarUrl { get; set; }
    /// <summary>Sanitized HTML appended to the agent's replies.</summary>
    public string? Signature { get; set; }
    public string? Address { get; set; }
    public string? Background { get; set; }

    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public Guid? LocationId { get; set; }
    public Location? Location { get; set; }
    public Guid? ManagerId { get; set; }
    public User? Manager { get; set; }

    public bool IsAgent { get; set; }
    public bool IsOrgAdmin { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsVip { get; set; }
    /// <summary>Set when personal data was erased (SR-PRIV-3). The row stays so references remain valid.</summary>
    public bool IsForgotten { get; set; }

    public AgentAvailability Availability { get; set; } = AgentAvailability.Offline;
    public string? OutOfOfficeMessage { get; set; }
    public DateTimeOffset? OutOfOfficeUntil { get; set; }
    public List<string> Skills { get; set; } = [];

    public string CustomFieldsJson { get; set; } = "{}";

    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<UserEmail> SecondaryEmails { get; set; } = [];
    public List<WorkspaceMembership> Memberships { get; set; } = [];
    public List<AgentGroupMember> GroupMemberships { get; set; } = [];

    /// <summary>Two-letter initials for avatars.</summary>
    public string Initials
    {
        get
        {
            var parts = (DisplayName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                0 => "?",
                1 => parts[0][..1].ToUpperInvariant(),
                _ => string.Concat(parts[0][..1], parts[^1][..1]).ToUpperInvariant(),
            };
        }
    }
}

public enum AgentAvailability
{
    Offline = 0,
    Available = 1,
    Away = 2,
}

/// <summary>Additional addresses for a user. All addresses are unique across users (INV-ID-2).</summary>
public class UserEmail : Entity
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
}

/// <summary>Which records an agent can see in a workspace (FR-IAM-11).</summary>
public enum TicketScope
{
    /// <summary>All records in the workspace.</summary>
    Global = 1,
    /// <summary>Records of the agent's groups, plus records assigned to them.</summary>
    Group = 2,
    /// <summary>Only records assigned to them.</summary>
    Restricted = 3,
}

/// <summary>An agent role: a named set of privileges.</summary>
public class Role : Entity
{
    /// <summary>Stable key for built-in roles, e.g. <c>sd_agent</c>.</summary>
    public string? Key { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public List<string> Privileges { get; set; } = [];
}

/// <summary>An agent's role and ticket scope in one workspace.</summary>
public class WorkspaceMembership : Entity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
    public TicketScope TicketScope { get; set; } = TicketScope.Global;
}

public class Department : Entity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Guid? HeadUserId { get; set; }
    public Guid? PrimeUserId { get; set; }
    /// <summary>Email domains that auto-associate requesters, e.g. <c>finance.example.com</c>.</summary>
    public List<string> Domains { get; set; } = [];
    public string CustomFieldsJson { get; set; } = "{}";
}

public class Location : Entity
{
    public string Name { get; set; } = "";
    public Guid? ParentId { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
}

public enum AssignmentMode
{
    None = 0,
    RoundRobin = 1,
    LoadBased = 2,
    SkillBased = 3,
}

public class AgentGroup : Entity
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public AssignmentMode AssignmentMode { get; set; }
    public int? MaxOpenPerAgent { get; set; }
    public int? UnassignedEscalationMinutes { get; set; }
    public Guid? EscalateToUserId { get; set; }
    /// <summary>Owned by the service-levels module; no FK from Core.</summary>
    public Guid? BusinessCalendarId { get; set; }
    /// <summary>Round-robin pointer.</summary>
    public Guid? LastAssignedUserId { get; set; }
    public List<AgentGroupMember> Members { get; set; } = [];
}

public class AgentGroupMember
{
    public Guid GroupId { get; set; }
    public AgentGroup? Group { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    /// <summary>Observers see the group's records and get notifications but are not assigned work.</summary>
    public bool IsObserver { get; set; }
}

public class RequesterGroup : Entity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsRuleBased { get; set; }
    /// <summary>Rule definition for rule-based groups (field/operator/value conditions).</summary>
    public string? RuleJson { get; set; }
    public List<RequesterGroupMember> Members { get; set; } = [];
}

public class RequesterGroupMember
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
}

[Flags]
public enum DelegationScope
{
    None = 0,
    Approvals = 1,
    Forms = 2,
}

/// <summary>A user hands approvals/forms to a delegate for a date range (FR-IAM-4).</summary>
public class UserDelegation : Entity
{
    public Guid UserId { get; set; }
    public Guid DelegateUserId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public DelegationScope Scope { get; set; } = DelegationScope.Approvals;
    public DateTimeOffset CreatedAt { get; set; }
}
