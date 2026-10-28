using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Catalog.Services;

namespace Switchboard.Modules.Catalog.Web;

/// <summary>Shared behavior of the browse pages (<c>/portal/catalog</c>, <c>/catalog</c>).</summary>
public abstract class CatalogBrowsePageBase(ICatalogService catalog) : PageModel
{
    protected abstract bool ForAgent { get; }
    protected abstract string BasePath { get; }

    public DirectoryModel Directory { get; private set; } = default!;

    public virtual async Task OnGetAsync(string? q, Guid? category)
    {
        var ct = HttpContext.RequestAborted;
        var categories = await catalog.CategoriesAsync(ForAgent, ct);
        var items = await catalog.BrowseAsync(q, category, ForAgent, ct: ct);
        var popular = string.IsNullOrWhiteSpace(q) && category is null ? await catalog.PopularAsync(ForAgent, 4, ct) : [];
        Directory = new DirectoryModel(BasePath, q?.Trim(), category, categories, items, popular.Count >= 2 ? popular : [], ForAgent);
    }
}

/// <summary>Shared behavior of the item request pages (<c>/portal/catalog/{id}</c>, <c>/catalog/{id}</c>).</summary>
public abstract class CatalogItemPageBase(ICatalogService catalog, ICatalogRequestService requests, SwitchboardDbContext db, IActorAccessor actors) : PageModel
{
    protected abstract bool ForAgent { get; }

    public CatalogItemDetails Details { get; private set; } = default!;
    public IReadOnlyList<string> Steps { get; private set; } = [];
    public FormRenderModel Form { get; private set; } = default!;
    public int Quantity { get; private set; } = 1;
    public Guid? RequesterId { get; private set; }
    public Guid? RequestedForId { get; private set; }
    public IReadOnlyDictionary<Guid, string> People { get; private set; } = new Dictionary<Guid, string>();

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        await LoadAsync(id, new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, string>());
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, int? quantity, Guid? requestedFor, Guid? requester)
    {
        var answers = CatalogFormBinding.Read(Request.Form);
        Quantity = quantity ?? 1;
        RequestedForId = requestedFor;
        RequesterId = requester;
        if (ForAgent && requester is null)
        {
            ModelState.AddModelError("requester", "Choose who is asking for this.");
            await LoadAsync(id, answers, new Dictionary<string, string>());
            return Page();
        }
        try
        {
            var result = await requests.SubmitAsync(new SubmitRequestCommand
            {
                ItemId = id,
                Quantity = Quantity,
                Answers = answers,
                RequestedForId = requestedFor,
                RequesterId = ForAgent ? requester : null,
            }, HttpContext.RequestAborted);
            return Submitted(result);
        }
        catch (ValidationException ex)
        {
            var fieldErrors = CatalogFormBinding.Errors(ex, ModelState);
            await LoadAsync(id, answers, fieldErrors);
            return Page();
        }
    }

    protected abstract IActionResult Submitted(SubmitResult result);

    private async Task LoadAsync(Guid id, IReadOnlyDictionary<string, IReadOnlyList<string>> answers, IReadOnlyDictionary<string, string> errors)
    {
        var ct = HttpContext.RequestAborted;
        Details = await catalog.GetAsync(id, ForAgent, ct);
        var person = ForAgent ? RequestedForId ?? RequesterId : RequestedForId ?? actors.Current.UserId;
        Steps = await catalog.DescribeApprovalAsync(Details.Approval, person, Details.Fields, ct);
        var ids = CatalogForm.LookupIds(Details.Fields, answers).Concat(new[] { RequesterId, RequestedForId }.OfType<Guid>()).Distinct().ToList();
        People = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        Form = new FormRenderModel(Details.Fields, answers, errors, People);
    }
}
