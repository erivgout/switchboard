using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Modules.Admin.Services;

public sealed record DepartmentRow(Guid Id, string Name, string? Head, IReadOnlyList<string> Domains, int People);

public sealed record DepartmentInput(
    string Name,
    string? Description,
    Guid? HeadUserId,
    Guid? PrimeUserId,
    IReadOnlyList<string> Domains,
    IReadOnlyDictionary<string, JsonElement?>? CustomFields);

public sealed record LocationRow(Location Location, int Depth, int People);

public sealed record LocationInput
{
    public string Name { get; init; } = "";
    public Guid? ParentId { get; init; }
    public string? AddressLine1 { get; init; }
    public string? AddressLine2 { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? PostalCode { get; init; }
    public string? Country { get; init; }
    public string? ContactName { get; init; }
    public string? ContactEmail { get; init; }
    public string? ContactPhone { get; init; }
}

/// <summary>Departments (FR-IAM-5) and hierarchical locations (FR-IAM-6).</summary>
public sealed partial class OrgStructureService(SwitchboardDbContext db, AdminAccess access, ICustomFieldService customFields, IAuditLog audit)
{
    // ---------------------------------------------------------------- departments

    public async Task<IReadOnlyList<DepartmentRow>> ListDepartmentsAsync(CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.DepartmentsManage, ct: ct);
        var rows = await db.Departments.AsNoTracking().OrderBy(d => d.Name)
            .Select(d => new
            {
                d.Id, d.Name, d.Domains,
                Head = db.Users.Where(u => u.Id == d.HeadUserId).Select(u => u.DisplayName).FirstOrDefault(),
                People = db.Users.Count(u => u.DepartmentId == d.Id && u.IsActive),
            })
            .ToListAsync(ct);
        return rows.Select(r => new DepartmentRow(r.Id, r.Name, r.Head, r.Domains, r.People)).ToList();
    }

    public async Task<(Department Department, int People, int Tickets)> GetDepartmentAsync(Guid id, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.DepartmentsManage, ct: ct);
        var d = await db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        return (d, await db.Users.CountAsync(u => u.DepartmentId == id, ct), await db.Tickets.CountAsync(t => t.DepartmentId == id, ct));
    }

    public async Task<Department> SaveDepartmentAsync(Guid? id, DepartmentInput input, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.DepartmentsManage, ct: ct);
        var department = id is { } existing
            ? await db.Departments.FirstOrDefaultAsync(d => d.Id == existing, ct) ?? throw new NotFoundException()
            : new Department();
        var errors = new Dictionary<string, string[]>();

        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 200)
        {
            errors["Name"] = [name.Length == 0 ? "Name the department." : "Keep the name under 200 characters."];
        }
        else if (await db.Departments.AnyAsync(d => d.Name == name && d.Id != department.Id, ct))
        {
            errors["Name"] = [$"A department called {name} already exists."];
        }
        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        if (description is { Length: > 1000 })
        {
            errors["Description"] = ["Keep the description under 1,000 characters."];
        }
        foreach (var (key, userId) in new[] { ("HeadUserId", input.HeadUserId), ("PrimeUserId", input.PrimeUserId) })
        {
            if (userId is { } u && !await db.Users.AnyAsync(x => x.Id == u && x.IsActive, ct))
            {
                errors[key] = ["Choose an active person."];
            }
        }

        var domains = new List<string>();
        foreach (var raw in input.Domains ?? [])
        {
            var domain = raw.Trim().TrimStart('@').ToLowerInvariant();
            if (domain.Length == 0)
            {
                continue;
            }
            if (!DomainPattern().IsMatch(domain))
            {
                errors.TryAdd("Domains", [$"{domain} isn't a domain name. Use the part after @, like finance.example.com."]);
            }
            else if (!domains.Contains(domain))
            {
                domains.Add(domain);
            }
        }
        if (domains.Count > 0)
        {
            var others = await db.Departments.AsNoTracking().Where(d => d.Id != department.Id).Select(d => new { d.Name, d.Domains }).ToListAsync(ct);
            var clash = others.SelectMany(o => o.Domains.Select(x => (o.Name, Domain: x.ToLowerInvariant()))).FirstOrDefault(x => domains.Contains(x.Domain));
            if (clash.Domain is not null)
            {
                errors.TryAdd("Domains", [$"{clash.Domain} already places people in {clash.Name}. A domain can belong to one department."]);
            }
        }

        string? customJson = null;
        if (input.CustomFields is { Count: > 0 })
        {
            try
            {
                customJson = await customFields.ApplyAsync("department", null, department.CustomFieldsJson, input.CustomFields, FieldAudience.Agent, ct);
            }
            catch (ValidationException ex)
            {
                foreach (var (k, v) in ex.Errors)
                {
                    errors.TryAdd(k, v);
                }
            }
        }
        var missing = await customFields.MissingRequiredAsync("department", null, customJson ?? department.CustomFieldsJson, RequiredMoment.AgentSave, ct);
        if (missing.Count > 0)
        {
            errors.TryAdd("CustomFields", [.. missing]);
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        department.Name = name;
        department.Description = description;
        department.HeadUserId = input.HeadUserId;
        department.PrimeUserId = input.PrimeUserId;
        department.Domains = domains;
        department.CustomFieldsJson = customJson ?? department.CustomFieldsJson;
        if (id is null)
        {
            db.Departments.Add(department);
        }
        audit.Record(id is null ? "departments.created" : "departments.updated", "department", department.Id.ToString(), new { name, domains });
        await db.SaveChangesAsync(ct);
        return department;
    }

    /// <summary>People and tickets in the department move to <paramref name="replacementId"/> or lose their department.</summary>
    public async Task DeleteDepartmentAsync(Guid id, Guid? replacementId, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.DepartmentsManage, ct: ct);
        var department = await db.Departments.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException();
        Department? replacement = null;
        if (replacementId is { } rid)
        {
            replacement = await db.Departments.FirstOrDefaultAsync(d => d.Id == rid && d.Id != id, ct)
                ?? throw new ValidationException("ReplacementId", "That replacement department no longer exists.");
        }
        await db.InTransactionAsync(async () =>
        {
            var tickets = await TicketRemap.ApplyAsync(db, t => t.DepartmentId == id,
                t =>
                {
                    t.DepartmentId = replacement?.Id;
                    return [new FieldChange("Department", department.Name, replacement?.Name)];
                },
                replacement is null ? $"Department {department.Name} was deleted" : $"Moved to department {replacement.Name}", ct);
            var people = await db.Users.Where(u => u.DepartmentId == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DepartmentId, replacementId), ct);
            db.Departments.Remove(department);
            audit.Record("departments.deleted", "department", id.ToString(), new { name = department.Name, people, tickets, replacementId });
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    // ---------------------------------------------------------------- locations

    /// <summary>All locations as a depth-first list (parents before children).</summary>
    public async Task<IReadOnlyList<LocationRow>> ListLocationsAsync(CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.LocationsManage, ct: ct);
        var all = await db.Locations.AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);
        var people = await db.Users.Where(u => u.LocationId != null && u.IsActive).GroupBy(u => u.LocationId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var rows = new List<LocationRow>();
        var ids = all.Select(l => l.Id).ToHashSet();
        void Walk(Guid? parent, int depth, HashSet<Guid> seen)
        {
            foreach (var l in all.Where(l => l.ParentId == parent || (parent is null && l.ParentId is { } p && !ids.Contains(p))))
            {
                if (seen.Add(l.Id))
                {
                    rows.Add(new LocationRow(l, depth, people.GetValueOrDefault(l.Id)));
                    Walk(l.Id, depth + 1, seen);
                }
            }
        }
        Walk(null, 0, []);
        return rows;
    }

    public async Task<(Location Location, int People, int Children)> GetLocationAsync(Guid id, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.LocationsManage, ct: ct);
        var l = await db.Locations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        return (l, await db.Users.CountAsync(u => u.LocationId == id, ct), await db.Locations.CountAsync(x => x.ParentId == id, ct));
    }

    public async Task<Location> SaveLocationAsync(Guid? id, LocationInput input, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.LocationsManage, ct: ct);
        var location = id is { } existing
            ? await db.Locations.FirstOrDefaultAsync(l => l.Id == existing, ct) ?? throw new NotFoundException()
            : new Location();
        var errors = new Dictionary<string, string[]>();
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 200)
        {
            errors["Name"] = [name.Length == 0 ? "Name the location." : "Keep the name under 200 characters."];
        }
        else if (await db.Locations.AnyAsync(l => l.Name == name && l.ParentId == input.ParentId && l.Id != location.Id, ct))
        {
            errors["Name"] = [$"{name} already exists here."];
        }
        if (input.ParentId is { } parent)
        {
            if (parent == location.Id)
            {
                errors["ParentId"] = ["A location can't be inside itself."];
            }
            else if (!await db.Locations.AnyAsync(l => l.Id == parent, ct))
            {
                errors["ParentId"] = ["That parent location no longer exists."];
            }
            else if (id is not null && await ParentChainReachesAsync(parent, location.Id, ct))
            {
                errors["ParentId"] = ["That location is inside this one. Choose a parent outside it."];
            }
        }
        string? Clean(string key, string? value)
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (v is { Length: > 200 })
            {
                errors.TryAdd(key, ["Keep this under 200 characters."]);
            }
            return v;
        }
        var contactEmail = Clean(nameof(LocationInput.ContactEmail), input.ContactEmail);
        if (contactEmail is not null && !IUserDirectory.IsValidEmail(contactEmail))
        {
            errors.TryAdd(nameof(LocationInput.ContactEmail), ["Enter a valid email address."]);
        }
        var values = new
        {
            A1 = Clean(nameof(LocationInput.AddressLine1), input.AddressLine1),
            A2 = Clean(nameof(LocationInput.AddressLine2), input.AddressLine2),
            City = Clean(nameof(LocationInput.City), input.City),
            State = Clean(nameof(LocationInput.State), input.State),
            Postal = Clean(nameof(LocationInput.PostalCode), input.PostalCode),
            Country = Clean(nameof(LocationInput.Country), input.Country),
            ContactName = Clean(nameof(LocationInput.ContactName), input.ContactName),
            ContactPhone = Clean(nameof(LocationInput.ContactPhone), input.ContactPhone),
        };
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        location.Name = name;
        location.ParentId = input.ParentId;
        location.AddressLine1 = values.A1;
        location.AddressLine2 = values.A2;
        location.City = values.City;
        location.State = values.State;
        location.PostalCode = values.Postal;
        location.Country = values.Country;
        location.ContactName = values.ContactName;
        location.ContactEmail = contactEmail;
        location.ContactPhone = values.ContactPhone;
        if (id is null)
        {
            db.Locations.Add(location);
        }
        audit.Record(id is null ? "locations.created" : "locations.updated", "location", location.Id.ToString(), new { name, parentId = input.ParentId });
        await db.SaveChangesAsync(ct);
        return location;
    }

    /// <summary>A location without sub-locations; its people move to <paramref name="replacementId"/> or lose their location.</summary>
    public async Task DeleteLocationAsync(Guid id, Guid? replacementId, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.LocationsManage, ct: ct);
        var location = await db.Locations.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException();
        if (await db.Locations.AnyAsync(l => l.ParentId == id, ct))
        {
            throw new ValidationException("Location", $"{location.Name} contains other locations. Move or delete those first.");
        }
        if (replacementId is { } rid && (rid == id || !await db.Locations.AnyAsync(l => l.Id == rid, ct)))
        {
            throw new ValidationException("ReplacementId", "That replacement location no longer exists.");
        }
        await db.InTransactionAsync(async () =>
        {
            var people = await db.Users.Where(u => u.LocationId == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.LocationId, replacementId), ct);
            db.Locations.Remove(location);
            audit.Record("locations.deleted", "location", id.ToString(), new { name = location.Name, people, replacementId });
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    internal async Task<bool> ParentChainReachesAsync(Guid start, Guid target, CancellationToken ct)
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
            current = await db.Locations.Where(l => l.Id == c).Select(l => l.ParentId).FirstOrDefaultAsync(ct);
        }
        return false;
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex DomainPattern();
}
