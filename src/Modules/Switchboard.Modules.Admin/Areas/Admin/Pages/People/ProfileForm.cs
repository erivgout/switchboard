using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Pages.People;

/// <summary>Bound profile fields; shared by the new-person and edit pages.</summary>
public sealed class ProfileInput
{
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    /// <summary>One address per line.</summary>
    public string? SecondaryEmails { get; set; }
    public string? Phone { get; set; }
    public string? Mobile { get; set; }
    public string? JobTitle { get; set; }
    public string? TimeZone { get; set; }
    public string? Language { get; set; } = "en";
    public string? Address { get; set; }
    public string? Background { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid? ManagerId { get; set; }
    public bool IsVip { get; set; }

    public static ProfileInput From(User u, IReadOnlyList<string> secondary) => new()
    {
        DisplayName = u.DisplayName,
        Email = u.Email ?? "",
        SecondaryEmails = string.Join('\n', secondary),
        Phone = u.PhoneNumber,
        Mobile = u.Mobile,
        JobTitle = u.JobTitle,
        TimeZone = u.TimeZone,
        Language = u.Language,
        Address = u.Address,
        Background = u.Background,
        DepartmentId = u.DepartmentId,
        LocationId = u.LocationId,
        ManagerId = u.ManagerId,
        IsVip = u.IsVip,
    };

    public PersonInput ToInput(IReadOnlyDictionary<string, JsonElement?>? custom) => new()
    {
        DisplayName = DisplayName ?? "",
        Email = Email ?? "",
        SecondaryEmails = (SecondaryEmails ?? "").Split(['\n', ',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        Phone = Phone,
        Mobile = Mobile,
        JobTitle = JobTitle,
        TimeZone = TimeZone,
        Language = Language,
        Address = Address,
        Background = Background,
        DepartmentId = DepartmentId,
        LocationId = LocationId,
        ManagerId = ManagerId,
        IsVip = IsVip,
        CustomFields = custom,
    };
}

/// <summary>Everything the profile form partial renders.</summary>
public sealed class ProfileFormModel
{
    public required ProfileInput Input { get; init; }
    public required IReadOnlyList<SelectListItem> Departments { get; init; }
    public required IReadOnlyList<SelectListItem> Locations { get; init; }
    public required IEnumerable<SelectListItem> TimeZones { get; init; }
    public required PersonOption? Manager { get; init; }
    public required CustomFieldsModel Custom { get; init; }
    public bool Disabled { get; init; }

    public static async Task<ProfileFormModel> LoadAsync(SwitchboardDbContext db, ICustomFieldService fields, PeopleService people,
        ProfileInput input, string customJson, bool disabled)
    {
        var definitions = await fields.GetDefinitionsAsync(FieldEntities.User, null);
        return new ProfileFormModel
        {
            Input = input,
            Departments = await AdminSelects.DepartmentsAsync(db, input.DepartmentId),
            Locations = await AdminSelects.LocationsAsync(db, input.LocationId),
            TimeZones = AdminSelects.TimeZones(input.TimeZone),
            Manager = await people.OptionAsync(input.ManagerId),
            Custom = await CustomFieldsModel.LoadAsync(db, definitions, customJson, disabled),
            Disabled = disabled,
        };
    }
}

public sealed record PersonHeaderModel(PersonDetail Detail, string Tab);
