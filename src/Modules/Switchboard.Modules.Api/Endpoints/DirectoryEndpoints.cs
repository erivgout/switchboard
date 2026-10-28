using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Api;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Api.Auth;
using Switchboard.Modules.Api.Http;

namespace Switchboard.Modules.Api.Endpoints;

/// <summary>People, the caller's own profile, and read-only reference data.</summary>
internal static class DirectoryEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        MapMe(endpoints);
        MapPeople(endpoints);
        MapReference(endpoints);
    }

    // ---------------------------------------------------------------- me

    private static void MapMe(IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapSwitchboardApi("me", "People").WithApiConventions();
        g.MapGet("", async (SwitchboardDbContext db, IActorAccessor actors, IPermissionService permissions, CancellationToken ct) =>
            {
                var actor = actors.Current;
                var p = await permissions.CurrentAsync(ct) ?? throw new ForbiddenException();
                var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == p.UserId, ct);
                var workspaces = await db.Workspaces.AsNoTracking()
                    .Where(w => !w.IsArchived)
                    .OrderBy(w => !w.IsPrimary).ThenBy(w => w.Name)
                    .ToListAsync(ct);
                var mine = workspaces
                    .Where(w => p.Workspaces.ContainsKey(w.Id) || p.IsOrgAdmin)
                    .Select(w => p.Workspaces.TryGetValue(w.Id, out var access)
                        ? new MeWorkspaceDto(w.Id, w.Key, w.Name, access.RoleName, access.Scope)
                        : new MeWorkspaceDto(w.Id, w.Key, w.Name, "Organization admin", TicketScope.Global))
                    .ToList();
                return ApiJson.Ok(new MeDto(user.Id, user.DisplayName, user.Email, p.IsAgent, p.IsOrgAdmin, user.TimeZone, user.Language, mine,
                    actor.Scopes?.Order(StringComparer.Ordinal).ToList()));
            })
            .WithName("getMe").WithSummary("Who this API key acts as")
            .Produces<MeDto>().RequireScope(ApiScopes.Profile);
    }

    // ---------------------------------------------------------------- people

    private static void MapPeople(IEndpointRouteBuilder endpoints)
    {
        var requesters = endpoints.MapSwitchboardApi("requesters", "People").WithApiConventions();
        requesters.MapGet("", (HttpContext http, SwitchboardDbContext db, IPermissionService permissions, CancellationToken ct) =>
                ListPeopleAsync(http, db, permissions, agents: false, ct))
            .WithName("listRequesters").WithSummary("List requesters")
            .WithDescription("q matches name or email. department_id filters by department. state is active (default), inactive or all.")
            .Produces<ListResponse<PersonDto>>().RequireScope(ApiScopes.UsersRead);
        requesters.MapGet("/{id:guid}", (Guid id, SwitchboardDbContext db, IPermissionService permissions, CancellationToken ct) =>
                GetPersonAsync(db, permissions, id, agents: false, ct))
            .WithName("getRequester").WithSummary("Get a requester")
            .Produces<PersonDto>().RequireScope(ApiScopes.UsersRead);
        requesters.MapPost("", CreateRequesterAsync)
            .WithName("createRequester").WithSummary("Add a requester")
            .WithDescription("Needs permission to manage people. Scoped keys can't add people.")
            .Accepts<CreateRequesterRequest>("application/json").Produces<PersonDto>(StatusCodes.Status201Created);

        var agents = endpoints.MapSwitchboardApi("agents", "People").WithApiConventions();
        agents.MapGet("", (HttpContext http, SwitchboardDbContext db, IPermissionService permissions, CancellationToken ct) =>
                ListPeopleAsync(http, db, permissions, agents: true, ct))
            .WithName("listAgents").WithSummary("List agents")
            .WithDescription("q matches name or email. group_id filters by group. state is active (default), inactive or all.")
            .Produces<ListResponse<PersonDto>>().RequireScope(ApiScopes.UsersRead);
        agents.MapGet("/{id:guid}", (Guid id, SwitchboardDbContext db, IPermissionService permissions, CancellationToken ct) =>
                GetPersonAsync(db, permissions, id, agents: true, ct))
            .WithName("getAgent").WithSummary("Get an agent")
            .Produces<PersonDto>().RequireScope(ApiScopes.UsersRead);
    }

    private static async Task<IResult> ListPeopleAsync(HttpContext http, SwitchboardDbContext db, IPermissionService permissions, bool agents, CancellationToken ct)
    {
        await permissions.DemandAsync(Privileges.UsersView, ct: ct);
        var request = http.Request;
        ListCursor cursor;
        if (Paging.Text(request, "cursor", 4000) is { } raw)
        {
            cursor = ListCursor.Decode(raw);
        }
        else
        {
            var state = Paging.Text(request, "state", 10) ?? "active";
            if (state is not ("active" or "inactive" or "all"))
            {
                throw new ValidationException("state", "Use active, inactive or all.");
            }
            var filter = agents ? Paging.Id(request, "group_id") : Paging.Id(request, "department_id");
            cursor = new ListCursor(
                Paging.Text(request, "q", 200),
                null,
                Paging.Int(request, "page", 1, 1, Paging.MaxPage),
                Paging.Int(request, "per_page", Paging.DefaultPerPage, 1, Paging.MaxPerPage),
                false,
                $"{state}|{filter}");
        }

        var parts = (cursor.Extra ?? "active|").Split('|');
        var stateFilter = parts[0];
        Guid? idFilter = parts.Length > 1 && Guid.TryParse(parts[1], out var f) ? f : null;

        var query = db.Users.AsNoTracking().Where(u => !u.IsForgotten);
        query = agents ? query.Where(u => u.IsAgent || u.IsOrgAdmin) : query.Where(u => !u.IsAgent && !u.IsOrgAdmin);
        query = stateFilter switch
        {
            "inactive" => query.Where(u => !u.IsActive),
            "all" => query,
            _ => query.Where(u => u.IsActive),
        };
        if (idFilter is { } id)
        {
            query = agents ? query.Where(u => u.GroupMemberships.Any(m => m.GroupId == id)) : query.Where(u => u.DepartmentId == id);
        }
        if (cursor.Q is { } q)
        {
            query = query.Where(u => u.DisplayName.Contains(q) || (u.Email != null && u.Email.Contains(q)));
        }

        var total = await query.CountAsync(ct);
        var people = await query.OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
            .Skip((cursor.Page - 1) * cursor.PerPage).Take(cursor.PerPage)
            .Select(u => new { User = u, Groups = u.GroupMemberships.Where(m => !m.IsObserver).Select(m => m.GroupId).ToList() })
            .ToListAsync(ct);
        var data = people.Select(p => ToDto(p.User, agents ? p.Groups : null)).ToList();
        return ApiJson.Ok(new ListResponse<PersonDto>(data, Paging.Meta(http, total, cursor)));
    }

    private static async Task<IResult> GetPersonAsync(SwitchboardDbContext db, IPermissionService permissions, Guid id, bool agents, CancellationToken ct)
    {
        await permissions.DemandAsync(Privileges.UsersView, ct: ct);
        var person = await db.Users.AsNoTracking()
            .Where(u => u.Id == id && !u.IsForgotten && (agents ? u.IsAgent || u.IsOrgAdmin : !u.IsAgent && !u.IsOrgAdmin))
            .Select(u => new { User = u, Groups = u.GroupMemberships.Where(m => !m.IsObserver).Select(m => m.GroupId).ToList() })
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException();
        return ApiJson.Ok(ToDto(person.User, agents ? person.Groups : null));
    }

    private static async Task<IResult> CreateRequesterAsync(
        HttpContext http,
        SwitchboardDbContext db,
        IPermissionService permissions,
        IUserDirectory directory,
        IAuditLog audit,
        TimeProvider time,
        CancellationToken ct)
    {
        await permissions.DemandAsync(Privileges.UsersManage, ct: ct);
        var body = await ApiJson.ReadAsync<CreateRequesterRequest>(http.Request, ct);
        var errors = new Dictionary<string, string[]>();
        var name = body.Name?.Trim() ?? "";
        var email = body.Email?.Trim() ?? "";
        if (name.Length is 0 or > 200)
        {
            errors["name"] = [name.Length == 0 ? "Add the person's name." : "Keep the name under 200 characters."];
        }
        if (!IUserDirectory.IsValidEmail(email))
        {
            errors["email"] = ["Enter a valid email address."];
        }
        else if (await directory.FindByEmailAsync(email, ct) is { } existing)
        {
            errors["email"] = [$"That email is already used by {existing.DisplayName}."];
        }
        foreach (var (field, value, max) in new[] { ("jobTitle", body.JobTitle, 200), ("phone", body.Phone, 64), ("mobile", body.Mobile, 64), ("language", body.Language, 16) })
        {
            if (value?.Length > max)
            {
                errors[field] = [$"Keep this under {max} characters."];
            }
        }
        if (body.TimeZone is { } tz && !TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _))
        {
            errors["timeZone"] = ["Use an IANA time zone such as Europe/London."];
        }
        if (body.DepartmentId is { } dep && !await db.Departments.AnyAsync(d => d.Id == dep, ct))
        {
            errors["departmentId"] = ["Unknown department."];
        }
        if (body.LocationId is { } loc && !await db.Locations.AnyAsync(l => l.Id == loc, ct))
        {
            errors["locationId"] = ["Unknown location."];
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        var user = new User
        {
            DisplayName = name,
            Email = email,
            NormalizedEmail = IUserDirectory.Normalize(email),
            UserName = email,
            NormalizedUserName = IUserDirectory.Normalize(email),
            JobTitle = body.JobTitle?.Trim(),
            PhoneNumber = body.Phone?.Trim(),
            Mobile = body.Mobile?.Trim(),
            DepartmentId = body.DepartmentId,
            LocationId = body.LocationId,
            TimeZone = body.TimeZone,
            Language = string.IsNullOrWhiteSpace(body.Language) ? "en" : body.Language.Trim(),
            CreatedAt = time.GetUtcNow(),
        };
        db.Users.Add(user);
        audit.Record("users.created", "user", user.Id.ToString(), new { user.DisplayName, user.Email, kind = "requester" });
        await db.SaveChangesAsync(ct);
        http.Response.Headers.Location = $"{SwitchboardApi.Prefix}/requesters/{user.Id}";
        return ApiJson.Ok(ToDto(user, null), StatusCodes.Status201Created);
    }

    private static PersonDto ToDto(User u, IReadOnlyList<Guid>? groups) => new()
    {
        Id = u.Id,
        Name = u.DisplayName,
        Email = u.Email,
        JobTitle = u.JobTitle,
        Phone = u.PhoneNumber,
        Mobile = u.Mobile,
        DepartmentId = u.DepartmentId,
        LocationId = u.LocationId,
        ManagerId = u.ManagerId,
        TimeZone = u.TimeZone,
        Language = u.Language,
        IsAgent = u.IsAgent || u.IsOrgAdmin,
        IsActive = u.IsActive,
        IsVip = u.IsVip,
        CreatedAt = u.CreatedAt,
        GroupIds = groups,
        Availability = groups is null ? null : u.Availability,
    };

    // ---------------------------------------------------------------- reference data

    private static void MapReference(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSwitchboardApi("workspaces", "Configuration").WithApiConventions()
            .MapGet("", async (SwitchboardDbContext db, IPermissionService permissions, CancellationToken ct) =>
            {
                var ids = await WorkspaceIdsAsync(permissions, ct);
                var rows = await db.Workspaces.AsNoTracking()
                    .Where(w => !w.IsArchived && (ids == null || ids.Contains(w.Id)))
                    .OrderBy(w => !w.IsPrimary).ThenBy(w => w.Name)
                    .Select(w => new WorkspaceDto(w.Id, w.Key, w.Name, w.IsPrimary))
                    .ToListAsync(ct);
                return ApiJson.Ok(new ItemsResponse<WorkspaceDto>(rows));
            })
            .WithName("listWorkspaces").WithSummary("List your workspaces")
            .Produces<ItemsResponse<WorkspaceDto>>().AgentsOnly().RequireScope(ApiScopes.Profile);

        endpoints.MapSwitchboardApi("groups", "Directory").WithApiConventions()
            .MapGet("", async (HttpContext http, SwitchboardDbContext db, IPermissionService permissions, CancellationToken ct) =>
            {
                var ids = await WorkspaceIdsAsync(permissions, ct, Paging.Id(http.Request, "workspace_id"));
                var groups = await db.AgentGroups.AsNoTracking()
                    .Where(g => ids == null || ids.Contains(g.WorkspaceId))
                    .OrderBy(g => g.Name)
                    .Select(g => new
                    {
                        g.Id, g.WorkspaceId, g.Name, g.Description, g.AssignmentMode,
                        Members = g.Members.Where(m => !m.IsObserver).Select(m => m.UserId).ToList(),
                        Observers = g.Members.Where(m => m.IsObserver).Select(m => m.UserId).ToList(),
                    })
                    .ToListAsync(ct);
                return ApiJson.Ok(new ItemsResponse<GroupDto>(groups.Select(g => new GroupDto(g.Id, g.WorkspaceId, g.Name, g.Description, g.AssignmentMode, g.Members, g.Observers)).ToList()));
            })
            .WithName("listGroups").WithSummary("List agent groups")
            .WithDescription("workspace_id narrows to one workspace.")
            .Produces<ItemsResponse<GroupDto>>().AgentsOnly().RequireScope(ApiScopes.UsersRead);

        endpoints.MapSwitchboardApi("departments", "Directory").WithApiConventions()
            .MapGet("", async (SwitchboardDbContext db, CancellationToken ct) =>
                ApiJson.Ok(new ItemsResponse<DepartmentDto>(await db.Departments.AsNoTracking().OrderBy(d => d.Name)
                    .Select(d => new DepartmentDto(d.Id, d.Name, d.Description, d.HeadUserId, d.PrimeUserId, d.Domains))
                    .ToListAsync(ct))))
            .WithName("listDepartments").WithSummary("List departments")
            .Produces<ItemsResponse<DepartmentDto>>().AgentsOnly().RequireScope(ApiScopes.UsersRead);

        endpoints.MapSwitchboardApi("locations", "Directory").WithApiConventions()
            .MapGet("", async (SwitchboardDbContext db, CancellationToken ct) =>
                ApiJson.Ok(new ItemsResponse<LocationDto>(await db.Locations.AsNoTracking().OrderBy(l => l.Name)
                    .Select(l => new LocationDto(l.Id, l.Name, l.ParentId, l.AddressLine1, l.AddressLine2, l.City, l.State, l.PostalCode, l.Country))
                    .ToListAsync(ct))))
            .WithName("listLocations").WithSummary("List locations")
            .Produces<ItemsResponse<LocationDto>>().AgentsOnly().RequireScope(ApiScopes.UsersRead);

        endpoints.MapSwitchboardApi("ticket-statuses", "Configuration").WithApiConventions()
            .MapGet("", async (HttpContext http, SwitchboardDbContext db, IPermissionService permissions, CancellationToken ct) =>
            {
                var ids = await WorkspaceIdsAsync(permissions, ct, Paging.Id(http.Request, "workspace_id"));
                return ApiJson.Ok(new ItemsResponse<TicketStatusDto>(await db.TicketStatuses.AsNoTracking()
                    .Where(s => ids == null || ids.Contains(s.WorkspaceId))
                    .OrderBy(s => s.WorkspaceId).ThenBy(s => s.Order)
                    .Select(s => new TicketStatusDto(s.Id, s.WorkspaceId, s.Name, s.RequesterLabel, s.Category, s.SlaTimerOn, s.IsDefault, s.Order))
                    .ToListAsync(ct)));
            })
            .WithName("listTicketStatuses").WithSummary("List ticket statuses")
            .Produces<ItemsResponse<TicketStatusDto>>().AgentsOnly().RequireScope(ApiScopes.TicketsRead);

        endpoints.MapSwitchboardApi("ticket-categories", "Configuration").WithApiConventions()
            .MapGet("", async (HttpContext http, SwitchboardDbContext db, IPermissionService permissions, CancellationToken ct) =>
            {
                var ids = await WorkspaceIdsAsync(permissions, ct, Paging.Id(http.Request, "workspace_id"));
                return ApiJson.Ok(new ItemsResponse<TicketCategoryDto>(await db.TicketCategories.AsNoTracking()
                    .Where(c => ids == null || ids.Contains(c.WorkspaceId))
                    .OrderBy(c => c.Level).ThenBy(c => c.Order).ThenBy(c => c.Name)
                    .Select(c => new TicketCategoryDto(c.Id, c.WorkspaceId, c.ParentId, c.Name, c.Level, c.Order))
                    .ToListAsync(ct)));
            })
            .WithName("listTicketCategories").WithSummary("List ticket categories")
            .WithDescription("Three levels: category, sub-category (parentId = category) and item (parentId = sub-category).")
            .Produces<ItemsResponse<TicketCategoryDto>>().AgentsOnly().RequireScope(ApiScopes.TicketsRead);

        endpoints.MapSwitchboardApi("ticket-sources", "Configuration").WithApiConventions()
            .MapGet("", async (SwitchboardDbContext db, CancellationToken ct) =>
                ApiJson.Ok(new ItemsResponse<TicketSourceDto>(await db.TicketSources.AsNoTracking().OrderBy(s => s.Order)
                    .Select(s => new TicketSourceDto(s.Id, s.Key, s.Name, s.Order))
                    .ToListAsync(ct))))
            .WithName("listTicketSources").WithSummary("List ticket sources")
            .Produces<ItemsResponse<TicketSourceDto>>().AgentsOnly().RequireScope(ApiScopes.TicketsRead);

        endpoints.MapSwitchboardApi("ticket-fields", "Configuration").WithApiConventions()
            .MapGet("", async (HttpContext http, SwitchboardDbContext db, IPermissionService permissions, CancellationToken ct) =>
            {
                var ids = await WorkspaceIdsAsync(permissions, ct, Paging.Id(http.Request, "workspace_id"));
                var rows = await db.CustomFieldDefinitions.AsNoTracking()
                    .Where(d => d.EntityType == "ticket" && d.IsActive && (d.WorkspaceId == null || ids == null || ids.Contains(d.WorkspaceId.Value)))
                    .OrderBy(d => d.Order).ThenBy(d => d.Label)
                    .ToListAsync(ct);
                return ApiJson.Ok(new ItemsResponse<TicketFieldDto>(rows.Select(d => new TicketFieldDto(
                    d.Id, d.WorkspaceId, d.Key, d.Label, d.RequesterLabel, d.FieldType, Options(d.OptionsJson),
                    d.RequiredForAgents, d.RequiredOnResolve, d.RequiredForRequesters, d.VisibleToRequesters, d.EditableByRequesters,
                    d.HelpText, d.DefaultValue, d.Section, d.Order)).ToList()));
            })
            .WithName("listTicketFields").WithSummary("List ticket custom fields")
            .WithDescription("Keys are what customFields uses when creating or updating tickets.")
            .Produces<ItemsResponse<TicketFieldDto>>().AgentsOnly().RequireScope(ApiScopes.TicketsRead);
    }

    /// <summary>Workspaces the agent belongs to (null = all, for org admins), optionally narrowed to one.</summary>
    private static async Task<IReadOnlyList<Guid>?> WorkspaceIdsAsync(IPermissionService permissions, CancellationToken ct, Guid? only = null)
    {
        var p = await permissions.CurrentAsync(ct) ?? throw new ForbiddenException();
        IReadOnlyList<Guid>? ids = p.IsOrgAdmin ? null : p.Workspaces.Keys.ToList();
        if (only is { } one)
        {
            ids = ids is null || ids.Contains(one) ? [one] : [];
        }
        return ids;
    }

    private static JsonElement Options(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("[]").RootElement.Clone();
        }
    }
}
