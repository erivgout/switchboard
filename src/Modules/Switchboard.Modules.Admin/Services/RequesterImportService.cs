using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Admin.Data;

namespace Switchboard.Modules.Admin.Services;

public sealed record ImportField(string Key, string Title, bool Required, IReadOnlyList<string> Aliases);

public sealed record ImportRowError(int Row, string? Email, string Message);

public sealed record ImportRowResult(int Row, string? Email, string? Name, ImportOutcome Outcome, string? Message);

public enum ImportOutcome
{
    Create,
    Update,
    Skip,
    Error,
}

public sealed record ImportPreview(int Total, int Create, int Update, int Skip, int Errors, IReadOnlyList<ImportRowResult> Rows);

/// <summary>CSV import of requesters with column mapping, a validation preview and an error report (FR-ADM-7).</summary>
public sealed partial class RequesterImportService(
    SwitchboardDbContext db,
    AdminAccess access,
    PeopleService people,
    ICustomFieldService customFields,
    IAuditLog audit,
    TimeProvider time)
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const int MaxRows = 10_000;
    public const string CustomPrefix = "custom:";

    public static readonly IReadOnlyList<ImportField> StandardFields =
    [
        new("name", "Name", true, ["name", "full name", "display name", "requester"]),
        new("email", "Email", true, ["email", "email address", "primary email", "e-mail", "mail"]),
        new("secondary_emails", "Other email addresses", false, ["secondary emails", "other emails", "other email addresses", "alternate emails", "aliases"]),
        new("phone", "Work phone", false, ["phone", "work phone", "telephone", "phone number"]),
        new("mobile", "Mobile", false, ["mobile", "mobile phone", "cell", "cell phone"]),
        new("job_title", "Job title", false, ["job title", "title", "position", "role"]),
        new("department", "Department", false, ["department", "dept", "team"]),
        new("location", "Location", false, ["location", "office", "site"]),
        new("manager_email", "Manager's email", false, ["manager", "manager email", "manager's email", "reports to"]),
        new("vip", "VIP", false, ["vip", "is vip"]),
        new("time_zone", "Time zone", false, ["time zone", "timezone", "tz"]),
        new("language", "Language", false, ["language", "locale"]),
        new("address", "Address", false, ["address", "street address"]),
    ];

    public async Task<IReadOnlyList<ImportField>> FieldsAsync(CancellationToken ct = default)
    {
        var custom = (await customFields.GetDefinitionsAsync(FieldEntities.User, null, ct))
            .Where(d => d.FieldType is not (CustomFieldType.Content or CustomFieldType.Lookup))
            .Select(d => new ImportField(CustomPrefix + d.Key, d.Label, false, [d.Label.ToLowerInvariant(), d.Key]));
        return [.. StandardFields, .. custom];
    }

    public async Task<IReadOnlyList<AdmImport>> RecentAsync(CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, ct: ct);
        return await db.Set<AdmImport>().AsNoTracking().OrderByDescending(i => i.CreatedAt).Take(10)
            .Select(i => new AdmImport
            {
                Id = i.Id, FileName = i.FileName, Status = i.Status, CreatedAt = i.CreatedAt, CompletedAt = i.CompletedAt, TotalRows = i.TotalRows,
                CreatedCount = i.CreatedCount, UpdatedCount = i.UpdatedCount, SkippedCount = i.SkippedCount, ErrorCount = i.ErrorCount,
            })
            .ToListAsync(ct);
    }

    public async Task<AdmImport> UploadAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, ct: ct);
        if (!fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("File", "Upload a .csv file. In a spreadsheet, use Save as, then CSV.");
        }
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length == 0 || buffer.Length > MaxBytes)
        {
            throw new ValidationException("File", "The file must have data and be smaller than 5 MB.");
        }
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        var rows = Csv.Parse(text, MaxRows + 2);
        if (rows.Count < 2)
        {
            throw new ValidationException("File", "The file needs a header row and at least one person.");
        }
        if (rows.Count > MaxRows + 1)
        {
            throw new ValidationException("File", $"Import at most {MaxRows:N0} people at a time. Split the file and import each part.");
        }
        var headers = rows[0].Select(h => h.Trim()).ToArray();
        if (headers.Any(h => h.Length == 0) || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
        {
            throw new ValidationException("File", "Every column needs a name in the header row, and each name can appear once.");
        }

        var fields = await FieldsAsync(ct);
        var mapping = new Dictionary<string, string>();
        foreach (var field in fields)
        {
            var match = headers.FirstOrDefault(h => field.Aliases.Append(field.Title).Any(alias => Normalize(alias) == Normalize(h)));
            if (match is not null && !mapping.ContainsValue(match))
            {
                mapping[field.Key] = match;
            }
        }
        var import = new AdmImport
        {
            FileName = Path.GetFileName(fileName).Length > 255 ? Path.GetFileName(fileName)[..255] : Path.GetFileName(fileName),
            CsvText = text,
            MappingJson = JsonSerializer.Serialize(mapping, JsonDefaults.Options),
            CreatedById = access.ActorUserId,
            CreatedAt = time.GetUtcNow(),
            TotalRows = rows.Count - 1,
        };
        db.Set<AdmImport>().Add(import);
        await db.SaveChangesAsync(ct);
        return import;
    }

    public async Task<(AdmImport Import, string[] Headers, Dictionary<string, string> Mapping)> GetAsync(Guid id, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, ct: ct);
        var import = await db.Set<AdmImport>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException();
        var headers = Csv.Parse(import.CsvText, 1).FirstOrDefault()?.Select(h => h.Trim()).ToArray() ?? [];
        return (import, headers, Mapping(import));
    }

    public async Task SaveMappingAsync(Guid id, IReadOnlyDictionary<string, string?> mapping, bool updateExisting, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, ct: ct);
        var import = await db.Set<AdmImport>().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException();
        if (import.Status == ImportStatus.Completed)
        {
            throw new ValidationException("Mapping", "This import already ran. Upload the file again to import it again.");
        }
        var headers = Csv.Parse(import.CsvText, 1)[0].Select(h => h.Trim()).ToHashSet(StringComparer.Ordinal);
        var keys = (await FieldsAsync(ct)).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var clean = mapping.Where(m => keys.Contains(m.Key) && m.Value is { Length: > 0 } && headers.Contains(m.Value))
            .ToDictionary(m => m.Key, m => m.Value!);
        var errors = new Dictionary<string, string[]>();
        foreach (var required in StandardFields.Where(f => f.Required && !clean.ContainsKey(f.Key)))
        {
            errors[$"Mapping.{required.Key}"] = [$"Choose the column that holds each person's {required.Title.ToLowerInvariant()}."];
        }
        var reused = clean.GroupBy(m => m.Value).FirstOrDefault(g => g.Count() > 1);
        if (reused is not null)
        {
            errors["Mapping"] = [$"The column {reused.Key} is used for more than one field. Map each column once."];
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
        import.MappingJson = JsonSerializer.Serialize(clean, JsonDefaults.Options);
        import.UpdateExisting = updateExisting;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Validates every row without changing anything.</summary>
    public async Task<ImportPreview> PreviewAsync(Guid id, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, ct: ct);
        var import = await db.Set<AdmImport>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException();
        var plans = await PlanAsync(import, ct);
        return Summarize(plans);
    }

    /// <summary>Imports the valid rows in one transaction and records the result and error report.</summary>
    public async Task<ImportPreview> RunAsync(Guid id, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, ct: ct);
        return await db.InTransactionAsync(async () =>
        {
            await db.LockEmailsAsync(ct);
            var import = await db.Set<AdmImport>().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException();
            if (import.Status == ImportStatus.Completed)
            {
                throw new ValidationException("Import", "This import already ran. Upload the file again to import it again.");
            }
            var plans = await PlanAsync(import, ct);
            var byEmail = new Dictionary<string, User>(StringComparer.Ordinal);
            var domains = await db.Departments.AsNoTracking().Select(d => new { d.Id, d.Domains }).ToListAsync(ct);

            foreach (var plan in plans.Where(p => p.Outcome is ImportOutcome.Create or ImportOutcome.Update))
            {
                User user;
                var email = plan.Email!;
                if (plan.Outcome == ImportOutcome.Create)
                {
                    var domain = email[(email.IndexOf('@') + 1)..].ToLowerInvariant();
                    user = new User
                    {
                        Email = email,
                        NormalizedEmail = IUserDirectory.Normalize(email),
                        UserName = email,
                        NormalizedUserName = IUserDirectory.Normalize(email),
                        EmailConfirmed = true,
                        LockoutEnabled = true,
                        DisplayName = plan.Name!,
                        CreatedAt = time.GetUtcNow(),
                        DepartmentId = domains.FirstOrDefault(d => d.Domains.Any(x => x.Equals(domain, StringComparison.OrdinalIgnoreCase)))?.Id,
                    };
                    db.Users.Add(user);
                }
                else
                {
                    user = await db.Users.FirstAsync(u => u.Id == plan.ExistingUserId, ct);
                    if (plan.Name is not null)
                    {
                        user.DisplayName = plan.Name;
                    }
                }
                user.PhoneNumber = plan.Phone ?? user.PhoneNumber;
                user.Mobile = plan.Mobile ?? user.Mobile;
                user.JobTitle = plan.JobTitle ?? user.JobTitle;
                user.Address = plan.Address ?? user.Address;
                user.TimeZone = plan.TimeZone ?? user.TimeZone;
                user.Language = plan.Language ?? user.Language;
                user.DepartmentId = plan.DepartmentId ?? user.DepartmentId;
                user.LocationId = plan.LocationId ?? user.LocationId;
                user.IsVip = plan.Vip ?? user.IsVip;
                user.CustomFieldsJson = plan.CustomFieldsJson ?? user.CustomFieldsJson;
                foreach (var secondary in plan.SecondaryEmails)
                {
                    db.UserEmails.Add(new UserEmail { UserId = user.Id, Email = secondary, NormalizedEmail = IUserDirectory.Normalize(secondary) });
                }
                byEmail[IUserDirectory.Normalize(email)] = user;
            }

            // Managers last, so a manager listed further down the file already exists.
            foreach (var plan in plans.Where(p => p.ManagerEmail is not null && p.Outcome is ImportOutcome.Create or ImportOutcome.Update))
            {
                var user = byEmail[IUserDirectory.Normalize(plan.Email!)];
                var key = IUserDirectory.Normalize(plan.ManagerEmail!);
                var managerId = byEmail.TryGetValue(key, out var m) ? m.Id : plan.ManagerId;
                if (managerId is not { } mid)
                {
                    plan.Warn($"The manager {plan.ManagerEmail} wasn't imported, so the manager wasn't set.");
                    continue;
                }
                if (mid == user.Id || await people.ManagerChainReachesAsync(mid, user.Id, ct))
                {
                    plan.Warn($"The manager {plan.ManagerEmail} would create a reporting loop, so the manager wasn't set.");
                    continue;
                }
                user.ManagerId = mid;
            }

            var summary = Summarize(plans);
            import.Status = ImportStatus.Completed;
            import.CompletedAt = time.GetUtcNow();
            import.CreatedCount = summary.Create;
            import.UpdatedCount = summary.Update;
            import.SkippedCount = summary.Skip;
            import.ErrorCount = summary.Errors;
            import.ErrorsJson = JsonSerializer.Serialize(
                plans.Where(p => p.Warning is not null || (p.Message is not null && p.Outcome is ImportOutcome.Skip or ImportOutcome.Error))
                    .Select(p => new ImportRowError(p.Row, p.Email, p.Warning ?? p.Message!)).ToList(),
                JsonDefaults.Options);
            audit.Record("users.imported", "import", import.Id.ToString(), new
            {
                file = import.FileName, created = summary.Create, updated = summary.Update, skipped = summary.Skip, errors = summary.Errors,
            });
            await db.SaveChangesAsync(ct);
            return summary;
        }, ct);
    }

    /// <summary>CSV of the rows that weren't imported, with the reason, for fixing and re-uploading.</summary>
    public async Task<byte[]> ErrorReportAsync(Guid id, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.UsersManage, ct: ct);
        var import = await db.Set<AdmImport>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException();
        var errors = import.Status == ImportStatus.Completed
            ? JsonSerializer.Deserialize<List<ImportRowError>>(import.ErrorsJson, JsonDefaults.Options) ?? []
            : (await PlanAsync(import, ct)).Where(p => p.Outcome is ImportOutcome.Error or ImportOutcome.Skip)
                .Select(p => new ImportRowError(p.Row, p.Email, p.Message ?? "")).ToList();
        var rows = Csv.Parse(import.CsvText, MaxRows + 2);
        var output = new List<IEnumerable<string?>> { rows[0].Prepend("Problem").Prepend("Row") };
        foreach (var e in errors.OrderBy(e => e.Row))
        {
            var source = e.Row - 1 < rows.Count && e.Row >= 2 ? rows[e.Row - 1] : [];
            output.Add(source.Prepend(e.Message).Prepend(e.Row.ToString(CultureInfo.InvariantCulture)));
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Csv.Write(output))).ToArray();
    }

    // ---------------------------------------------------------------- planning

    private sealed class RowPlan
    {
        public int Row { get; init; }
        public string? Email { get; set; }
        public string? Name { get; set; }
        public ImportOutcome Outcome { get; set; } = ImportOutcome.Create;
        public string? Message { get; set; }
        public string? Warning { get; private set; }
        public Guid? ExistingUserId { get; set; }
        public List<string> SecondaryEmails { get; } = [];
        public string? Phone { get; set; }
        public string? Mobile { get; set; }
        public string? JobTitle { get; set; }
        public string? Address { get; set; }
        public string? TimeZone { get; set; }
        public string? Language { get; set; }
        public Guid? DepartmentId { get; set; }
        public Guid? LocationId { get; set; }
        public string? ManagerEmail { get; set; }
        public Guid? ManagerId { get; set; }
        public bool? Vip { get; set; }
        public string? CustomFieldsJson { get; set; }

        public void Fail(string message)
        {
            if (Outcome != ImportOutcome.Error)
            {
                Outcome = ImportOutcome.Error;
                Message = message;
            }
        }

        public void Warn(string message) => Warning = message;
    }

    private async Task<List<RowPlan>> PlanAsync(AdmImport import, CancellationToken ct)
    {
        var rows = Csv.Parse(import.CsvText, MaxRows + 2);
        var headers = rows[0].Select(h => h.Trim()).ToList();
        var mapping = Mapping(import);
        string? Cell(string[] row, string key)
        {
            if (!mapping.TryGetValue(key, out var header))
            {
                return null;
            }
            var index = headers.IndexOf(header);
            var value = index >= 0 && index < row.Length ? row[index].Trim() : "";
            return value.Length == 0 ? null : value;
        }

        // Look-ups, loaded once.
        var departments = await db.Departments.AsNoTracking().Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        var locations = await db.Locations.AsNoTracking().Select(l => new { l.Id, l.Name }).ToListAsync(ct);
        var definitions = (await customFields.GetDefinitionsAsync(FieldEntities.User, null, ct)).ToDictionary(d => d.Key);
        var addresses = rows.Skip(1)
            .SelectMany(r => new[] { Cell(r, "email"), Cell(r, "manager_email") }.Concat(SplitEmails(Cell(r, "secondary_emails"))))
            .OfType<string>().Select(IUserDirectory.Normalize).Distinct().ToList();
        var owners = new Dictionary<string, (Guid Id, bool IsAgent, bool IsForgotten, bool IsActive, string CustomJson)>(StringComparer.Ordinal);
        foreach (var chunk in addresses.Chunk(1000))
        {
            var list = chunk.ToList();
            foreach (var u in await db.Users.AsNoTracking().Where(u => u.NormalizedEmail != null && list.Contains(u.NormalizedEmail))
                         .Select(u => new { u.NormalizedEmail, u.Id, Agent = u.IsAgent || u.IsOrgAdmin, u.IsForgotten, u.IsActive, u.CustomFieldsJson }).ToListAsync(ct))
            {
                owners[u.NormalizedEmail!] = (u.Id, u.Agent, u.IsForgotten, u.IsActive, u.CustomFieldsJson);
            }
            var secondary = await (from e in db.UserEmails.AsNoTracking()
                                   join u in db.Users.AsNoTracking() on e.UserId equals u.Id
                                   where list.Contains(e.NormalizedEmail)
                                   select new { e.NormalizedEmail, u.Id, Agent = u.IsAgent || u.IsOrgAdmin, u.IsForgotten, u.IsActive, u.CustomFieldsJson })
                .ToListAsync(ct);
            foreach (var e in secondary)
            {
                owners.TryAdd(e.NormalizedEmail, (e.Id, e.Agent, e.IsForgotten, e.IsActive, e.CustomFieldsJson));
            }
        }

        var fileEmails = rows.Skip(1).Select(r => Cell(r, "email")).OfType<string>().Select(IUserDirectory.Normalize).ToHashSet(StringComparer.Ordinal);
        var plans = new List<RowPlan>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var plan = new RowPlan { Row = i + 1, Email = Cell(row, "email"), Name = Cell(row, "name") };
            plans.Add(plan);

            if (plan.Email is null || !IUserDirectory.IsValidEmail(plan.Email))
            {
                plan.Fail(plan.Email is null ? "The email is empty." : $"{plan.Email} isn't a valid email address.");
                continue;
            }
            var normalized = IUserDirectory.Normalize(plan.Email);
            if (seen.TryGetValue(normalized, out var firstRow))
            {
                plan.Fail($"{plan.Email} is already on row {firstRow}.");
                continue;
            }
            seen[normalized] = plan.Row;

            string existingJson = "{}";
            if (owners.TryGetValue(normalized, out var owner))
            {
                existingJson = owner.CustomJson;
                if (!import.UpdateExisting)
                {
                    plan.Outcome = ImportOutcome.Skip;
                    plan.Message = "Already in Switchboard. Turn on updating to change their details.";
                    continue;
                }
                if (owner.IsAgent || owner.IsForgotten || !owner.IsActive)
                {
                    plan.Outcome = ImportOutcome.Skip;
                    plan.Message = owner.IsAgent ? "Belongs to an agent. Imports only change requesters." : "Belongs to an inactive account, so it wasn't changed.";
                    continue;
                }
                plan.Outcome = ImportOutcome.Update;
                plan.ExistingUserId = owner.Id;
            }
            else if (plan.Name is null)
            {
                plan.Fail("The name is empty.");
                continue;
            }
            if (plan.Name is { Length: > 200 })
            {
                plan.Fail("The name is longer than 200 characters.");
                continue;
            }

            foreach (var secondary in SplitEmails(Cell(row, "secondary_emails")))
            {
                var n = IUserDirectory.Normalize(secondary);
                if (!IUserDirectory.IsValidEmail(secondary))
                {
                    plan.Fail($"{secondary} isn't a valid email address.");
                }
                else if (seen.TryGetValue(n, out var other) && other != plan.Row)
                {
                    plan.Fail($"{secondary} is already on row {other}.");
                }
                else if (owners.TryGetValue(n, out var o) && o.Id != plan.ExistingUserId)
                {
                    plan.Fail($"{secondary} already belongs to someone else in Switchboard.");
                }
                else if (n != normalized && !(owners.TryGetValue(n, out var mine) && mine.Id == plan.ExistingUserId))
                {
                    seen[n] = plan.Row;
                    plan.SecondaryEmails.Add(secondary);
                }
            }
            if (plan.SecondaryEmails.Count > PeopleService.MaxSecondaryEmails)
            {
                plan.Fail($"Use at most {PeopleService.MaxSecondaryEmails} other email addresses.");
            }

            string? Limited(string key, int max, string label)
            {
                var v = Cell(row, key);
                if (v is { Length: var n } && n > max)
                {
                    plan.Fail($"The {label} is longer than {max} characters.");
                }
                return v;
            }
            plan.Phone = Limited("phone", 64, "phone number");
            plan.Mobile = Limited("mobile", 64, "mobile number");
            plan.JobTitle = Limited("job_title", 200, "job title");
            plan.Address = Limited("address", 500, "address");

            if (Cell(row, "department") is { } dept)
            {
                var match = departments.FirstOrDefault(d => d.Name.Equals(dept, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    plan.Fail($"No department is called {dept}. Create it first, or leave the cell empty.");
                }
                plan.DepartmentId = match?.Id;
            }
            if (Cell(row, "location") is { } loc)
            {
                var matches = locations.Where(l => l.Name.Equals(loc, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1)
                {
                    plan.Fail(matches.Count == 0 ? $"No location is called {loc}. Create it first, or leave the cell empty." : $"More than one location is called {loc}. Rename one so they can be told apart.");
                }
                plan.LocationId = matches.Count == 1 ? matches[0].Id : null;
            }
            if (Cell(row, "manager_email") is { } manager)
            {
                var n = IUserDirectory.Normalize(manager);
                if (!IUserDirectory.IsValidEmail(manager))
                {
                    plan.Fail($"The manager {manager} isn't a valid email address.");
                }
                else if (owners.TryGetValue(n, out var m) && m.IsActive && !m.IsForgotten)
                {
                    plan.ManagerEmail = manager;
                    plan.ManagerId = m.Id;
                }
                else if (fileEmails.Contains(n))
                {
                    plan.ManagerEmail = manager;
                }
                else
                {
                    plan.Fail($"No active person has the manager's email {manager}.");
                }
                if (n == normalized)
                {
                    plan.Fail("A person can't be their own manager.");
                }
            }
            if (Cell(row, "vip") is { } vip)
            {
                plan.Vip = vip.ToLowerInvariant() switch
                {
                    "yes" or "y" or "true" or "1" or "vip" => true,
                    "no" or "n" or "false" or "0" => false,
                    _ => null,
                };
                if (plan.Vip is null)
                {
                    plan.Fail($"VIP must be yes or no, not {vip}.");
                }
            }
            if (Cell(row, "time_zone") is { } tz)
            {
                if (tz != "UTC" && !TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _))
                {
                    plan.Fail($"{tz} isn't a time zone. Use names like America/Chicago or Europe/London.");
                }
                plan.TimeZone = tz;
            }
            if (Cell(row, "language") is { } language)
            {
                if (!LanguagePattern().IsMatch(language))
                {
                    plan.Fail($"{language} isn't a language code. Use codes like en or en-GB.");
                }
                plan.Language = language;
            }

            var changes = new Dictionary<string, JsonElement?>();
            foreach (var (key, def) in definitions)
            {
                if (Cell(row, CustomPrefix + key) is not { } value)
                {
                    continue;
                }
                changes[key] = def.FieldType switch
                {
                    CustomFieldType.Checkbox => JsonSerializer.SerializeToElement(value.ToLowerInvariant() is "yes" or "y" or "true" or "1"),
                    CustomFieldType.MultiSelect => JsonSerializer.SerializeToElement(value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
                    CustomFieldType.DependentDropdown => JsonSerializer.SerializeToElement(value.Split('>', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
                    _ => JsonSerializer.SerializeToElement(value),
                };
            }
            try
            {
                var json = changes.Count > 0
                    ? await customFields.ApplyAsync(FieldEntities.User, null, existingJson, changes, FieldAudience.Agent, ct)
                    : existingJson;
                if (plan.Outcome == ImportOutcome.Create)
                {
                    var missing = await customFields.MissingRequiredAsync(FieldEntities.User, null, json, RequiredMoment.AgentSave, ct);
                    if (missing.Count > 0)
                    {
                        plan.Fail(string.Join(" ", missing));
                    }
                }
                plan.CustomFieldsJson = changes.Count > 0 ? json : null;
            }
            catch (ValidationException ex)
            {
                plan.Fail(ex.Message);
            }
        }
        return plans;
    }

    private static ImportPreview Summarize(List<RowPlan> plans) => new(
        plans.Count,
        plans.Count(p => p.Outcome == ImportOutcome.Create),
        plans.Count(p => p.Outcome == ImportOutcome.Update),
        plans.Count(p => p.Outcome == ImportOutcome.Skip),
        plans.Count(p => p.Outcome == ImportOutcome.Error),
        plans.OrderBy(p => p.Outcome == ImportOutcome.Error ? 0 : p.Outcome == ImportOutcome.Skip ? 1 : 2).ThenBy(p => p.Row).Take(100)
            .Select(p => new ImportRowResult(p.Row, p.Email, p.Name, p.Outcome, p.Warning ?? p.Message)).ToList());

    private static Dictionary<string, string> Mapping(AdmImport import) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(import.MappingJson, JsonDefaults.Options) ?? [];

    private static IEnumerable<string> SplitEmails(string? value) =>
        (value ?? "").Split([';', ',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Normalize(string header) => header.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z]{2,4})?$")]
    private static partial Regex LanguagePattern();
}
