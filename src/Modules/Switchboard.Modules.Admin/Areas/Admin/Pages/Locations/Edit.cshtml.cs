using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Locations;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.LocationsManage)]
public sealed class EditModel(OrgStructureService org, SwitchboardDbContext db) : AdminPageModel
{
    public sealed class LocationForm
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

    [BindProperty]
    public LocationForm Input { get; set; } = new();

    /// <summary>A location id, or <c>none</c>.</summary>
    [BindProperty]
    public string? Replacement { get; set; }

    public Location? Location { get; private set; }
    public bool IsNew => Location is null;
    public int People { get; private set; }
    public int Children { get; private set; }
    public List<SelectListItem> Parents { get; private set; } = [];
    public List<SelectListItem> Replacements { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }
        if (Location is { } l)
        {
            Input = new LocationForm
            {
                Name = l.Name, ParentId = l.ParentId, AddressLine1 = l.AddressLine1, AddressLine2 = l.AddressLine2, City = l.City, State = l.State,
                PostalCode = l.PostalCode, Country = l.Country, ContactName = l.ContactName, ContactEmail = l.ContactEmail, ContactPhone = l.ContactPhone,
            };
            await LoadAsync(id);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }
        var i = Input;
        var input = new LocationInput
        {
            Name = i.Name, ParentId = i.ParentId, AddressLine1 = i.AddressLine1, AddressLine2 = i.AddressLine2, City = i.City, State = i.State,
            PostalCode = i.PostalCode, Country = i.Country, ContactName = i.ContactName, ContactEmail = i.ContactEmail, ContactPhone = i.ContactPhone,
        };
        Location? saved = null;
        if (await TryAsync(async () => saved = await org.SaveLocationAsync(Location?.Id, input)))
        {
            TempData.Toast(Location is null ? "Location created" : "Location saved");
            return Redirect($"/admin/locations/{saved!.Id}");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        if (!await LoadAsync(id) || Location is null)
        {
            return NotFound();
        }
        if (TryReadReplacement(Replacement, People > 0, "Choose where the people at this location go.", out var replacement)
            && await TryAsync(() => org.DeleteLocationAsync(Location.Id, replacement), ""))
        {
            TempData.Toast("Location deleted");
            return Redirect("/admin/locations");
        }
        return await OnGetAsync(id);
    }

    private async Task<bool> LoadAsync(string id)
    {
        if (id != "new")
        {
            if (!Guid.TryParse(id, out var guid))
            {
                return false;
            }
            (Location, People, Children) = await org.GetLocationAsync(guid);
            Replacements = await AdminSelects.LocationsAsync(db, null, guid, "Choose where they go");
            Replacements.Add(new SelectListItem("Leave them without a location", "none"));
            Replacements.ForEach(r => r.Selected = r.Value == (Replacement ?? ""));
        }
        Parents = await AdminSelects.LocationsAsync(db, Input.ParentId, Location?.Id, "None (top level)");
        return true;
    }
}
