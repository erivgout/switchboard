using System.Globalization;
using System.Text.Json.Nodes;
using Switchboard.Core.Agents;
using Switchboard.Core.Security;
using Switchboard.Modules.Approvals.Agents;
using Switchboard.Modules.Catalog.Domain;
using Switchboard.Modules.Catalog.Services;

namespace Switchboard.Modules.Catalog.Agents;

internal static class CatalogJson
{
    public static JsonObject Summary(CatalogItemSummary i) => new()
    {
        ["id"] = i.Id.ToString(),
        ["name"] = AgentContent.Untrusted(i.Name, "catalog.item.name"),
        ["summary"] = AgentContent.Untrusted(i.ShortDescription, "catalog.item.summary"),
        ["kind"] = i.Kind.ToString().ToLowerInvariant(),
        ["cost"] = Cost(i.CostMinor, i.Currency),
        ["delivery_days"] = i.DeliveryDays,
        ["needs_approval"] = i.NeedsApproval,
    };

    public static JsonObject? Cost(long? minor, string currency) => minor is { } m
        ? new JsonObject { ["amount_minor"] = m, ["currency"] = currency, ["display"] = CatalogForm.Money(m, currency) }
        : null;

    public static string TypeName(CatFieldType type) => type switch
    {
        CatFieldType.Text => "text",
        CatFieldType.Paragraph => "paragraph",
        CatFieldType.Number => "number",
        CatFieldType.Dropdown => "dropdown",
        CatFieldType.MultiSelect => "multi_select",
        CatFieldType.Checkbox => "checkbox",
        CatFieldType.Date => "date",
        _ => "user",
    };

    /// <summary>Accepts strings, numbers, booleans, arrays of those, or {"id": ...} for people.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Answers(JsonObject? fields)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (key, node) in fields ?? [])
        {
            result[key] = node switch
            {
                null => [],
                JsonArray array => array.Select(Text).OfType<string>().ToList(),
                _ => Text(node) is { } s ? [s] : [],
            };
        }
        return result;
    }

    private static string? Text(JsonNode? node) => node switch
    {
        JsonObject obj => obj["id"]?.GetValue<string>(),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<decimal>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };
}

internal sealed class SearchCatalogTool(ICatalogService catalog, IPermissionService permissions) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new(
        "search_catalog",
        "Search the service catalog",
        "Finds things the signed-in user can request from the service catalog (devices, software, access, services). " +
        "Pass query for a text search and/or category_id; with neither, returns the most requested items. Use get_catalog_item " +
        "for an item's form, then submit_request.",
        "catalog:read",
        AgentActionClass.Read,
        AgentContent.ObjectSchema([],
            ("query", AgentContent.StringSchema("Words to search item names and descriptions for.", 100)),
            ("category_id", AgentContent.StringSchema("Limit to one category (GUID).")),
            ("limit", AgentContent.IntegerSchema("Maximum items to return (1-100, default 20).", 1, 100))),
        RequesterAllowed: true);

    public Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct) =>
        AgentToolErrors.RunAsync(async () =>
        {
            var query = AgentToolErrors.String(arguments, "query");
            Guid? category = null;
            if (arguments["category_id"] is not null)
            {
                category = AgentToolErrors.TryGuid(arguments, "category_id", out var c) ? c : throw new ValidationException("category_id", "Give a valid category id.");
            }
            var limit = AgentToolErrors.Limit(arguments);
            // Requesters see only the items their groups and department may request; agents see every published item.
            var forAgent = (await permissions.CurrentAsync(ct))?.IsAgent == true;
            var items = string.IsNullOrWhiteSpace(query) && category is null
                ? await catalog.PopularAsync(forAgent, Math.Min(limit, 20), ct)
                : await catalog.BrowseAsync(query, category, forAgent, limit, ct);
            var categories = await catalog.CategoriesAsync(forAgent, ct);
            return AgentToolResult.Ok(new JsonObject
            {
                ["items"] = new JsonArray(items.Select(i => (JsonNode)CatalogJson.Summary(i)).ToArray()),
                ["categories"] = new JsonArray(categories.Where(c => c.ItemCount > 0).Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id.ToString(),
                    ["name"] = AgentContent.Untrusted(c.Name, "catalog.category.name"),
                }).ToArray()),
            }, $"{items.Count} catalog item(s).");
        });
}

internal sealed class GetCatalogItemTool(ICatalogService catalog, IPermissionService permissions) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new(
        "get_catalog_item",
        "Get a catalog item and its form",
        "Returns one catalog item with its request form: each field's key, label, type (text, paragraph, number, dropdown, " +
        "multi_select, checkbox, date, user), whether it's required, help and allowed options. Also the maximum quantity, " +
        "whether it can be requested for someone else, and the approval steps. Use the keys as the fields object of submit_request.",
        "catalog:read",
        AgentActionClass.Read,
        AgentContent.ObjectSchema(["item_id"], ("item_id", AgentContent.StringSchema("Catalog item id (GUID)."))),
        RequesterAllowed: true);

    public Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct) =>
        AgentToolErrors.RunAsync(async () =>
        {
            if (!AgentToolErrors.TryGuid(arguments, "item_id", out var id))
            {
                throw new ValidationException("item_id", "Give a valid item id.");
            }
            var forAgent = (await permissions.CurrentAsync(ct))?.IsAgent == true;
            var d = await catalog.GetAsync(id, forAgent, ct);
            var fields = new JsonArray(d.Fields.Select(f => (JsonNode)new JsonObject
            {
                ["key"] = f.Key,
                ["label"] = AgentContent.Untrusted(f.Label, "catalog.field.label"),
                ["type"] = CatalogJson.TypeName(f.Type),
                ["required"] = f.Required,
                ["help"] = f.HelpText is null ? null : AgentContent.Untrusted(f.HelpText, "catalog.field.help"),
                ["options"] = CatalogForm.UsesOptions(f.Type) ? new JsonArray(f.Options.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()) : null,
                ["value_format"] = f.Type switch
                {
                    CatFieldType.Date => "YYYY-MM-DD",
                    CatFieldType.UserLookup => "user id (GUID); find it with search_users",
                    CatFieldType.MultiSelect => "array of options",
                    CatFieldType.Checkbox => "true or false",
                    _ => null,
                },
            }).ToArray());
            var steps = await catalog.DescribeApprovalAsync(d.Approval, null, d.Fields, ct);
            return AgentToolResult.Ok(new JsonObject
            {
                ["id"] = d.Item.Id.ToString(),
                ["name"] = AgentContent.Untrusted(d.Item.Name, "catalog.item.name"),
                ["summary"] = AgentContent.Untrusted(d.Item.ShortDescription, "catalog.item.summary"),
                ["description"] = d.Item.Description is null ? null : AgentContent.Untrusted(d.Item.Description, "catalog.item.description"),
                ["kind"] = d.Item.Kind.ToString().ToLowerInvariant(),
                ["cost"] = CatalogJson.Cost(d.Item.CostMinor, d.Item.Currency),
                ["delivery_days"] = d.Item.DeliveryDays,
                ["max_quantity"] = d.Item.MaxQuantity,
                ["can_request_for_someone_else"] = d.Item.AllowRequestedFor,
                ["approval_steps"] = new JsonArray(steps.Select(s => (JsonNode)AgentContent.Untrusted(s, "catalog.approval")).ToArray()),
                ["bundle_items"] = new JsonArray(d.BundleItems.Select(b => (JsonNode)AgentContent.Untrusted(b.Name, "catalog.item.name")).ToArray()),
                ["fields"] = fields,
            }, $"Item with {d.Fields.Count} form field(s).");
        });
}

internal sealed class SubmitRequestTool(ICatalogRequestService requests) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new(
        "submit_request",
        "Request a catalog item",
        "Submits a service request for one catalog item, for the signed-in user. Call get_catalog_item first and pass answers in " +
        "fields, keyed by field key. Optional: quantity, requested_for (user id of someone else, if the item allows it). Agents may " +
        "pass requester_id to request on someone's behalf. Creates a service request ticket and starts approvals when the item needs " +
        "them. Returns the ticket and the approval status.",
        "catalog:request",
        AgentActionClass.InternalWrite,
        AgentContent.ObjectSchema(["item_id"],
            ("item_id", AgentContent.StringSchema("Catalog item id (GUID).")),
            ("quantity", AgentContent.IntegerSchema("How many (default 1).", 1, 100)),
            ("fields", new JsonObject { ["type"] = "object", ["description"] = "Answers keyed by field key.", ["additionalProperties"] = true }),
            ("requested_for", AgentContent.StringSchema("User id of the person the item is for, when not the requester.")),
            ("requester_id", AgentContent.StringSchema("Agents only: user id of the person requesting."))),
        RequesterAllowed: true);

    public Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct) =>
        AgentToolErrors.RunAsync(async () =>
        {
            if (!AgentToolErrors.TryGuid(arguments, "item_id", out var itemId))
            {
                throw new ValidationException("item_id", "Give a valid item id.");
            }
            var quantity = arguments["quantity"] is JsonValue q && q.TryGetValue<int>(out var n) ? n : 1;
            Guid? Optional(string name) => arguments[name] is null ? null
                : AgentToolErrors.TryGuid(arguments, name, out var g) ? g : throw new ValidationException(name, "Give a valid user id.");
            if (arguments["fields"] is not null and not JsonObject)
            {
                throw new ValidationException("fields", "Pass fields as an object keyed by field key.");
            }
            var command = new SubmitRequestCommand
            {
                ItemId = itemId,
                Quantity = quantity,
                Answers = CatalogJson.Answers(arguments["fields"] as JsonObject),
                RequestedForId = Optional("requested_for"),
                RequesterId = Optional("requester_id"),
                StrictAnswers = true,
            };
            if (context.DryRun)
            {
                return AgentToolResult.Ok(new JsonObject { ["dry_run"] = true, ["item_id"] = itemId.ToString(), ["quantity"] = quantity }, "Dry run: nothing was submitted.");
            }
            var result = await requests.SubmitAsync(command, ct);
            return AgentToolResult.Ok(new JsonObject
            {
                ["ticket_id"] = result.Ticket.Id.ToString(),
                ["display_id"] = result.Ticket.DisplayId,
                ["requested_item_id"] = result.Item.Id.ToString(),
                ["approval_id"] = result.Approval?.Id.ToString(),
                ["approval_status"] = result.Approval is null ? "not_required" : result.Approval.Status.ToString().ToLowerInvariant(),
                ["message"] = result.PostSubmitMessage is null ? null : AgentContent.Untrusted(result.PostSubmitMessage, "catalog.item.post_submit_message"),
            }, result.Approval is null ? $"Submitted {result.Ticket.DisplayId}." : $"Submitted {result.Ticket.DisplayId}; it's waiting for approval.");
        });
}
