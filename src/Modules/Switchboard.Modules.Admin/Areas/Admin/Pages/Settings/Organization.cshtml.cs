using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Settings;

/// <summary>Organization settings (FR-ORG-2).</summary>
[Authorize(Policy = SwitchboardPolicies.OrgAdmin)]
[RequestSizeLimit(AdminSettingsService.MaxLogoBytes + 64 * 1024)]
public sealed class OrganizationModel(AdminSettingsService settings) : AdminPageModel
{
    public sealed class OrganizationForm
    {
        public string Name { get; set; } = "";
        public string? BaseUrl { get; set; }
        public string TimeZone { get; set; } = "UTC";
        public string Language { get; set; } = "en";
        public string DateFormat { get; set; } = "MMM d, yyyy";
        public string Currency { get; set; } = "USD";
        public DayOfWeek FirstDayOfWeek { get; set; } = DayOfWeek.Monday;
    }

    [BindProperty]
    public OrganizationForm Input { get; set; } = new();

    [BindProperty]
    public IFormFile? Logo { get; set; }

    public Organization Organization { get; private set; } = default!;

    public async Task OnGetAsync()
    {
        Organization = await settings.GetOrganizationAsync();
        var o = Organization;
        Input = new OrganizationForm
        {
            Name = o.Name, BaseUrl = o.BaseUrl, TimeZone = o.TimeZone, Language = o.Language, DateFormat = o.DateFormat,
            Currency = o.Currency, FirstDayOfWeek = o.FirstDayOfWeek,
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var i = Input;
        if (await TryAsync(() => settings.SaveOrganizationAsync(new OrganizationInput(i.Name, i.BaseUrl, i.TimeZone, i.Language, i.DateFormat, i.Currency, i.FirstDayOfWeek))))
        {
            TempData.Toast("Organization settings saved");
            return RedirectToPage();
        }
        Organization = await settings.GetOrganizationAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostLogoAsync()
    {
        if (Logo is null || Logo.Length == 0)
        {
            ModelState.AddModelError("Logo", "Choose an image to upload.");
        }
        else
        {
            await using var stream = Logo.OpenReadStream();
            if (await TryAsync(() => settings.SetLogoAsync(stream, Logo.FileName), ""))
            {
                TempData.Toast("Logo updated");
                return RedirectToPage();
            }
        }
        await OnGetAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveLogoAsync()
    {
        await settings.RemoveLogoAsync();
        TempData.Toast("Logo removed");
        return RedirectToPage();
    }
}
