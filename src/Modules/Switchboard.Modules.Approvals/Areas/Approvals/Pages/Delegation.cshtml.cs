using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Approvals.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Approvals.Areas.Approvals.Pages;

/// <summary>Form handlers behind the "Approval delegation" section on /profile (FR-IAM-4).</summary>
public sealed class DelegationModel(IApprovalDelegationService delegation, SwitchboardDbContext db, IActorAccessor actors) : PageModel
{
    public IActionResult OnGet() => Redirect("/profile#delegation");

    public async Task<IActionResult> OnPostCreateAsync(Guid? delegateId, DateOnly? from, DateOnly? until)
    {
        try
        {
            if (delegateId is not { } id)
            {
                throw new ValidationException("delegate", "Choose who covers your approvals.");
            }
            if (from is not { } start || until is not { } end)
            {
                throw new ValidationException("dates", "Choose the first and last day.");
            }
            var zone = await ZoneAsync();
            await delegation.CreateAsync(id, StartOfDay(start, zone), StartOfDay(end.AddDays(1), zone), HttpContext.RequestAborted);
            TempData.Toast("Delegation saved");
        }
        catch (ValidationException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return Redirect("/profile#delegation");
    }

    public async Task<IActionResult> OnPostEndAsync(Guid id)
    {
        await delegation.EndAsync(id, HttpContext.RequestAborted);
        TempData.Toast("Delegation ended");
        return Redirect("/profile#delegation");
    }

    /// <summary>Whole days in the person's time zone (UTC when unknown).</summary>
    private async Task<TimeZoneInfo> ZoneAsync()
    {
        var uid = actors.Current.UserId;
        var tz = await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.TimeZone).FirstOrDefaultAsync();
        try
        {
            return string.IsNullOrWhiteSpace(tz) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(tz);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
