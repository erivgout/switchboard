using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Catalog.Domain;

namespace Switchboard.Modules.Catalog.Services;

public sealed record CatalogItemSummary(
    Guid Id,
    string Name,
    string ShortDescription,
    string Icon,
    CatItemKind Kind,
    CatItemState State,
    long? CostMinor,
    string Currency,
    int? DeliveryDays,
    bool NeedsApproval,
    bool IsLimited,
    IReadOnlyList<Guid> CategoryIds);

public sealed record CatalogCategoryView(Guid Id, string Name, string? Description, string Icon, int Order, int ItemCount);

/// <summary>An item with its form, as the viewer may request it.</summary>
public sealed class CatalogItemDetails
{
    public required CatItem Item { get; init; }
    public required IReadOnlyList<FormField> Fields { get; init; }
    public required ApprovalConfig? Approval { get; init; }
    public required IReadOnlyList<CatalogItemSummary> BundleItems { get; init; }
    public required IReadOnlyList<string> CategoryNames { get; init; }
    public string? FulfillmentGroupName { get; init; }
}

/// <summary>Browsing the catalog (FR-SRM-1, FR-SRM-11). Requesters see published items their groups and department may see.</summary>
public interface ICatalogService
{
    Task<IReadOnlyList<CatalogCategoryView>> CategoriesAsync(bool forAgent, CancellationToken ct = default);
    /// <summary>Published items the current user may request (agents: every published item), filtered by search text and category.</summary>
    Task<IReadOnlyList<CatalogItemSummary>> BrowseAsync(string? query, Guid? categoryId, bool forAgent, int take = 200, CancellationToken ct = default);
    /// <summary>Most requested in the last 90 days, newest published as a tiebreak.</summary>
    Task<IReadOnlyList<CatalogItemSummary>> PopularAsync(bool forAgent, int take = 4, CancellationToken ct = default);
    /// <summary>One published item the current user may see, or <see cref="NotFoundException"/>.</summary>
    Task<CatalogItemDetails> GetAsync(Guid itemId, bool forAgent, CancellationToken ct = default);
    /// <summary>Whether <paramref name="userId"/> may request the item (published and visible to them).</summary>
    Task<bool> IsVisibleToAsync(Guid itemId, Guid userId, CancellationToken ct = default);
    /// <summary>Plain-language approval steps for the requester's preview, e.g. "Your manager, Grace Kim".</summary>
    Task<IReadOnlyList<string>> DescribeApprovalAsync(ApprovalConfig? config, Guid? personId, IReadOnlyList<FormField> fields, CancellationToken ct = default);
}

internal sealed class CatalogService(SwitchboardDbContext db, IActorAccessor actors, IPermissionService permissions) : ICatalogService
{
    private DbSet<CatItem> Items => db.Set<CatItem>();

    public async Task<IReadOnlyList<CatalogCategoryView>> CategoriesAsync(bool forAgent, CancellationToken ct = default)
    {
        var visible = await VisibleQueryAsync(forAgent, ct);
        var counts = await db.Set<CatItemCategory>().AsNoTracking()
            .Where(c => visible.Any(i => i.Id == c.ItemId))
            .GroupBy(c => c.CategoryId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var categories = await db.Set<CatCategory>().AsNoTracking().OrderBy(c => c.Order).ThenBy(c => c.Name).ToListAsync(ct);
        return categories.Select(c => new CatalogCategoryView(c.Id, c.Name, c.Description, c.Icon, c.Order, counts.GetValueOrDefault(c.Id))).ToList();
    }

    public async Task<IReadOnlyList<CatalogItemSummary>> BrowseAsync(string? query, Guid? categoryId, bool forAgent, int take = 200, CancellationToken ct = default)
    {
        var items = await VisibleQueryAsync(forAgent, ct);
        var q = (query ?? "").Trim();
        if (q.Length > 0)
        {
            q = q.Length > 100 ? q[..100] : q;
            items = items.Where(i => i.Name.Contains(q) || i.ShortDescription.Contains(q) || (i.Description != null && i.Description.Contains(q)));
        }
        if (categoryId is { } cat)
        {
            items = items.Where(i => i.Categories.Any(c => c.CategoryId == cat));
        }
        return await Summaries(items.OrderBy(i => i.Name).Take(Math.Clamp(take, 1, 500)), ct);
    }

    public async Task<IReadOnlyList<CatalogItemSummary>> PopularAsync(bool forAgent, int take = 4, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-90);
        var items = await VisibleQueryAsync(forAgent, ct);
        var ranked = items
            .Select(i => new
            {
                Item = i,
                Requests = db.Set<CatRequestedItem>().Count(r => r.ItemId == i.Id && r.ParentId == null && r.CreatedAt >= since),
            })
            .OrderByDescending(x => x.Requests).ThenByDescending(x => x.Item.PublishedAt)
            .Select(x => x.Item)
            .Take(Math.Clamp(take, 1, 20));
        return await Summaries(ranked, ct);
    }

    public async Task<CatalogItemDetails> GetAsync(Guid itemId, bool forAgent, CancellationToken ct = default)
    {
        var visible = await VisibleQueryAsync(forAgent, ct);
        var item = await visible.AsNoTracking()
            .Include(i => i.Fields).ThenInclude(f => f.SharedField)
            .Include(i => i.Categories)
            .AsSplitQuery()
            .FirstOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new NotFoundException();
        return await DetailsAsync(item, ct);
    }

    internal async Task<CatalogItemDetails> DetailsAsync(CatItem item, CancellationToken ct)
    {
        var categoryIds = item.Categories.Select(c => c.CategoryId).ToList();
        var categoryNames = await db.Set<CatCategory>().AsNoTracking().Where(c => categoryIds.Contains(c.Id))
            .OrderBy(c => c.Order).Select(c => c.Name).ToListAsync(ct);
        var bundle = item.Kind == CatItemKind.Bundle && item.BundleItemIds.Count > 0
            ? await Summaries(Items.AsNoTracking().Where(i => item.BundleItemIds.Contains(i.Id)), ct)
            : [];
        var group = item.FulfillmentGroupId is { } g
            ? await db.AgentGroups.AsNoTracking().Where(x => x.Id == g).Select(x => x.Name).FirstOrDefaultAsync(ct)
            : null;
        return new CatalogItemDetails
        {
            Item = item,
            Fields = item.Fields.OrderBy(f => f.Order).Select(FormField.From).ToList(),
            Approval = ParseApproval(item.ApprovalConfigJson),
            BundleItems = bundle,
            CategoryNames = categoryNames,
            FulfillmentGroupName = group,
        };
    }

    public async Task<bool> IsVisibleToAsync(Guid itemId, Guid userId, CancellationToken ct = default) =>
        await (await VisibleToUserAsync(userId, ct)).AnyAsync(i => i.Id == itemId, ct);

    public async Task<IReadOnlyList<string>> DescribeApprovalAsync(ApprovalConfig? config, Guid? personId, IReadOnlyList<FormField> fields, CancellationToken ct = default)
    {
        if (config is null || config.IsEmpty)
        {
            return [];
        }
        var person = personId is { } pid
            ? await db.Users.AsNoTracking().Where(u => u.Id == pid).Select(u => new { u.ManagerId, u.DepartmentId }).FirstOrDefaultAsync(ct)
            : null;
        var userIds = config.Levels.SelectMany(l => l.Sources).SelectMany(s => s.UserIds ?? []).Distinct().ToList();
        if (person?.ManagerId is { } m)
        {
            userIds.Add(m);
        }
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var groupIds = config.Levels.SelectMany(l => l.Sources).Select(s => s.GroupId).OfType<Guid>().Distinct().ToList();
        var groups = await db.AgentGroups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        var head = person?.DepartmentId is { } d
            ? await db.Departments.AsNoTracking().Where(x => x.Id == d).Select(x => x.HeadUserId == null ? null : db.Users.Where(u => u.Id == x.HeadUserId).Select(u => u.DisplayName).FirstOrDefault()).FirstOrDefaultAsync(ct)
            : null;

        var steps = new List<string>();
        foreach (var level in config.Levels)
        {
            var parts = level.Sources.Select(s => s.Kind switch
            {
                ApproverSourceKind.Users => string.Join(", ", (s.UserIds ?? []).Select(id => names.GetValueOrDefault(id, "a named approver"))),
                ApproverSourceKind.Manager when (s.ManagerLevel ?? 1) == 1 => person?.ManagerId is { } mid && names.TryGetValue(mid, out var mn) ? $"your manager, {mn}" : "your manager",
                ApproverSourceKind.Manager => (s.ManagerLevel ?? 1) == 2 ? "your manager's manager" : $"your manager {s.ManagerLevel} levels up",
                ApproverSourceKind.DepartmentHead => head is null ? "the head of your department" : $"the head of your department, {head}",
                ApproverSourceKind.AgentGroup => s.GroupId is { } gid && groups.TryGetValue(gid, out var gn) ? $"the {gn} team" : "a team",
                ApproverSourceKind.LookupField => $"the person you choose for {fields.FirstOrDefault(f => f.Key == s.FieldKey)?.Label ?? "the form"}",
                _ => "an approver",
            }).Where(p => p.Length > 0).ToList();
            var who = string.Join(level.Rule == ApprovalRule.Any ? " or " : " and ", parts);
            var rule = level.Rule switch
            {
                ApprovalRule.Everyone when parts.Count > 1 || level.Sources.Any(s => s.Kind == ApproverSourceKind.AgentGroup) => " (everyone)",
                ApprovalRule.Majority => " (a majority)",
                _ => "",
            };
            steps.Add(Capitalize(who) + rule);
        }
        return steps;
    }

    internal static ApprovalConfig? ParseApproval(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var config = JsonSerializer.Deserialize<ApprovalConfig>(json, JsonDefaults.Options);
            return config is { IsEmpty: false } ? config : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Published items the current viewer may browse.</summary>
    private async Task<IQueryable<CatItem>> VisibleQueryAsync(bool forAgent, CancellationToken ct)
    {
        var actor = actors.Current;
        if (forAgent)
        {
            if (!actor.IsInternal && (await permissions.CurrentAsync(ct))?.IsAgent != true)
            {
                throw new ForbiddenException();
            }
            return Items.Where(i => i.State == CatItemState.Published);
        }
        if (actor.IsInternal)
        {
            return Items.Where(i => i.State == CatItemState.Published);
        }
        return actor.UserId is { } uid ? await VisibleToUserAsync(uid, ct) : Items.Where(_ => false);
    }

    /// <summary>Visibility rule: no audience set, or the person is in one of the requester groups or departments.</summary>
    internal async Task<IQueryable<CatItem>> VisibleToUserAsync(Guid userId, CancellationToken ct)
    {
        var groups = await db.RequesterGroupMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.GroupId).ToListAsync(ct);
        var department = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DepartmentId).FirstOrDefaultAsync(ct);
        var departments = department is { } d ? new List<Guid> { d } : [];
        return Items.Where(i => i.State == CatItemState.Published
            && ((i.VisibleToGroupIds.Count == 0 && i.VisibleToDepartmentIds.Count == 0)
                || i.VisibleToGroupIds.Any(g => groups.Contains(g))
                || i.VisibleToDepartmentIds.Any(x => departments.Contains(x))));
    }

    internal static async Task<IReadOnlyList<CatalogItemSummary>> Summaries(IQueryable<CatItem> items, CancellationToken ct)
    {
        var rows = await items.AsNoTracking()
            .Select(i => new
            {
                i.Id, i.Name, i.ShortDescription, i.Icon, i.Kind, i.State, i.CostMinor, i.Currency, i.DeliveryDays, i.ApprovalConfigJson,
                Limited = i.VisibleToGroupIds.Count > 0 || i.VisibleToDepartmentIds.Count > 0,
                Categories = i.Categories.Select(c => c.CategoryId).ToList(),
            })
            .ToListAsync(ct);
        return rows.Select(r => new CatalogItemSummary(r.Id, r.Name, r.ShortDescription, r.Icon, r.Kind, r.State, r.CostMinor, r.Currency,
            r.DeliveryDays, ParseApproval(r.ApprovalConfigJson) is not null, r.Limited, r.Categories)).ToList();
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
