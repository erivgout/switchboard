using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.ServiceDesk.Data;
using Switchboard.Modules.ServiceDesk.Services;
using Switchboard.UI;

namespace Switchboard.Modules.ServiceDesk.Areas.ServiceDesk.Pages.Tickets;

/// <summary>Manage canned responses (FR-AUT-7): personal ones for every agent, shared and group ones for settings managers.</summary>
[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class CannedResponsesModel(CannedResponseService canned, SwitchboardDbContext db, IHtmlContent html) : PageModel
{
    public sealed class ResponseInput
    {
        public Guid? Id { get; set; }
        public Guid? FolderId { get; set; }
        public string Title { get; set; } = "";
        public string? BodyHtml { get; set; }
    }

    public sealed class FolderInput
    {
        public string Name { get; set; } = "";
        public CannedVisibility Visibility { get; set; } = CannedVisibility.Personal;
        public Guid? GroupId { get; set; }
    }

    [BindProperty] public ResponseInput Input { get; set; } = new();
    [BindProperty] public FolderInput Folder { get; set; } = new();

    public IReadOnlyList<SdCannedResponseFolder> Folders { get; private set; } = [];
    public IReadOnlyList<SdCannedResponseFolder> EditableFolders { get; private set; } = [];
    public IReadOnlyList<CannedResponseItem> Responses { get; private set; } = [];
    public HashSet<Guid> EditableFolderIds { get; private set; } = [];
    public IReadOnlyDictionary<Guid, string> GroupNames { get; private set; } = new Dictionary<Guid, string>();
    public bool CanManageShared { get; private set; }
    public Guid? SelectedFolder { get; private set; }
    public bool Editing { get; private set; }
    public string? Q { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid? folder, Guid? edit, bool? @new, string? q, CancellationToken ct)
    {
        SelectedFolder = folder;
        Q = q;
        if (edit is { } id)
        {
            var response = await canned.GetAsync(id, ct);
            if (!await canned.CanEditFolderAsync(response.Folder!, ct))
            {
                TempData.Toast("Only an admin can change shared and group responses.", ToastKind.Error);
                return Redirect("/tickets/canned-responses");
            }
            Input = new ResponseInput { Id = response.Id, FolderId = response.FolderId, Title = response.Title, BodyHtml = response.BodyHtml };
            Editing = true;
        }
        else if (@new == true)
        {
            Input = new ResponseInput { FolderId = folder };
            Editing = true;
        }
        await LoadAsync(ct);
        ViewData["Title"] = "Canned responses";
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        try
        {
            var data = new CannedResponseInput(Input.FolderId, Input.Title, Input.BodyHtml ?? "");
            var saved = Input.Id is { } id ? await canned.UpdateAsync(id, data, ct) : await canned.CreateAsync(data, ct);
            TempData.Toast(Input.Id is null ? "Response saved." : "Response updated.");
            return Redirect($"/tickets/canned-responses?folder={saved.FolderId}");
        }
        catch (ValidationException ex)
        {
            foreach (var (key, messages) in ex.Errors)
            {
                foreach (var m in messages)
                {
                    ModelState.AddModelError(key == "title" ? "Input.Title" : "Input.BodyHtml", m);
                }
            }
        }
        catch (ForbiddenException ex)
        {
            ModelState.AddModelError("", ex.Message);
        }
        Input.BodyHtml = html.Sanitize(Input.BodyHtml);
        Editing = true;
        SelectedFolder = Input.FolderId;
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await canned.DeleteAsync(id, ct);
            TempData.Toast("Response deleted.");
        }
        catch (Exception ex) when (ex is ForbiddenException or NotFoundException)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return Redirect("/tickets/canned-responses");
    }

    public async Task<IActionResult> OnPostCreateFolderAsync(CancellationToken ct)
    {
        try
        {
            var folder = await canned.CreateFolderAsync(new CannedFolderInput(Folder.Name, Folder.Visibility, Folder.GroupId), ct);
            TempData.Toast($"Folder \"{folder.Name}\" created.");
            return Redirect($"/tickets/canned-responses?folder={folder.Id}");
        }
        catch (Exception ex) when (ex is ValidationException or ForbiddenException)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
            return Redirect("/tickets/canned-responses");
        }
    }

    public async Task<IActionResult> OnPostDeleteFolderAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await canned.DeleteFolderAsync(id, ct);
            TempData.Toast("Folder deleted.");
        }
        catch (Exception ex) when (ex is ValidationException or ForbiddenException or NotFoundException)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return Redirect("/tickets/canned-responses");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Folders = await canned.VisibleFoldersAsync(ct);
        EditableFolders = await canned.EditableFoldersAsync(ct);
        EditableFolderIds = EditableFolders.Select(f => f.Id).ToHashSet();
        CanManageShared = await canned.CanManageSharedAsync(ct);
        var all = await canned.SearchAsync(Q, 200, ct);
        Responses = SelectedFolder is { } f ? all.Where(r => r.FolderId == f).ToList() : all;
        var groupIds = Folders.Where(x => x.GroupId != null).Select(x => x.GroupId!.Value).ToList();
        GroupNames = CanManageShared
            ? await db.AgentGroups.AsNoTracking().OrderBy(g => g.Name).ToDictionaryAsync(g => g.Id, g => g.Name, ct)
            : await db.AgentGroups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
    }

    public string VisibilityText(SdCannedResponseFolder folder) => folder.Visibility switch
    {
        CannedVisibility.Personal => "Only you",
        CannedVisibility.Shared => "Every agent",
        _ => folder.GroupId is { } g && GroupNames.TryGetValue(g, out var name) ? $"{name} group" : "One group",
    };
}
