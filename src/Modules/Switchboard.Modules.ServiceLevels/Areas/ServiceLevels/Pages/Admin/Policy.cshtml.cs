using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceLevels.Domain;
using Switchboard.Modules.ServiceLevels.Services;
using Switchboard.UI;

namespace Switchboard.Modules.ServiceLevels.Areas.ServiceLevels.Pages.Admin;

public sealed class PolicyModel(SwitchboardDbContext db, IPermissionService permissions, SlaAdminService service) : SlaAdminPage(db, permissions)
{
    public static readonly Priority[] Priorities = [Priority.Urgent, Priority.High, Priority.Medium, Priority.Low];

    [BindProperty]
    public PolicyForm Form { get; set; } = new();

    public bool IsNew { get; private set; }
    public string Title { get; private set; } = "New SLA policy";
    public List<Option> Sources { get; private set; } = [];
    public List<Option> Groups { get; private set; } = [];
    public List<Option> Departments { get; private set; } = [];
    public List<Option> RequesterGroups { get; private set; } = [];
    public List<Option> Agents { get; private set; } = [];
    public List<Option> Calendars { get; private set; } = [];
    public List<Option> CustomFields { get; private set; } = [];
    public List<(Option Category, List<Option> Children)> Categories { get; private set; } = [];

    public sealed record Option(Guid Id, string Name, string? Key = null);

    public async Task<IActionResult> OnGetAsync(string id, string? ws, CancellationToken ct)
    {
        if (!await LoadAsync(id, ws, ct))
        {
            return NotFound();
        }
        if (!IsNew)
        {
            Form = PolicyForm.From(await service.PolicyAsync(Guid.Parse(id), ct));
        }
        Form.Pad();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string id, string? ws, CancellationToken ct)
    {
        if (!await LoadAsync(id, ws, ct))
        {
            return NotFound();
        }
        if (ModelState.IsValid)
        {
            try
            {
                var input = Form.ToInput(Workspace.Id, IsNew ? null : Guid.Parse(id));
                var saved = await service.SavePolicyAsync(input, ct);
                TempData.Toast(IsNew ? $"Policy “{saved.Name}” created" : "Policy saved");
                return Redirect(WithWorkspace("/admin/service-levels/policies"));
            }
            catch (ValidationException ex)
            {
                AddErrors(ex);
            }
        }
        Form.Pad();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id, string? ws, CancellationToken ct)
    {
        if (!await LoadAsync(id, ws, ct) || IsNew)
        {
            return NotFound();
        }
        await service.DeletePolicyAsync(Guid.Parse(id), ct);
        TempData.Toast("Policy deleted");
        return Redirect(WithWorkspace("/admin/service-levels/policies"));
    }

    public IEnumerable<string> Errors(string key) =>
        ModelState.TryGetValue(key, out var entry) ? entry.Errors.Select(e => e.ErrorMessage) : [];

    private async Task<bool> LoadAsync(string id, string? ws, CancellationToken ct)
    {
        IsNew = id == "new";
        if (IsNew)
        {
            if (!await UseWorkspaceAsync(ws, ct))
            {
                return false;
            }
        }
        else
        {
            if (!Guid.TryParse(id, out var policyId))
            {
                return false;
            }
            SlaPolicy policy;
            try
            {
                policy = await service.PolicyAsync(policyId, ct);
            }
            catch (NotFoundException)
            {
                return false;
            }
            if (!await UseWorkspaceAsync(policy.WorkspaceId, ct))
            {
                return false;
            }
            Title = policy.Name;
        }

        var wsId = Workspace.Id;
        Sources = await Db.TicketSources.AsNoTracking().OrderBy(s => s.Order).Select(s => new Option(s.Id, s.Name, null)).ToListAsync(ct);
        Groups = await Db.AgentGroups.AsNoTracking().Where(g => g.WorkspaceId == wsId).OrderBy(g => g.Name).Select(g => new Option(g.Id, g.Name, null)).ToListAsync(ct);
        Departments = await Db.Departments.AsNoTracking().OrderBy(d => d.Name).Select(d => new Option(d.Id, d.Name, null)).ToListAsync(ct);
        RequesterGroups = await Db.RequesterGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => new Option(g.Id, g.Name, null)).ToListAsync(ct);
        Agents = await Db.Users.AsNoTracking()
            .Where(u => u.IsActive && (u.IsOrgAdmin || u.Memberships.Any(m => m.WorkspaceId == wsId)))
            .OrderBy(u => u.DisplayName).Select(u => new Option(u.Id, u.DisplayName, u.Email)).ToListAsync(ct);
        Calendars = await Db.Set<SlaCalendar>().AsNoTracking().Where(c => c.WorkspaceId == wsId && c.DeletedAt == null)
            .OrderByDescending(c => c.IsDefault).ThenBy(c => c.Name).Select(c => new Option(c.Id, c.Name, c.IsDefault ? "default" : null)).ToListAsync(ct);
        CustomFields = await Db.CustomFieldDefinitions.AsNoTracking()
            .Where(f => f.EntityType == "ticket" && f.IsActive && (f.WorkspaceId == null || f.WorkspaceId == wsId)
                && f.FieldType != CustomFieldType.Paragraph && f.FieldType != CustomFieldType.Content)
            .OrderBy(f => f.Label).Select(f => new Option(f.Id, f.Label, f.Key)).ToListAsync(ct);
        var categories = await Db.TicketCategories.AsNoTracking().Where(c => c.WorkspaceId == wsId && c.Level <= 2)
            .OrderBy(c => c.Order).ThenBy(c => c.Name).ToListAsync(ct);
        Categories = categories.Where(c => c.Level == 1)
            .Select(c => (new Option(c.Id, c.Name), categories.Where(x => x.ParentId == c.Id).Select(x => new Option(x.Id, x.Name)).ToList()))
            .ToList();
        return true;
    }
}

/// <summary>The policy editor's fields. Times are a number and a unit (minutes or hours).</summary>
public sealed class PolicyForm
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? CalendarId { get; set; }

    public List<string> Types { get; set; } = [];
    public List<Guid> Sources { get; set; } = [];
    public List<Guid> Groups { get; set; } = [];
    public List<Guid> Categories { get; set; } = [];
    public List<Guid> Departments { get; set; } = [];
    public List<Guid> RequesterGroups { get; set; } = [];
    public List<CustomRow> Custom { get; set; } = [];

    public List<TargetRow> Targets { get; set; } = [];
    public List<ReminderRow> Reminders { get; set; } = [];
    public NotifyForm Response { get; set; } = new();
    public List<LevelRow> Levels { get; set; } = [];

    public sealed class CustomRow
    {
        public string? Key { get; set; }
        public string? Value { get; set; }
    }

    public sealed class TargetRow
    {
        public Priority Priority { get; set; }
        public int? FirstResponse { get; set; }
        public string FirstResponseUnit { get; set; } = "h";
        public int? NextResponse { get; set; }
        public string NextResponseUnit { get; set; } = "h";
        public int? Resolution { get; set; }
        public string ResolutionUnit { get; set; } = "h";
        public bool BusinessHours { get; set; } = true;
    }

    public sealed class ReminderRow
    {
        public SlaTargetKind Target { get; set; } = SlaTargetKind.Resolution;
        public int? Before { get; set; }
        public string BeforeUnit { get; set; } = "m";
    }

    public sealed class NotifyForm
    {
        public bool Assignee { get; set; }
        public bool Manager { get; set; }
        public List<Guid> Groups { get; set; } = [];
        public List<Guid> Users { get; set; } = [];

        public SlaNotifyTargets ToTargets() => new() { Assignee = Assignee, AssigneeManager = Manager, GroupIds = Groups.Distinct().ToList(), UserIds = Users.Distinct().ToList() };

        public static NotifyForm From(SlaNotifyTargets t) => new() { Assignee = t.Assignee, Manager = t.AssigneeManager, Groups = [.. t.GroupIds], Users = [.. t.UserIds] };
    }

    public sealed class LevelRow
    {
        public int Level { get; set; }
        public bool Enabled { get; set; }
        public int? Delay { get; set; }
        public string DelayUnit { get; set; } = "h";
        public NotifyForm Notify { get; set; } = new();
    }

    /// <summary>Fills in the fixed rows the editor shows: four priorities, three reminders, two custom matches, four levels.</summary>
    public void Pad()
    {
        Targets = PolicyModel.Priorities.Select(p => Targets.FirstOrDefault(t => t.Priority == p) ?? new TargetRow { Priority = p }).ToList();
        while (Reminders.Count < 3)
        {
            Reminders.Add(new ReminderRow());
        }
        while (Custom.Count < 2)
        {
            Custom.Add(new CustomRow());
        }
        Levels = Enumerable.Range(1, 4).Select(l => Levels.FirstOrDefault(x => x.Level == l) ?? new LevelRow { Level = l }).ToList();
    }

    public static PolicyForm From(SlaPolicy p)
    {
        List<string> Values(string field) => p.Conditions.Where(c => c.Field == field).SelectMany(c => c.Values).ToList();
        List<Guid> Ids(string field) => Values(field).Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        var form = new PolicyForm
        {
            Name = p.Name,
            Description = p.Description,
            IsActive = p.IsActive,
            CalendarId = p.CalendarId,
            Types = Values(SlaConditionFields.Type),
            Sources = Ids(SlaConditionFields.Source),
            Groups = Ids(SlaConditionFields.Group),
            Categories = Ids(SlaConditionFields.Category),
            Departments = Ids(SlaConditionFields.Department),
            RequesterGroups = Ids(SlaConditionFields.RequesterGroup),
            Custom = p.Conditions.Where(c => c.Field.StartsWith(SlaConditionFields.CustomPrefix, StringComparison.Ordinal))
                .Select(c => new CustomRow { Key = c.Field[SlaConditionFields.CustomPrefix.Length..], Value = string.Join(", ", c.Values) }).ToList(),
            Response = NotifyForm.From(p.Escalations.FirstResponse),
        };
        foreach (var t in p.Targets)
        {
            var row = new TargetRow { Priority = t.Priority, BusinessHours = t.BusinessHours };
            (row.FirstResponse, row.FirstResponseUnit) = Split(t.FirstResponseMinutes);
            (row.NextResponse, row.NextResponseUnit) = Split(t.NextResponseMinutes);
            (row.Resolution, row.ResolutionUnit) = Split(t.ResolutionMinutes);
            form.Targets.Add(row);
        }
        foreach (var r in p.Reminders)
        {
            var (value, unit) = Split(r.MinutesBefore);
            form.Reminders.Add(new ReminderRow { Target = r.Target, Before = value, BeforeUnit = unit });
        }
        foreach (var level in p.Escalations.Resolution)
        {
            var (value, unit) = Split(level.DelayMinutes);
            form.Levels.Add(new LevelRow { Level = level.Level, Enabled = true, Delay = value ?? 0, DelayUnit = unit, Notify = NotifyForm.From(level.Notify) });
        }
        return form;
    }

    public PolicyInput ToInput(Guid workspaceId, Guid? id)
    {
        var conditions = new List<SlaCondition>
        {
            new(SlaConditionFields.Type, Types.Where(t => t is "incident" or "request").ToList()),
            new(SlaConditionFields.Source, Sources.Select(g => g.ToString()).ToList()),
            new(SlaConditionFields.Group, Groups.Select(g => g.ToString()).ToList()),
            new(SlaConditionFields.Category, Categories.Select(g => g.ToString()).ToList()),
            new(SlaConditionFields.Department, Departments.Select(g => g.ToString()).ToList()),
            new(SlaConditionFields.RequesterGroup, RequesterGroups.Select(g => g.ToString()).ToList()),
        };
        foreach (var row in Custom.Where(c => !string.IsNullOrWhiteSpace(c.Key) && !string.IsNullOrWhiteSpace(c.Value)))
        {
            // "Gold, Platinum" matches either value.
            conditions.Add(new SlaCondition(SlaConditionFields.CustomPrefix + row.Key!.Trim(),
                row.Value!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()));
        }

        return new PolicyInput
        {
            Id = id,
            WorkspaceId = workspaceId,
            Name = Name,
            Description = Description,
            IsActive = IsActive,
            CalendarId = CalendarId,
            Conditions = conditions.Where(c => c.Values.Count > 0).ToList(),
            Targets = Targets.Select(t => new SlaTarget(t.Priority,
                Minutes(t.FirstResponse, t.FirstResponseUnit, "targets"),
                Minutes(t.NextResponse, t.NextResponseUnit, "targets"),
                Minutes(t.Resolution, t.ResolutionUnit, "targets"),
                t.BusinessHours)).ToList(),
            Reminders = Reminders.Where(r => r.Before is not null)
                .Select(r => new SlaReminderRule(r.Target, Minutes(r.Before, r.BeforeUnit, "reminders") ?? 0)).ToList(),
            Escalations = new SlaEscalationPlan
            {
                FirstResponse = Response.ToTargets(),
                Resolution = Levels.Where(l => l.Enabled)
                    .Select(l => new SlaEscalationLevel(l.Level, Minutes(l.Delay ?? 0, l.DelayUnit, "escalations", allowZero: true) ?? 0, l.Notify.ToTargets()))
                    .ToList(),
            },
        };
    }

    private static int? Minutes(int? value, string unit, string field, bool allowZero = false)
    {
        if (value is not { } v)
        {
            return null;
        }
        if (v < 0 || (v == 0 && !allowZero))
        {
            throw new ValidationException(field, "Times must be whole numbers above zero.");
        }
        if (v > 1_000_000)
        {
            throw new ValidationException(field, "That time is too long. Targets can be up to 365 days.");
        }
        return unit == "h" ? v * 60 : v;
    }

    private static (int? Value, string Unit) Split(int? minutes) => minutes switch
    {
        null => (null, "h"),
        0 => (0, "m"),
        var m when m % 60 == 0 => (m / 60, "h"),
        var m => (m, "m"),
    };
}
