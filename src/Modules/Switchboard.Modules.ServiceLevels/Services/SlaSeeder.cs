using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;
using Switchboard.Modules.ServiceLevels.Domain;
using Switchboard.Modules.ServiceLevels.Time;

namespace Switchboard.Modules.ServiceLevels.Services;

/// <summary>Per workspace, once: a Mon-Fri 9-5 calendar and a default policy. Removing them later sticks.</summary>
internal static class SlaSeeder
{
    private const string SeededKey = "sla.seeded";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<SwitchboardDbContext>();
        var settings = services.GetRequiredService<ISettingsService>();
        var org = await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).FirstOrDefaultAsync(ct);
        var zone = BusinessCalendar.FindZone(org?.TimeZone) is not null ? org!.TimeZone : "UTC";

        foreach (var workspace in await db.Workspaces.AsNoTracking().Where(w => !w.IsArchived).ToListAsync(ct))
        {
            if (await settings.GetAsync(SeededKey, false, workspace.Id, ct))
            {
                continue;
            }

            var calendar = new SlaCalendar
            {
                WorkspaceId = workspace.Id,
                Name = "Business hours",
                Description = "Monday to Friday, 9:00 to 17:00.",
                TimeZoneId = zone,
                IsDefault = true,
            };
            foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
            {
                calendar.Hours.Add(new SlaWorkingHours { CalendarId = calendar.Id, Day = day, StartMinute = 9 * 60, EndMinute = 17 * 60 });
            }
            db.Set<SlaCalendar>().Add(calendar);

            var policy = new SlaPolicy
            {
                WorkspaceId = workspace.Id,
                Name = "Default service levels",
                Description = "Applies to every ticket that no earlier policy covers.",
                Order = 1,
                Targets =
                [
                    new SlaTarget(Priority.Urgent, 30, null, 4 * 60),
                    new SlaTarget(Priority.High, 60, null, 8 * 60),
                    new SlaTarget(Priority.Medium, 4 * 60, null, 16 * 60),
                    new SlaTarget(Priority.Low, 8 * 60, null, 40 * 60),
                ],
                Reminders =
                [
                    new SlaReminderRule(SlaTargetKind.FirstResponse, 15),
                    new SlaReminderRule(SlaTargetKind.Resolution, 60),
                ],
                Escalations = new SlaEscalationPlan
                {
                    FirstResponse = new SlaNotifyTargets { Assignee = true },
                    Resolution =
                    [
                        new SlaEscalationLevel(1, 0, new SlaNotifyTargets { Assignee = true }),
                        new SlaEscalationLevel(2, 4 * 60, new SlaNotifyTargets { Assignee = true, AssigneeManager = true }),
                    ],
                },
            };
            db.Set<SlaPolicy>().Add(policy);
            await settings.SetAsync(SeededKey, true, workspace.Id, ct);
        }
        await db.SaveChangesAsync(ct);
    }
}
