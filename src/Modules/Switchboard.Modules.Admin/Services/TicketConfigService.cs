using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Modules.Admin.Services;

public sealed record StatusRow(TicketStatus Status, int Tickets);

public sealed record StatusInput(string Name, string? RequesterLabel, StatusCategory Category, bool SlaTimerOn, bool IsDefault);

public sealed record CategoryNode(TicketCategory Category, IReadOnlyList<CategoryNode> Children, int Tickets);

public sealed record SourceRow(TicketSource Source, int Tickets);

/// <summary>Ticket statuses (FR-TKT-4), the category tree and ticket sources, per workspace where they are scoped.</summary>
public sealed partial class TicketConfigService(SwitchboardDbContext db, AdminAccess access, IAuditLog audit)
{
    public const int MaxCategoryLevel = 3;

    /// <summary>Icons offered for custom sources.</summary>
    public static readonly IReadOnlyList<string> SourceIcons =
    [
        "bi-question-circle", "bi-envelope", "bi-telephone", "bi-chat-dots", "bi-globe", "bi-megaphone", "bi-printer",
        "bi-building", "bi-person-badge", "bi-clipboard-check", "bi-bell", "bi-plug", "bi-hdd-network", "bi-qr-code",
    ];

    // ---------------------------------------------------------------- statuses

    public async Task<IReadOnlyList<StatusRow>> ListStatusesAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.FieldsManage, workspaceId, ct);
        var statuses = await db.TicketStatuses.AsNoTracking().Where(s => s.WorkspaceId == workspaceId)
            .OrderBy(s => s.Order).ThenBy(s => s.Name).ToListAsync(ct);
        var counts = await db.Tickets.Where(t => t.WorkspaceId == workspaceId).GroupBy(t => t.StatusId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return statuses.Select(s => new StatusRow(s, counts.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<StatusRow> GetStatusAsync(Guid id, CancellationToken ct = default)
    {
        var status = await db.TicketStatuses.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Privileges.FieldsManage, status.WorkspaceId, ct);
        return new StatusRow(status, await db.Tickets.CountAsync(t => t.StatusId == id, ct));
    }

    public async Task<TicketStatus> CreateStatusAsync(Guid workspaceId, StatusInput input, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.FieldsManage, workspaceId, ct);
        var name = await StatusNameAsync(workspaceId, input.Name, null, ct);
        ValidateCategory(input.Category);
        var status = new TicketStatus
        {
            WorkspaceId = workspaceId,
            Name = name,
            RequesterLabel = Label(input.RequesterLabel),
            Category = input.Category,
            SlaTimerOn = input.SlaTimerOn,
            Order = (await db.TicketStatuses.Where(s => s.WorkspaceId == workspaceId).MaxAsync(s => (int?)s.Order, ct) ?? 0) + 1,
        };
        db.TicketStatuses.Add(status);
        if (input.IsDefault)
        {
            await MakeDefaultAsync(status, ct);
        }
        audit.Record("statuses.created", "ticket_status", status.Id.ToString(), new { name, category = input.Category, slaTimerOn = input.SlaTimerOn, isDefault = input.IsDefault });
        await db.SaveChangesAsync(ct);
        return status;
    }

    public async Task UpdateStatusAsync(Guid id, StatusInput input, CancellationToken ct = default)
    {
        var status = await db.TicketStatuses.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Privileges.FieldsManage, status.WorkspaceId, ct);
        var changes = new List<string>();

        if (!status.IsSystem)
        {
            var name = await StatusNameAsync(status.WorkspaceId, input.Name, id, ct);
            ValidateCategory(input.Category);
            if (input.Category != status.Category && await db.Tickets.AnyAsync(t => t.StatusId == id, ct))
            {
                throw new ValidationException("Category",
                    "Tickets use this status, so its category can't change. Create a new status, or move its tickets by deleting this one.");
            }
            if (input.Category != status.Category && status.IsDefault)
            {
                throw new ValidationException("Category", "The default status must stay in the Open category.");
            }
            Track(changes, "name", status.Name, name, v => status.Name = v);
            Track(changes, "category", status.Category, input.Category, v => status.Category = v);
        }
        Track(changes, "requesterLabel", status.RequesterLabel, Label(input.RequesterLabel), v => status.RequesterLabel = v);
        Track(changes, "slaTimerOn", status.SlaTimerOn, input.SlaTimerOn, v => status.SlaTimerOn = v);

        if (input.IsDefault && !status.IsDefault)
        {
            await MakeDefaultAsync(status, ct);
            changes.Add("isDefault");
        }
        else if (!input.IsDefault && status.IsDefault)
        {
            throw new ValidationException("IsDefault", "New tickets need a default status. Make another status the default instead.");
        }

        if (changes.Count == 0)
        {
            return;
        }
        audit.Record("statuses.updated", "ticket_status", id.ToString(), new { fields = changes });
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveStatusAsync(Guid id, int delta, CancellationToken ct = default)
    {
        var status = await db.TicketStatuses.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Privileges.FieldsManage, status.WorkspaceId, ct);
        var all = await db.TicketStatuses.Where(s => s.WorkspaceId == status.WorkspaceId).OrderBy(s => s.Order).ThenBy(s => s.Name).ToListAsync(ct);
        if (Reorder(all, status, delta, (s, o) => s.Order = o))
        {
            audit.Record("statuses.reordered", "workspace", status.WorkspaceId.ToString(), new { order = all.Select(s => s.Name) });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Deletes a custom status. Tickets that use it move to <paramref name="replacementId"/> (same category) first.</summary>
    public async Task<int> DeleteStatusAsync(Guid id, Guid? replacementId, CancellationToken ct = default)
    {
        var status = await db.TicketStatuses.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Privileges.FieldsManage, status.WorkspaceId, ct);
        if (status.IsSystem)
        {
            throw new ValidationException("Status", "Built-in statuses can't be deleted.");
        }
        if (status.IsDefault)
        {
            throw new ValidationException("Status", "This is the default status for new tickets. Make another status the default first.");
        }

        return await db.InTransactionAsync(async () =>
        {
            var moved = 0;
            if (await db.Tickets.AnyAsync(t => t.StatusId == id, ct))
            {
                var replacement = replacementId is { } rid && rid != id
                    ? await db.TicketStatuses.FirstOrDefaultAsync(s => s.Id == rid && s.WorkspaceId == status.WorkspaceId, ct)
                    : null;
                if (replacement is null)
                {
                    throw new ValidationException("ReplacementId", "Tickets use this status. Choose the status they move to.");
                }
                if (replacement.Category != status.Category)
                {
                    throw new ValidationException("ReplacementId", $"Choose a status in the {status.Category} category, so the tickets keep their meaning.");
                }
                moved = await TicketRemap.ApplyAsync(db, t => t.StatusId == id,
                    t =>
                    {
                        t.StatusId = replacement.Id;
                        return [new FieldChange("Status", status.Name, replacement.Name)];
                    },
                    $"Changed status to {replacement.Name} because {status.Name} was removed", ct,
                    t => [new TicketStatusChanged(t.Id, t.WorkspaceId, status.Id, replacement.Id, status.Category, replacement.Category)]);
            }
            db.TicketStatuses.Remove(status);
            audit.Record("statuses.deleted", "ticket_status", id.ToString(), new { name = status.Name, moved, replacementId });
            await db.SaveChangesAsync(ct);
            return moved;
        }, ct);
    }

    private async Task MakeDefaultAsync(TicketStatus status, CancellationToken ct)
    {
        if (status.Category != StatusCategory.Open)
        {
            throw new ValidationException("IsDefault", "The default status must be in the Open category.");
        }
        foreach (var other in await db.TicketStatuses.Where(s => s.WorkspaceId == status.WorkspaceId && s.IsDefault && s.Id != status.Id).ToListAsync(ct))
        {
            other.IsDefault = false;
        }
        status.IsDefault = true;
    }

    private async Task<string> StatusNameAsync(Guid workspaceId, string? raw, Guid? exceptId, CancellationToken ct)
    {
        var name = (raw ?? "").Trim();
        if (name.Length is 0 or > 100)
        {
            throw new ValidationException("Name", name.Length == 0 ? "Name the status." : "Keep the name under 100 characters.");
        }
        if (await db.TicketStatuses.AnyAsync(s => s.WorkspaceId == workspaceId && s.Name == name && s.Id != exceptId, ct))
        {
            throw new ValidationException("Name", $"A status called {name} already exists.");
        }
        return name;
    }

    private static void ValidateCategory(StatusCategory category)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ValidationException("Category", "Choose what kind of status this is.");
        }
    }

    private static string? Label(string? raw)
    {
        var label = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        return label is { Length: > 100 } ? throw new ValidationException("RequesterLabel", "Keep the label under 100 characters.") : label;
    }

    // ---------------------------------------------------------------- categories

    public async Task<IReadOnlyList<CategoryNode>> CategoryTreeAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.FieldsManage, workspaceId, ct);
        var all = await db.TicketCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId).ToListAsync(ct);
        var usage = new Dictionary<Guid, int>();
        foreach (var row in await db.Tickets.Where(t => t.WorkspaceId == workspaceId && t.CategoryId != null)
                     .GroupBy(t => new { t.CategoryId, t.SubCategoryId, t.ItemCategoryId })
                     .Select(g => new { g.Key.CategoryId, g.Key.SubCategoryId, g.Key.ItemCategoryId, Count = g.Count() })
                     .ToListAsync(ct))
        {
            foreach (var id in new[] { row.CategoryId, row.SubCategoryId, row.ItemCategoryId }.OfType<Guid>())
            {
                usage[id] = usage.GetValueOrDefault(id) + row.Count;
            }
        }
        List<CategoryNode> Children(Guid? parent) => all.Where(c => c.ParentId == parent)
            .OrderBy(c => c.Order).ThenBy(c => c.Name)
            .Select(c => new CategoryNode(c, Children(c.Id), usage.GetValueOrDefault(c.Id)))
            .ToList();
        return Children(null);
    }

    public async Task<TicketCategory> GetCategoryAsync(Guid id, CancellationToken ct = default)
    {
        var category = await db.TicketCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Privileges.FieldsManage, category.WorkspaceId, ct);
        return category;
    }

    public async Task<TicketCategory> CreateCategoryAsync(Guid workspaceId, Guid? parentId, string name, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.FieldsManage, workspaceId, ct);
        var level = 1;
        if (parentId is { } pid)
        {
            var parent = await db.TicketCategories.FirstOrDefaultAsync(c => c.Id == pid && c.WorkspaceId == workspaceId, ct)
                ?? throw new ValidationException("ParentId", "That parent category no longer exists.");
            if (parent.Level >= MaxCategoryLevel)
            {
                throw new ValidationException("ParentId", "Categories go three levels deep: category, sub-category and item.");
            }
            level = parent.Level + 1;
        }
        var clean = await CategoryNameAsync(workspaceId, parentId, name, null, ct);
        var category = new TicketCategory
        {
            WorkspaceId = workspaceId,
            ParentId = parentId,
            Name = clean,
            Level = level,
            Order = (await db.TicketCategories.Where(c => c.WorkspaceId == workspaceId && c.ParentId == parentId).MaxAsync(c => (int?)c.Order, ct) ?? 0) + 1,
        };
        db.TicketCategories.Add(category);
        audit.Record("categories.created", "ticket_category", category.Id.ToString(), new { name = clean, parentId, level });
        await db.SaveChangesAsync(ct);
        return category;
    }

    public async Task RenameCategoryAsync(Guid id, string name, CancellationToken ct = default)
    {
        var category = await db.TicketCategories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Privileges.FieldsManage, category.WorkspaceId, ct);
        var clean = await CategoryNameAsync(category.WorkspaceId, category.ParentId, name, id, ct);
        if (clean == category.Name)
        {
            return;
        }
        audit.Record("categories.renamed", "ticket_category", id.ToString(), new { from = category.Name, to = clean });
        category.Name = clean;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Saves a drag-and-drop order of siblings. Ids that aren't siblings under <paramref name="parentId"/> are rejected.</summary>
    public async Task ReorderCategoriesAsync(Guid workspaceId, Guid? parentId, IReadOnlyList<Guid> orderedIds, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.FieldsManage, workspaceId, ct);
        var siblings = await db.TicketCategories.Where(c => c.WorkspaceId == workspaceId && c.ParentId == parentId).ToListAsync(ct);
        if (orderedIds.Count != siblings.Count || orderedIds.Distinct().Count() != siblings.Count || siblings.Any(s => !orderedIds.Contains(s.Id)))
        {
            throw new ValidationException("Order", "The category list changed while you were sorting it. Reload and try again.");
        }
        for (var i = 0; i < orderedIds.Count; i++)
        {
            siblings.First(s => s.Id == orderedIds[i]).Order = i + 1;
        }
        audit.Record("categories.reordered", "ticket_category", parentId?.ToString() ?? workspaceId.ToString(), new { count = siblings.Count });
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveCategoryAsync(Guid id, int delta, CancellationToken ct = default)
    {
        var category = await db.TicketCategories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Privileges.FieldsManage, category.WorkspaceId, ct);
        var siblings = await db.TicketCategories.Where(c => c.WorkspaceId == category.WorkspaceId && c.ParentId == category.ParentId)
            .OrderBy(c => c.Order).ThenBy(c => c.Name).ToListAsync(ct);
        if (Reorder(siblings, category, delta, (c, o) => c.Order = o))
        {
            audit.Record("categories.reordered", "ticket_category", category.ParentId?.ToString() ?? category.WorkspaceId.ToString(), new { count = siblings.Count });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Deletes a category without children. Tickets that use it move to <paramref name="replacementId"/> (same level),
    /// or lose that level of categorization when <paramref name="clear"/> is set (SR-DEL-2).
    /// </summary>
    public async Task<int> DeleteCategoryAsync(Guid id, Guid? replacementId, bool clear, CancellationToken ct = default)
    {
        var category = await db.TicketCategories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Privileges.FieldsManage, category.WorkspaceId, ct);
        if (await db.TicketCategories.AnyAsync(c => c.ParentId == id, ct))
        {
            throw new ValidationException("Category", $"{category.Name} has sub-categories. Delete or move those first.");
        }

        return await db.InTransactionAsync(async () =>
        {
            var level = category.Level;
            System.Linq.Expressions.Expression<Func<Ticket, bool>> uses = level switch
            {
                1 => t => t.CategoryId == id,
                2 => t => t.SubCategoryId == id,
                _ => t => t.ItemCategoryId == id,
            };
            var moved = 0;
            if (await db.Tickets.AnyAsync(uses, ct))
            {
                Guid?[] path;
                string? toName;
                if (replacementId is { } rid && rid != id)
                {
                    var replacement = await db.TicketCategories.FirstOrDefaultAsync(c => c.Id == rid && c.WorkspaceId == category.WorkspaceId, ct);
                    if (replacement is null || replacement.Level != level)
                    {
                        throw new ValidationException("ReplacementId", "Choose a replacement at the same level.");
                    }
                    path = await PathAsync(replacement, ct);
                    toName = replacement.Name;
                }
                else if (clear)
                {
                    path = [null, null, null];
                    toName = null;
                }
                else
                {
                    throw new ValidationException("ReplacementId", "Tickets use this category. Choose where they move, or leave them without it.");
                }

                moved = await TicketRemap.ApplyAsync(db, uses,
                    t =>
                    {
                        // The deleted category has no children, so levels below it are already empty.
                        switch (level)
                        {
                            case 1:
                                t.CategoryId = path[0];
                                t.SubCategoryId = null;
                                t.ItemCategoryId = null;
                                break;
                            case 2:
                                t.CategoryId = path[0] ?? t.CategoryId;
                                t.SubCategoryId = path[1];
                                t.ItemCategoryId = null;
                                break;
                            default:
                                t.CategoryId = path[0] ?? t.CategoryId;
                                t.SubCategoryId = path[1] ?? t.SubCategoryId;
                                t.ItemCategoryId = path[2];
                                break;
                        }
                        return [new FieldChange("Category", category.Name, toName)];
                    },
                    toName is null ? $"Removed category {category.Name}" : $"Moved from category {category.Name} to {toName}", ct);
            }
            db.TicketCategories.Remove(category);
            audit.Record("categories.deleted", "ticket_category", id.ToString(), new { name = category.Name, moved, replacementId, cleared = clear && replacementId is null });
            await db.SaveChangesAsync(ct);
            return moved;
        }, ct);
    }

    /// <summary>[level 1, level 2, level 3] ids for a category (unused levels null).</summary>
    private async Task<Guid?[]> PathAsync(TicketCategory category, CancellationToken ct)
    {
        var path = new Guid?[3];
        var current = category;
        while (true)
        {
            path[current.Level - 1] = current.Id;
            if (current.ParentId is not { } parent)
            {
                break;
            }
            current = await db.TicketCategories.FirstAsync(c => c.Id == parent, ct);
        }
        return path;
    }

    private async Task<string> CategoryNameAsync(Guid workspaceId, Guid? parentId, string? raw, Guid? exceptId, CancellationToken ct)
    {
        var name = (raw ?? "").Trim();
        if (name.Length is 0 or > 200)
        {
            throw new ValidationException("Name", name.Length == 0 ? "Name the category." : "Keep the name under 200 characters.");
        }
        if (await db.TicketCategories.AnyAsync(c => c.WorkspaceId == workspaceId && c.ParentId == parentId && c.Name == name && c.Id != exceptId, ct))
        {
            throw new ValidationException("Name", $"{name} already exists here.");
        }
        return name;
    }

    // ---------------------------------------------------------------- sources

    public async Task<IReadOnlyList<SourceRow>> ListSourcesAsync(CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.FieldsManage, ct: ct);
        var sources = await db.TicketSources.AsNoTracking().OrderBy(s => s.Order).ThenBy(s => s.Name).ToListAsync(ct);
        var counts = await db.Tickets.GroupBy(t => t.SourceId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return sources.Select(s => new SourceRow(s, counts.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<TicketSource> CreateSourceAsync(string name, string icon, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.FieldsManage, ct: ct);
        var clean = await SourceNameAsync(name, null, ct);
        var key = "custom_" + Slug().Replace(clean.ToLowerInvariant(), "_").Trim('_');
        if (key == "custom_" || key.Length > 64)
        {
            key = "custom_" + Guid.NewGuid().ToString("N")[..12];
        }
        var unique = key;
        for (var i = 2; await db.TicketSources.AnyAsync(s => s.Key == unique, ct); i++)
        {
            unique = $"{key}_{i}";
        }
        var source = new TicketSource
        {
            Key = unique,
            Name = clean,
            Icon = SourceIcons.Contains(icon) ? icon : SourceIcons[0],
            Order = (await db.TicketSources.MaxAsync(s => (int?)s.Order, ct) ?? 0) + 1,
        };
        db.TicketSources.Add(source);
        audit.Record("sources.created", "ticket_source", source.Id.ToString(), new { name = clean, key = unique });
        await db.SaveChangesAsync(ct);
        return source;
    }

    public async Task UpdateSourceAsync(Guid id, string name, string icon, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.FieldsManage, ct: ct);
        var source = await db.TicketSources.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException();
        if (source.IsSystem)
        {
            throw new ValidationException("Name", "Built-in sources can't be renamed.");
        }
        var clean = await SourceNameAsync(name, id, ct);
        var cleanIcon = SourceIcons.Contains(icon) ? icon : source.Icon;
        if (clean == source.Name && cleanIcon == source.Icon)
        {
            return;
        }
        source.Name = clean;
        source.Icon = cleanIcon;
        audit.Record("sources.updated", "ticket_source", id.ToString(), new { name = clean });
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteSourceAsync(Guid id, Guid? replacementId, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.FieldsManage, ct: ct);
        var source = await db.TicketSources.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException();
        if (source.IsSystem)
        {
            throw new ValidationException("Source", "Built-in sources can't be deleted.");
        }
        return await db.InTransactionAsync(async () =>
        {
            var moved = 0;
            if (await db.Tickets.AnyAsync(t => t.SourceId == id, ct))
            {
                var replacement = replacementId is { } rid && rid != id ? await db.TicketSources.FirstOrDefaultAsync(s => s.Id == rid, ct) : null;
                if (replacement is null)
                {
                    throw new ValidationException("ReplacementId", "Tickets came in through this source. Choose the source they show instead.");
                }
                moved = await TicketRemap.ApplyAsync(db, t => t.SourceId == id,
                    t =>
                    {
                        t.SourceId = replacement.Id;
                        return [new FieldChange("Source", source.Name, replacement.Name)];
                    },
                    $"Changed source to {replacement.Name} because {source.Name} was removed", ct);
            }
            db.TicketSources.Remove(source);
            audit.Record("sources.deleted", "ticket_source", id.ToString(), new { name = source.Name, moved, replacementId });
            await db.SaveChangesAsync(ct);
            return moved;
        }, ct);
    }

    private async Task<string> SourceNameAsync(string? raw, Guid? exceptId, CancellationToken ct)
    {
        var name = (raw ?? "").Trim();
        if (name.Length is 0 or > 100)
        {
            throw new ValidationException("Name", name.Length == 0 ? "Name the source." : "Keep the name under 100 characters.");
        }
        if (await db.TicketSources.AnyAsync(s => s.Name == name && s.Id != exceptId, ct))
        {
            throw new ValidationException("Name", $"A source called {name} already exists.");
        }
        return name;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Moves <paramref name="item"/> by <paramref name="delta"/> places and renumbers the list. False when it can't move.</summary>
    internal static bool Reorder<T>(List<T> ordered, T item, int delta, Action<T, int> setOrder) where T : class
    {
        var index = ordered.IndexOf(item);
        var target = Math.Clamp(index + Math.Sign(delta), 0, ordered.Count - 1);
        if (index < 0 || target == index)
        {
            return false;
        }
        ordered.RemoveAt(index);
        ordered.Insert(target, item);
        for (var i = 0; i < ordered.Count; i++)
        {
            setOrder(ordered[i], i + 1);
        }
        return true;
    }

    private static void Track<T>(List<string> changes, string name, T current, T next, Action<T> apply)
    {
        if (!EqualityComparer<T>.Default.Equals(current, next))
        {
            changes.Add(name);
            apply(next);
        }
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex Slug();
}
