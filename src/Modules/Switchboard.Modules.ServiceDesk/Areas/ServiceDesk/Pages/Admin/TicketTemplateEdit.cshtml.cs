using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.ServiceDesk.Services;
using Switchboard.UI;

namespace Switchboard.Modules.ServiceDesk.Areas.ServiceDesk.Pages.Admin;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.SettingsManage)]
public sealed class TicketTemplateEditModel(TicketTemplateService templates, TicketFormOptions formOptions, SwitchboardDbContext db, IHtmlContent html) : PageModel
{
    public sealed class TemplateForm
    {
        public Guid WorkspaceId { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public TicketType Type { get; set; } = TicketType.Incident;
        public string Subject { get; set; } = "";
        public string? DescriptionHtml { get; set; }
        public Guid? CategoryId { get; set; }
        public Guid? SubCategoryId { get; set; }
        public Guid? ItemCategoryId { get; set; }
        public Priority? Priority { get; set; }
        public Level? Urgency { get; set; }
        public Level? Impact { get; set; }
        public Guid? GroupId { get; set; }
        public string? Tags { get; set; }
        public bool IsActive { get; set; } = true;
    }

    [BindProperty] public TemplateForm Form { get; set; } = new();

    public Guid? Id { get; private set; }
    public WorkspaceOptions Options { get; private set; } = default!;
    public IReadOnlyList<NamedOption> Workspaces { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string key, Guid? ws, CancellationToken ct)
    {
        var id = ParseKey(key);
        Id = id;
        if (id is { } existing)
        {
            var t = await templates.GetForManageAsync(existing, ct);
            Form = new TemplateForm
            {
                WorkspaceId = t.WorkspaceId,
                Name = t.Name,
                Description = t.Description,
                Type = t.Type,
                Subject = t.Subject,
                DescriptionHtml = t.DescriptionHtml,
                CategoryId = t.CategoryId,
                SubCategoryId = t.SubCategoryId,
                ItemCategoryId = t.ItemCategoryId,
                Priority = t.Priority,
                Urgency = t.Urgency,
                Impact = t.Impact,
                GroupId = t.GroupId,
                Tags = string.Join(", ", t.Tags),
                IsActive = t.IsActive,
            };
        }
        await LoadAsync(ws, ct);
        ViewData["Title"] = id is null ? "New ticket template" : $"Edit {Form.Name}";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string key, CancellationToken ct)
    {
        var id = ParseKey(key);
        Id = id;
        try
        {
            await templates.SaveAsync(id, new TicketTemplateInput
            {
                WorkspaceId = Form.WorkspaceId,
                Name = Form.Name,
                Description = Form.Description,
                Type = Form.Type,
                Subject = Form.Subject,
                DescriptionHtml = Form.DescriptionHtml,
                CategoryId = Form.CategoryId,
                SubCategoryId = Form.SubCategoryId,
                ItemCategoryId = Form.ItemCategoryId,
                Priority = Form.Priority,
                Urgency = Form.Urgency,
                Impact = Form.Impact,
                GroupId = Form.GroupId,
                Tags = (Form.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                IsActive = Form.IsActive,
            }, ct);
            TempData.Toast(id is null ? "Template created." : "Template saved.");
            return Redirect("/admin/ticket-templates");
        }
        catch (ValidationException ex)
        {
            foreach (var (field, messages) in ex.Errors)
            {
                foreach (var m in messages)
                {
                    ModelState.AddModelError($"Form.{field}", m);
                }
            }
        }
        Form.DescriptionHtml = html.Sanitize(Form.DescriptionHtml);
        await LoadAsync(Form.WorkspaceId, ct);
        return Page();
    }

    private static Guid? ParseKey(string key) =>
        key == "new" ? null : Guid.TryParse(key, out var id) ? id : throw new NotFoundException();

    private async Task LoadAsync(Guid? ws, CancellationToken ct)
    {
        var managed = await templates.ManagedWorkspacesAsync(ct);
        Workspaces = await db.Workspaces.AsNoTracking().Where(w => managed.Contains(w.Id)).OrderByDescending(w => w.IsPrimary).ThenBy(w => w.Name)
            .Select(w => new NamedOption(w.Id, w.Name)).ToListAsync(ct);
        if (Workspaces.Count == 0)
        {
            throw new ForbiddenException();
        }
        if (!Workspaces.Any(w => w.Id == Form.WorkspaceId))
        {
            Form.WorkspaceId = ws is { } w && Workspaces.Any(x => x.Id == w) ? w : Workspaces[0].Id;
        }
        Options = await formOptions.LoadAsync(Form.WorkspaceId, ct);
    }
}
