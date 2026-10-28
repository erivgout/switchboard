using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Catalog.Domain;
using Switchboard.Modules.Catalog.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Catalog.Areas.Catalog.Pages.Admin;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + CatalogPrivileges.Manage)]
public sealed class ItemModel(ICatalogAdmin admin, SwitchboardDbContext db) : PageModel
{
    public sealed class DetailsForm
    {
        public string Name { get; set; } = "";
        public string ShortDescription { get; set; } = "";
        public string? Description { get; set; }
        public string Icon { get; set; } = "bi-box-seam";
        public CatItemKind Kind { get; set; } = CatItemKind.Standard;
        public string? Cost { get; set; }
        public string Currency { get; set; } = "USD";
        public int? DeliveryDays { get; set; }
        public int MaxQuantity { get; set; } = 1;
        public bool AllowRequestedFor { get; set; }
        public Guid? FulfillmentGroupId { get; set; }
        public string? PostSubmitMessage { get; set; }
        public List<Guid> CategoryIds { get; set; } = [];
        public List<Guid> BundleItemIds { get; set; } = [];
    }

    public sealed class LevelForm
    {
        public string? Name { get; set; }
        public ApprovalRule Rule { get; set; } = ApprovalRule.Any;
        public bool UseManager { get; set; }
        public int ManagerLevel { get; set; } = 1;
        public bool UseDepartmentHead { get; set; }
        public bool UseUsers { get; set; }
        public List<Guid> UserIds { get; set; } = [];
        public bool UseGroup { get; set; }
        public Guid? GroupId { get; set; }
        public bool UseLookup { get; set; }
        public string? FieldKey { get; set; }
    }

    public sealed class FieldForm
    {
        public string Label { get; set; } = "";
        public CatFieldType FieldType { get; set; } = CatFieldType.Text;
        public bool Required { get; set; }
        public string? HelpText { get; set; }
        public string? Options { get; set; }

        public FieldInput ToInput() => new()
        {
            Label = Label,
            FieldType = FieldType,
            Required = Required,
            HelpText = HelpText,
            Options = (Options ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
    }

    [BindProperty(SupportsGet = true)]
    public string Tab { get; set; } = "details";

    [BindProperty]
    public DetailsForm Details { get; set; } = new();

    [BindProperty]
    public List<LevelForm> Levels { get; set; } = [];

    public CatalogItemDetails Item { get; private set; } = default!;
    public IReadOnlyList<CatalogCategoryView> Categories { get; private set; } = [];
    public IReadOnlyList<(Guid Id, string Name)> Groups { get; private set; } = [];
    public IReadOnlyList<(Guid Id, string Name)> RequesterGroups { get; private set; } = [];
    public IReadOnlyList<(Guid Id, string Name)> Departments { get; private set; } = [];
    public IReadOnlyList<CatSharedField> SharedFields { get; private set; } = [];
    public IReadOnlyList<CatalogItemSummary> BundleCandidates { get; private set; } = [];
    public IReadOnlyDictionary<Guid, string> People { get; private set; } = new Dictionary<Guid, string>();
    public bool LevelsChangedNotSaved { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Tab = Tab is "form" or "access" ? Tab : "details";
        await LoadAsync(id, fillForms: true);
        return Page();
    }

    // ---------------------------------------------------------------- details and state

    public async Task<IActionResult> OnPostDetailsAsync(Guid id)
    {
        Tab = "details";
        long? cost = null;
        if (!string.IsNullOrWhiteSpace(Details.Cost))
        {
            if (!decimal.TryParse(Details.Cost.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount < 0)
            {
                ModelState.AddModelError("Details.Cost", "Enter the cost as a number, like 1299.00.");
                await LoadAsync(id, fillForms: false);
                return Page();
            }
            cost = (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
        }
        try
        {
            await admin.UpdateItemAsync(id, new ItemDetailsInput
            {
                Name = Details.Name,
                ShortDescription = Details.ShortDescription,
                Description = Details.Description,
                Icon = Details.Icon,
                Kind = Details.Kind,
                CostMinor = cost,
                Currency = Details.Currency,
                DeliveryDays = Details.DeliveryDays,
                MaxQuantity = Details.MaxQuantity,
                AllowRequestedFor = Details.AllowRequestedFor,
                FulfillmentGroupId = Details.FulfillmentGroupId,
                PostSubmitMessage = Details.PostSubmitMessage,
                CategoryIds = Details.CategoryIds,
                BundleItemIds = Details.BundleItemIds,
            });
        }
        catch (ValidationException ex)
        {
            AddErrors(ex, "Details.");
            await LoadAsync(id, fillForms: false);
            return Page();
        }
        TempData.Toast("Details saved");
        return RedirectToPage(new { id, tab = "details" });
    }

    public async Task<IActionResult> OnPostStateAsync(Guid id, CatItemState state)
    {
        try
        {
            await admin.SetStateAsync(id, state);
            TempData.Toast(state switch
            {
                CatItemState.Published => "Item published",
                CatItemState.Archived => "Item archived",
                _ => "Item moved to drafts",
            });
        }
        catch (ValidationException ex)
        {
            TempData.Toast("Can't publish yet: " + string.Join(" ", ex.Errors.SelectMany(e => e.Value)), ToastKind.Error);
        }
        return RedirectToPage(new { id, tab = Tab });
    }

    // ---------------------------------------------------------------- audience and approvals

    public async Task<IActionResult> OnPostAudienceAsync(Guid id, List<Guid> groupIds, List<Guid> departmentIds)
    {
        await admin.UpdateAudienceAsync(id, groupIds, departmentIds);
        TempData.Toast("Audience saved");
        return RedirectToPage(new { id, tab = "access" });
    }

    public async Task<IActionResult> OnPostApprovalAsync(Guid id, string? command)
    {
        Tab = "access";
        if (command == "add")
        {
            Levels.Add(new LevelForm { UseManager = true });
            return await RedisplayLevelsAsync(id);
        }
        if (command?.StartsWith("remove-", StringComparison.Ordinal) == true && int.TryParse(command[7..], out var index) && index >= 0 && index < Levels.Count)
        {
            Levels.RemoveAt(index);
            return await RedisplayLevelsAsync(id);
        }
        try
        {
            await admin.UpdateApprovalAsync(id, command == "none" || Levels.Count == 0 ? null : new ApprovalConfig { Levels = Levels.Select(ToConfig).ToList() });
        }
        catch (ValidationException ex)
        {
            AddErrors(ex, "");
            return await RedisplayLevelsAsync(id, saved: false);
        }
        TempData.Toast(command == "none" || Levels.Count == 0 ? "Approvals turned off for this item" : "Approval chain saved");
        return RedirectToPage(new { id, tab = "access" });
    }

    // ---------------------------------------------------------------- form builder (htmx)

    public Task<IActionResult> OnPostAddFieldAsync(Guid id, FieldForm field) =>
        BuilderAsync(id, () => admin.AddFieldAsync(id, field.ToInput()), "Question added");

    public Task<IActionResult> OnPostAddSharedAsync(Guid id, Guid sharedFieldId, bool required) =>
        BuilderAsync(id, () => admin.AddSharedFieldAsync(id, sharedFieldId, required), "Shared field added");

    public Task<IActionResult> OnPostUpdateFieldAsync(Guid id, Guid fieldId, FieldForm field) =>
        BuilderAsync(id, () => admin.UpdateFieldAsync(id, fieldId, field.ToInput()), "Question saved");

    public Task<IActionResult> OnPostRemoveFieldAsync(Guid id, Guid fieldId) =>
        BuilderAsync(id, () => admin.RemoveFieldAsync(id, fieldId), "Question removed");

    public Task<IActionResult> OnPostMoveFieldAsync(Guid id, Guid fieldId, int direction) =>
        BuilderAsync(id, () => admin.MoveFieldAsync(id, fieldId, direction), null);

    private async Task<IActionResult> BuilderAsync(Guid id, Func<Task> action, string? success)
    {
        Tab = "form";
        string? error = null;
        try
        {
            await action();
        }
        catch (ValidationException ex)
        {
            error = string.Join(" ", ex.Errors.SelectMany(e => e.Value));
        }
        if (!Request.IsHtmx())
        {
            TempData.Toast(error ?? success ?? "Saved", error is null ? ToastKind.Success : ToastKind.Error);
            return RedirectToPage(new { id, tab = "form" });
        }
        if (error is not null)
        {
            Response.HxToast(error, ToastKind.Error);
        }
        else if (success is not null)
        {
            Response.HxToast(success);
        }
        await LoadAsync(id, fillForms: false);
        return Partial("_Builder", this);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<IActionResult> RedisplayLevelsAsync(Guid id, bool saved = true)
    {
        LevelsChangedNotSaved = saved;
        await LoadAsync(id, fillForms: false, keepLevels: true);
        return Page();
    }

    private static ApprovalLevelConfig ToConfig(LevelForm l)
    {
        var sources = new List<ApproverSource>();
        if (l.UseManager)
        {
            sources.Add(new ApproverSource { Kind = ApproverSourceKind.Manager, ManagerLevel = l.ManagerLevel });
        }
        if (l.UseDepartmentHead)
        {
            sources.Add(new ApproverSource { Kind = ApproverSourceKind.DepartmentHead });
        }
        if (l.UseUsers)
        {
            sources.Add(new ApproverSource { Kind = ApproverSourceKind.Users, UserIds = l.UserIds.Distinct().ToList() });
        }
        if (l.UseGroup)
        {
            sources.Add(new ApproverSource { Kind = ApproverSourceKind.AgentGroup, GroupId = l.GroupId });
        }
        if (l.UseLookup)
        {
            sources.Add(new ApproverSource { Kind = ApproverSourceKind.LookupField, FieldKey = l.FieldKey });
        }
        return new ApprovalLevelConfig { Name = l.Name, Rule = l.Rule, Sources = sources };
    }

    private static LevelForm FromConfig(ApprovalLevelConfig l)
    {
        var form = new LevelForm { Name = l.Name, Rule = l.Rule };
        foreach (var s in l.Sources)
        {
            switch (s.Kind)
            {
                case ApproverSourceKind.Manager:
                    form.UseManager = true;
                    form.ManagerLevel = s.ManagerLevel ?? 1;
                    break;
                case ApproverSourceKind.DepartmentHead:
                    form.UseDepartmentHead = true;
                    break;
                case ApproverSourceKind.Users:
                    form.UseUsers = true;
                    form.UserIds = s.UserIds ?? [];
                    break;
                case ApproverSourceKind.AgentGroup:
                    form.UseGroup = true;
                    form.GroupId = s.GroupId;
                    break;
                case ApproverSourceKind.LookupField:
                    form.UseLookup = true;
                    form.FieldKey = s.FieldKey;
                    break;
            }
        }
        return form;
    }

    private void AddErrors(ValidationException ex, string prefix)
    {
        foreach (var (key, messages) in ex.Errors)
        {
            var name = key switch
            {
                "levels" or "approvers" => "",
                _ => prefix + char.ToUpperInvariant(key[0]) + key[1..],
            };
            foreach (var message in messages)
            {
                ModelState.AddModelError(name, message);
            }
        }
    }

    private async Task LoadAsync(Guid id, bool fillForms, bool keepLevels = false)
    {
        Item = await admin.GetItemAsync(id);
        var item = Item.Item;
        Categories = await admin.ListCategoriesAsync();
        Groups = (await db.AgentGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync()).Select(g => (g.Id, g.Name)).ToList();
        RequesterGroups = (await db.RequesterGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync()).Select(g => (g.Id, g.Name)).ToList();
        Departments = (await db.Departments.AsNoTracking().OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync()).Select(d => (d.Id, d.Name)).ToList();
        SharedFields = await db.Set<CatSharedField>().AsNoTracking().Where(f => !f.IsArchived).OrderBy(f => f.Label).ToListAsync();
        BundleCandidates = (await admin.ListItemsAsync(null)).Where(i => i.Id != id && i.Kind != CatItemKind.Bundle && i.State != CatItemState.Archived).ToList();

        if (fillForms)
        {
            Details = new DetailsForm
            {
                Name = item.Name,
                ShortDescription = item.ShortDescription,
                Description = item.Description,
                Icon = item.Icon,
                Kind = item.Kind,
                Cost = item.CostMinor is { } c ? (c / 100m).ToString("0.00", CultureInfo.InvariantCulture) : null,
                Currency = item.Currency,
                DeliveryDays = item.DeliveryDays,
                MaxQuantity = item.MaxQuantity,
                AllowRequestedFor = item.AllowRequestedFor,
                FulfillmentGroupId = item.FulfillmentGroupId,
                PostSubmitMessage = item.PostSubmitMessage,
                CategoryIds = item.Categories.Select(x => x.CategoryId).ToList(),
                BundleItemIds = item.BundleItemIds,
            };
        }
        if (!keepLevels && (fillForms || Levels.Count == 0))
        {
            Levels = Item.Approval?.Levels.Select(FromConfig).ToList() ?? [];
        }
        var userIds = Levels.SelectMany(l => l.UserIds).Distinct().ToList();
        People = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
    }
}
