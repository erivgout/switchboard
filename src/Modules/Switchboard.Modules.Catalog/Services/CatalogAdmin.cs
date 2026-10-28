using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Catalog.Domain;

namespace Switchboard.Modules.Catalog.Services;

public static class CatalogPrivileges
{
    public const string Manage = "catalog.manage";
}

public sealed record ItemDetailsInput
{
    public string Name { get; init; } = "";
    public string ShortDescription { get; init; } = "";
    public string? Description { get; init; }
    public string Icon { get; init; } = "bi-box-seam";
    public CatItemKind Kind { get; init; } = CatItemKind.Standard;
    public long? CostMinor { get; init; }
    public string Currency { get; init; } = "USD";
    public int? DeliveryDays { get; init; }
    public int MaxQuantity { get; init; } = 1;
    public bool AllowRequestedFor { get; init; }
    public Guid? FulfillmentGroupId { get; init; }
    public string? PostSubmitMessage { get; init; }
    public IReadOnlyList<Guid> CategoryIds { get; init; } = [];
    public IReadOnlyList<Guid> BundleItemIds { get; init; } = [];
}

public sealed record FieldInput
{
    public string Label { get; init; } = "";
    public CatFieldType FieldType { get; init; } = CatFieldType.Text;
    public bool Required { get; init; }
    public string? HelpText { get; init; }
    public IReadOnlyList<string> Options { get; init; } = [];
    /// <summary>Optional; derived from the label when empty. Can't change later.</summary>
    public string? Key { get; init; }
}

/// <summary>Catalog administration (FR-SRM-1..5). Every method demands <c>catalog.manage</c>, saves and writes the audit log.</summary>
public interface ICatalogAdmin
{
    Task<IReadOnlyList<CatalogItemSummary>> ListItemsAsync(CatItemState? state, CancellationToken ct = default);
    Task<CatalogItemDetails> GetItemAsync(Guid itemId, CancellationToken ct = default);
    Task<CatItem> CreateItemAsync(string name, Guid? categoryId, CancellationToken ct = default);
    Task UpdateItemAsync(Guid itemId, ItemDetailsInput input, CancellationToken ct = default);
    Task SetStateAsync(Guid itemId, CatItemState state, CancellationToken ct = default);
    Task UpdateAudienceAsync(Guid itemId, IReadOnlyList<Guid> requesterGroupIds, IReadOnlyList<Guid> departmentIds, CancellationToken ct = default);
    /// <summary>Sets the approval chain; null or empty removes approvals.</summary>
    Task UpdateApprovalAsync(Guid itemId, ApprovalConfig? config, CancellationToken ct = default);

    Task<CatItemField> AddFieldAsync(Guid itemId, FieldInput input, CancellationToken ct = default);
    Task<CatItemField> AddSharedFieldAsync(Guid itemId, Guid sharedFieldId, bool required, CancellationToken ct = default);
    Task UpdateFieldAsync(Guid itemId, Guid fieldId, FieldInput input, CancellationToken ct = default);
    Task RemoveFieldAsync(Guid itemId, Guid fieldId, CancellationToken ct = default);
    Task MoveFieldAsync(Guid itemId, Guid fieldId, int direction, CancellationToken ct = default);

    Task<IReadOnlyList<CatalogCategoryView>> ListCategoriesAsync(CancellationToken ct = default);
    Task<CatCategory> SaveCategoryAsync(Guid? categoryId, string name, string? description, string icon, CancellationToken ct = default);
    Task MoveCategoryAsync(Guid categoryId, int direction, CancellationToken ct = default);
    Task DeleteCategoryAsync(Guid categoryId, CancellationToken ct = default);

    Task<IReadOnlyList<(CatSharedField Field, int UsedBy)>> ListSharedFieldsAsync(CancellationToken ct = default);
    Task<CatSharedField> SaveSharedFieldAsync(Guid? sharedFieldId, FieldInput input, CancellationToken ct = default);
    Task ArchiveSharedFieldAsync(Guid sharedFieldId, bool archived, CancellationToken ct = default);
}

internal sealed class CatalogAdmin(
    SwitchboardDbContext db,
    IPermissionService permissions,
    IAuditLog audit,
    CatalogService catalog,
    TimeProvider time) : ICatalogAdmin
{
    public const int MaxFields = 50;
    public const int MaxApprovalLevels = 5;
    public const int MaxManagerLevels = 5;

    public static readonly IReadOnlyList<string> Icons =
    [
        "bi-box-seam", "bi-laptop", "bi-pc-display", "bi-display", "bi-phone", "bi-tablet", "bi-headset", "bi-keyboard", "bi-mouse",
        "bi-printer", "bi-usb-drive", "bi-camera-video", "bi-telephone", "bi-wifi", "bi-router", "bi-key", "bi-shield-lock",
        "bi-person-badge", "bi-person-plus", "bi-people", "bi-envelope", "bi-cloud", "bi-database", "bi-code-slash", "bi-window-stack",
        "bi-app-indicator", "bi-file-earmark-text", "bi-calendar-event", "bi-briefcase", "bi-building", "bi-truck", "bi-tools",
        "bi-bag", "bi-gift", "bi-mortarboard", "bi-lightning-charge", "bi-grid", "bi-hdd-network", "bi-cart", "bi-credit-card",
    ];

    private DbSet<CatItem> Items => db.Set<CatItem>();

    public async Task<IReadOnlyList<CatalogItemSummary>> ListItemsAsync(CatItemState? state, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var query = Items.AsQueryable();
        if (state is { } s)
        {
            query = query.Where(i => i.State == s);
        }
        return await CatalogService.Summaries(query.OrderBy(i => i.Name), ct);
    }

    public async Task<CatalogItemDetails> GetItemAsync(Guid itemId, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var item = await Items.AsNoTracking()
            .Include(i => i.Fields).ThenInclude(f => f.SharedField)
            .Include(i => i.Categories)
            .AsSplitQuery()
            .FirstOrDefaultAsync(i => i.Id == itemId, ct) ?? throw new NotFoundException();
        return await catalog.DetailsAsync(item, ct);
    }

    public async Task<CatItem> CreateItemAsync(string name, Guid? categoryId, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var trimmed = RequireText(name, "name", "Give the item a name.", 200);
        var item = new CatItem { Name = trimmed, ShortDescription = "", CreatedAt = time.GetUtcNow() };
        if (categoryId is { } c)
        {
            if (!await db.Set<CatCategory>().AnyAsync(x => x.Id == c, ct))
            {
                throw new ValidationException("category", "That category doesn't exist.");
            }
            item.Categories.Add(new CatItemCategory { ItemId = item.Id, CategoryId = c });
        }
        Items.Add(item);
        audit.Record("catalog.item.created", "catalog_item", item.Id.ToString(), new { item.Name });
        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task UpdateItemAsync(Guid itemId, ItemDetailsInput input, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var item = await Items.Include(i => i.Categories).FirstOrDefaultAsync(i => i.Id == itemId, ct) ?? throw new NotFoundException();
        var errors = new Dictionary<string, string[]>();
        void Error(string key, string message) => errors[key] = [message];

        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 200)
        {
            Error("name", name.Length == 0 ? "Give the item a name." : "Keep the name under 200 characters.");
        }
        var shortDescription = (input.ShortDescription ?? "").Trim();
        if (shortDescription.Length > 300)
        {
            Error("shortDescription", "Keep the summary under 300 characters.");
        }
        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        if (description is { Length: > 8000 })
        {
            Error("description", "Keep the description under 8,000 characters.");
        }
        if (!Icons.Contains(input.Icon))
        {
            Error("icon", "Choose one of the icons.");
        }
        if (!Enum.IsDefined(input.Kind))
        {
            Error("kind", "Choose a kind.");
        }
        if (input.CostMinor is < 0 or > 100_000_000_000)
        {
            Error("cost", "Enter a cost of zero or more.");
        }
        var currency = (input.Currency ?? "").Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
        {
            Error("currency", "Use a three-letter currency code, like USD.");
        }
        if (input.DeliveryDays is < 0 or > 365)
        {
            Error("deliveryDays", "Use 0 to 365 working days.");
        }
        if (input.MaxQuantity is < 1 or > 100)
        {
            Error("maxQuantity", "Allow 1 to 100 per request.");
        }
        var message = string.IsNullOrWhiteSpace(input.PostSubmitMessage) ? null : input.PostSubmitMessage.Trim();
        if (message is { Length: > 1000 })
        {
            Error("postSubmitMessage", "Keep the message under 1,000 characters.");
        }
        if (input.FulfillmentGroupId is { } g && !await db.AgentGroups.AnyAsync(x => x.Id == g, ct))
        {
            Error("fulfillmentGroupId", "That group doesn't exist.");
        }
        var categoryIds = input.CategoryIds.Distinct().ToList();
        if (await db.Set<CatCategory>().CountAsync(c => categoryIds.Contains(c.Id), ct) != categoryIds.Count)
        {
            Error("categoryIds", "One of the categories doesn't exist.");
        }
        var bundleIds = input.Kind == CatItemKind.Bundle ? input.BundleItemIds.Distinct().Where(id => id != itemId).ToList() : [];
        if (bundleIds.Count > 0)
        {
            var valid = await Items.CountAsync(i => bundleIds.Contains(i.Id) && i.Kind != CatItemKind.Bundle && i.State != CatItemState.Archived, ct);
            if (valid != bundleIds.Count)
            {
                Error("bundleItemIds", "A bundle can only include items that aren't bundles or archived.");
            }
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        item.Name = name;
        item.ShortDescription = shortDescription;
        item.Description = description;
        item.Icon = input.Icon;
        item.Kind = input.Kind;
        item.CostMinor = input.CostMinor;
        item.Currency = currency;
        item.DeliveryDays = input.DeliveryDays;
        item.MaxQuantity = input.MaxQuantity;
        item.AllowRequestedFor = input.AllowRequestedFor;
        item.FulfillmentGroupId = input.FulfillmentGroupId;
        item.PostSubmitMessage = message;
        item.BundleItemIds = bundleIds;
        item.Categories.RemoveAll(c => !categoryIds.Contains(c.CategoryId));
        foreach (var id in categoryIds.Where(id => item.Categories.All(c => c.CategoryId != id)))
        {
            item.Categories.Add(new CatItemCategory { ItemId = item.Id, CategoryId = id });
        }
        if (item.State == CatItemState.Published)
        {
            EnsurePublishable(item);
        }
        audit.Record("catalog.item.updated", "catalog_item", item.Id.ToString(), new { item.Name });
        await db.SaveChangesAsync(ct);
    }

    public async Task SetStateAsync(Guid itemId, CatItemState state, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var item = await Items.Include(i => i.Categories).Include(i => i.Fields).FirstOrDefaultAsync(i => i.Id == itemId, ct) ?? throw new NotFoundException();
        if (!Enum.IsDefined(state) || item.State == state)
        {
            return;
        }
        if (state == CatItemState.Published)
        {
            EnsurePublishable(item);
            if (CatalogService.ParseApproval(item.ApprovalConfigJson) is { } config)
            {
                await ValidateApprovalAsync(item, config, ct);
            }
            item.PublishedAt ??= time.GetUtcNow();
        }
        item.State = state;
        audit.Record("catalog.item.state_changed", "catalog_item", item.Id.ToString(), new { state });
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAudienceAsync(Guid itemId, IReadOnlyList<Guid> requesterGroupIds, IReadOnlyList<Guid> departmentIds, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var item = await Items.FirstOrDefaultAsync(i => i.Id == itemId, ct) ?? throw new NotFoundException();
        var groups = requesterGroupIds.Distinct().ToList();
        var departments = departmentIds.Distinct().ToList();
        if (await db.RequesterGroups.CountAsync(g => groups.Contains(g.Id), ct) != groups.Count)
        {
            throw new ValidationException("groups", "One of the requester groups doesn't exist.");
        }
        if (await db.Departments.CountAsync(d => departments.Contains(d.Id), ct) != departments.Count)
        {
            throw new ValidationException("departments", "One of the departments doesn't exist.");
        }
        item.VisibleToGroupIds = groups;
        item.VisibleToDepartmentIds = departments;
        audit.Record("catalog.item.audience_updated", "catalog_item", item.Id.ToString(), new { groups, departments });
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateApprovalAsync(Guid itemId, ApprovalConfig? config, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var item = await Items.Include(i => i.Fields).ThenInclude(f => f.SharedField).FirstOrDefaultAsync(i => i.Id == itemId, ct) ?? throw new NotFoundException();
        if (config is null || config.IsEmpty)
        {
            item.ApprovalConfigJson = null;
        }
        else
        {
            await ValidateApprovalAsync(item, config, ct);
            item.ApprovalConfigJson = JsonSerializer.Serialize(Normalize(config), JsonDefaults.Options);
        }
        audit.Record("catalog.item.approvals_updated", "catalog_item", item.Id.ToString(), new { levels = config?.Levels.Count ?? 0 });
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- fields

    public async Task<CatItemField> AddFieldAsync(Guid itemId, FieldInput input, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var item = await LoadWithFieldsAsync(itemId, ct);
        if (item.Fields.Count >= MaxFields)
        {
            throw new ValidationException("field", $"A form can have at most {MaxFields} questions.");
        }
        var (label, help, options) = ValidateField(input);
        var key = string.IsNullOrWhiteSpace(input.Key) ? CatalogForm.KeyFrom(label) : input.Key.Trim();
        if (!CatalogForm.IsValidKey(key))
        {
            throw new ValidationException("key", "Keys use lower-case letters, digits and underscores, starting with a letter.");
        }
        key = UniqueKey(key, item.Fields.Select(f => f.Key));
        var field = new CatItemField
        {
            ItemId = item.Id,
            Key = key,
            Label = label,
            FieldType = input.FieldType,
            Required = input.Required,
            HelpText = help,
            Options = options,
            Order = item.Fields.Count == 0 ? 1 : item.Fields.Max(f => f.Order) + 1,
        };
        db.Set<CatItemField>().Add(field);
        audit.Record("catalog.field.added", "catalog_item", item.Id.ToString(), new { field.Key, field.FieldType });
        await db.SaveChangesAsync(ct);
        return field;
    }

    public async Task<CatItemField> AddSharedFieldAsync(Guid itemId, Guid sharedFieldId, bool required, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var item = await LoadWithFieldsAsync(itemId, ct);
        var shared = await db.Set<CatSharedField>().FirstOrDefaultAsync(s => s.Id == sharedFieldId && !s.IsArchived, ct)
            ?? throw new ValidationException("sharedField", "That shared field doesn't exist or is archived.");
        if (item.Fields.Any(f => f.Key == shared.Key))
        {
            throw new ValidationException("sharedField", $"This form already has a question with the key {shared.Key}.");
        }
        if (item.Fields.Count >= MaxFields)
        {
            throw new ValidationException("field", $"A form can have at most {MaxFields} questions.");
        }
        var field = new CatItemField
        {
            ItemId = item.Id,
            SharedFieldId = shared.Id,
            Key = shared.Key,
            Label = shared.Label,
            FieldType = shared.FieldType,
            Required = required,
            Order = item.Fields.Count == 0 ? 1 : item.Fields.Max(f => f.Order) + 1,
        };
        db.Set<CatItemField>().Add(field);
        audit.Record("catalog.field.added", "catalog_item", item.Id.ToString(), new { field.Key, shared = true });
        await db.SaveChangesAsync(ct);
        return field;
    }

    public async Task UpdateFieldAsync(Guid itemId, Guid fieldId, FieldInput input, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var item = await LoadWithFieldsAsync(itemId, ct);
        var field = item.Fields.FirstOrDefault(f => f.Id == fieldId) ?? throw new NotFoundException();
        field.Required = input.Required;
        if (field.SharedFieldId is null)
        {
            var (label, help, options) = ValidateField(input);
            if (input.FieldType != CatFieldType.UserLookup && field.FieldType == CatFieldType.UserLookup && UsedByApproval(item, field.Key))
            {
                throw new ValidationException("fieldType", "Approvals use this question to pick an approver. Change the approval settings first.");
            }
            field.Label = label;
            field.HelpText = help;
            field.FieldType = input.FieldType;
            field.Options = options;
        }
        audit.Record("catalog.field.updated", "catalog_item", item.Id.ToString(), new { field.Key });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveFieldAsync(Guid itemId, Guid fieldId, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var item = await LoadWithFieldsAsync(itemId, ct);
        var field = item.Fields.FirstOrDefault(f => f.Id == fieldId) ?? throw new NotFoundException();
        if (UsedByApproval(item, field.Key))
        {
            throw new ValidationException("field", "Approvals use this question to pick an approver. Change the approval settings first.");
        }
        db.Set<CatItemField>().Remove(field);
        audit.Record("catalog.field.removed", "catalog_item", item.Id.ToString(), new { field.Key });
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveFieldAsync(Guid itemId, Guid fieldId, int direction, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var item = await LoadWithFieldsAsync(itemId, ct);
        var ordered = item.Fields.OrderBy(f => f.Order).ToList();
        var index = ordered.FindIndex(f => f.Id == fieldId);
        if (index < 0)
        {
            throw new NotFoundException();
        }
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= ordered.Count)
        {
            return;
        }
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Order = i + 1;
        }
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- categories

    public async Task<IReadOnlyList<CatalogCategoryView>> ListCategoriesAsync(CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var counts = await db.Set<CatItemCategory>().AsNoTracking().GroupBy(c => c.CategoryId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return (await db.Set<CatCategory>().AsNoTracking().OrderBy(c => c.Order).ThenBy(c => c.Name).ToListAsync(ct))
            .Select(c => new CatalogCategoryView(c.Id, c.Name, c.Description, c.Icon, c.Order, counts.GetValueOrDefault(c.Id)))
            .ToList();
    }

    public async Task<CatCategory> SaveCategoryAsync(Guid? categoryId, string name, string? description, string icon, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var trimmed = RequireText(name, "name", "Give the category a name.", 100);
        var desc = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (desc is { Length: > 500 })
        {
            throw new ValidationException("description", "Keep the description under 500 characters.");
        }
        if (!Icons.Contains(icon))
        {
            throw new ValidationException("icon", "Choose one of the icons.");
        }
        var categories = db.Set<CatCategory>();
        if (await categories.AnyAsync(c => c.Name == trimmed && c.Id != categoryId, ct))
        {
            throw new ValidationException("name", $"There's already a category called {trimmed}.");
        }
        CatCategory category;
        if (categoryId is { } id)
        {
            category = await categories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException();
        }
        else
        {
            category = new CatCategory { Order = (await categories.MaxAsync(c => (int?)c.Order, ct) ?? 0) + 1, CreatedAt = time.GetUtcNow() };
            categories.Add(category);
        }
        category.Name = trimmed;
        category.Description = desc;
        category.Icon = icon;
        audit.Record(categoryId is null ? "catalog.category.created" : "catalog.category.updated", "catalog_category", category.Id.ToString(), new { category.Name });
        await db.SaveChangesAsync(ct);
        return category;
    }

    public async Task MoveCategoryAsync(Guid categoryId, int direction, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var ordered = await db.Set<CatCategory>().OrderBy(c => c.Order).ThenBy(c => c.Name).ToListAsync(ct);
        var index = ordered.FindIndex(c => c.Id == categoryId);
        if (index < 0)
        {
            throw new NotFoundException();
        }
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= ordered.Count)
        {
            return;
        }
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Order = i + 1;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteCategoryAsync(Guid categoryId, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var category = await db.Set<CatCategory>().FirstOrDefaultAsync(c => c.Id == categoryId, ct) ?? throw new NotFoundException();
        db.Set<CatCategory>().Remove(category);
        audit.Record("catalog.category.deleted", "catalog_category", category.Id.ToString(), new { category.Name });
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- shared fields

    public async Task<IReadOnlyList<(CatSharedField Field, int UsedBy)>> ListSharedFieldsAsync(CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var usage = await db.Set<CatItemField>().AsNoTracking().Where(f => f.SharedFieldId != null)
            .GroupBy(f => f.SharedFieldId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var fields = await db.Set<CatSharedField>().AsNoTracking().OrderBy(f => f.IsArchived).ThenBy(f => f.Label).ToListAsync(ct);
        return fields.Select(f => (f, usage.GetValueOrDefault(f.Id))).ToList();
    }

    public async Task<CatSharedField> SaveSharedFieldAsync(Guid? sharedFieldId, FieldInput input, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var (label, help, options) = ValidateField(input);
        var set = db.Set<CatSharedField>();
        CatSharedField field;
        if (sharedFieldId is { } id)
        {
            field = await set.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException();
            if (input.FieldType != field.FieldType)
            {
                throw new ValidationException("fieldType", "A shared field's type can't change once it exists. Make a new shared field instead.");
            }
        }
        else
        {
            var key = string.IsNullOrWhiteSpace(input.Key) ? CatalogForm.KeyFrom(label) : input.Key.Trim();
            if (!CatalogForm.IsValidKey(key))
            {
                throw new ValidationException("key", "Keys use lower-case letters, digits and underscores, starting with a letter.");
            }
            if (await set.AnyAsync(f => f.Key == key, ct))
            {
                throw new ValidationException("key", $"A shared field already uses the key {key}.");
            }
            field = new CatSharedField { Key = key, FieldType = input.FieldType, CreatedAt = time.GetUtcNow() };
            set.Add(field);
        }
        field.Label = label;
        field.HelpText = help;
        field.Options = options;
        audit.Record(sharedFieldId is null ? "catalog.shared_field.created" : "catalog.shared_field.updated", "catalog_shared_field", field.Id.ToString(), new { field.Key });
        await db.SaveChangesAsync(ct);
        return field;
    }

    public async Task ArchiveSharedFieldAsync(Guid sharedFieldId, bool archived, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var field = await db.Set<CatSharedField>().FirstOrDefaultAsync(f => f.Id == sharedFieldId, ct) ?? throw new NotFoundException();
        field.IsArchived = archived;
        audit.Record(archived ? "catalog.shared_field.archived" : "catalog.shared_field.unarchived", "catalog_shared_field", field.Id.ToString(), new { field.Key });
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- helpers

    private Task DemandAsync(CancellationToken ct) => permissions.DemandAsync(CatalogPrivileges.Manage, null, ct);

    private async Task<CatItem> LoadWithFieldsAsync(Guid itemId, CancellationToken ct) =>
        await Items.Include(i => i.Fields).ThenInclude(f => f.SharedField).FirstOrDefaultAsync(i => i.Id == itemId, ct) ?? throw new NotFoundException();

    private static (string Label, string? Help, List<string> Options) ValidateField(FieldInput input)
    {
        var label = RequireText(input.Label, "label", "Write the question.", 200);
        if (!Enum.IsDefined(input.FieldType))
        {
            throw new ValidationException("fieldType", "Choose a field type.");
        }
        var help = string.IsNullOrWhiteSpace(input.HelpText) ? null : input.HelpText.Trim();
        if (help is { Length: > 500 })
        {
            throw new ValidationException("helpText", "Keep the help text under 500 characters.");
        }
        var options = new List<string>();
        if (CatalogForm.UsesOptions(input.FieldType))
        {
            options = input.Options.Select(o => (o ?? "").Trim()).Where(o => o.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (options.Count == 0)
            {
                throw new ValidationException("options", "Add at least one option, one per line.");
            }
            if (options.Count > CatalogForm.MaxOptions || options.Any(o => o.Length > 200))
            {
                throw new ValidationException("options", $"Use at most {CatalogForm.MaxOptions} options of up to 200 characters each.");
            }
        }
        return (label, help, options);
    }

    private static void EnsurePublishable(CatItem item)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(item.ShortDescription))
        {
            errors["shortDescription"] = ["Add a one-line summary so requesters know what this is."];
        }
        if (item.Categories.Count == 0)
        {
            errors["categoryIds"] = ["Put the item in at least one category so requesters can find it."];
        }
        if (item.Kind == CatItemKind.Bundle && item.BundleItemIds.Count == 0)
        {
            errors["bundleItemIds"] = ["Add the items this bundle includes."];
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }

    private async Task ValidateApprovalAsync(CatItem item, ApprovalConfig config, CancellationToken ct)
    {
        if (config.Levels.Count > MaxApprovalLevels)
        {
            throw new ValidationException("levels", $"Use at most {MaxApprovalLevels} approval levels.");
        }
        var order = 0;
        foreach (var level in config.Levels)
        {
            order++;
            if (!Enum.IsDefined(level.Rule))
            {
                throw new ValidationException("levels", $"Level {order} needs a rule.");
            }
            if (level.Sources.Count == 0)
            {
                throw new ValidationException("levels", $"Level {order} has no approvers. Add someone or remove the level.");
            }
            foreach (var source in level.Sources)
            {
                switch (source.Kind)
                {
                    case ApproverSourceKind.Users:
                        var ids = source.UserIds ?? [];
                        if (ids.Count == 0 || await db.Users.CountAsync(u => ids.Contains(u.Id) && u.IsActive, ct) != ids.Distinct().Count())
                        {
                            throw new ValidationException("levels", $"Level {order}: choose active people as approvers.");
                        }
                        break;
                    case ApproverSourceKind.Manager:
                        if (source.ManagerLevel is not (>= 1 and <= MaxManagerLevels))
                        {
                            throw new ValidationException("levels", $"Level {order}: the manager level must be 1 to {MaxManagerLevels}.");
                        }
                        break;
                    case ApproverSourceKind.DepartmentHead:
                        break;
                    case ApproverSourceKind.AgentGroup:
                        if (source.GroupId is not { } g || !await db.AgentGroups.AnyAsync(x => x.Id == g, ct))
                        {
                            throw new ValidationException("levels", $"Level {order}: choose an agent group.");
                        }
                        break;
                    case ApproverSourceKind.LookupField:
                        var field = item.Fields.FirstOrDefault(f => f.Key == source.FieldKey);
                        if (field is null || FormField.From(field).Type != CatFieldType.UserLookup)
                        {
                            throw new ValidationException("levels", $"Level {order}: choose a person question from this item's form.");
                        }
                        break;
                    default:
                        throw new ValidationException("levels", $"Level {order} has an unknown approver type.");
                }
            }
        }
    }

    /// <summary>Drops values that don't belong to each source kind, so stored JSON stays tidy.</summary>
    private static ApprovalConfig Normalize(ApprovalConfig config) => new()
    {
        Levels = config.Levels.Select(l => new ApprovalLevelConfig
        {
            Name = string.IsNullOrWhiteSpace(l.Name) ? null : l.Name.Trim(),
            Rule = l.Rule,
            Sources = l.Sources.Select(s => s.Kind switch
            {
                ApproverSourceKind.Users => new ApproverSource { Kind = s.Kind, UserIds = s.UserIds?.Distinct().ToList() },
                ApproverSourceKind.Manager => new ApproverSource { Kind = s.Kind, ManagerLevel = s.ManagerLevel },
                ApproverSourceKind.AgentGroup => new ApproverSource { Kind = s.Kind, GroupId = s.GroupId },
                ApproverSourceKind.LookupField => new ApproverSource { Kind = s.Kind, FieldKey = s.FieldKey },
                _ => new ApproverSource { Kind = s.Kind },
            }).ToList(),
        }).ToList(),
    };

    private static bool UsedByApproval(CatItem item, string key) =>
        CatalogService.ParseApproval(item.ApprovalConfigJson)?.Levels.SelectMany(l => l.Sources)
            .Any(s => s.Kind == ApproverSourceKind.LookupField && s.FieldKey == key) == true;

    private static string UniqueKey(string key, IEnumerable<string> existing)
    {
        var taken = existing.ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(key))
        {
            return key;
        }
        for (var i = 2; ; i++)
        {
            var candidate = $"{key}_{i}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string RequireText(string? value, string field, string emptyMessage, int max)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0)
        {
            throw new ValidationException(field, emptyMessage);
        }
        if (trimmed.Length > max)
        {
            throw new ValidationException(field, $"Keep it under {max} characters.");
        }
        return trimmed;
    }
}
