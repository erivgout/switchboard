using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages;

[Authorize(Policy = "priv:" + KbPrivileges.Manage)]
public sealed class AnnouncementEditModel(IKbAnnouncementService announcements, IKnowledgeVisibility visibility, SwitchboardDbContext db, TimeProvider time) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }

    [BindProperty] public string Title { get; set; } = "";
    [BindProperty] public string? BodyHtml { get; set; }
    [BindProperty] public KbAnnouncementAudience Audience { get; set; } = KbAnnouncementAudience.Everyone;
    [BindProperty] public List<Guid> AgentGroupIds { get; set; } = [];
    [BindProperty] public List<Guid> RequesterGroupIds { get; set; } = [];
    [BindProperty] public List<Guid> DepartmentIds { get; set; } = [];
    /// <summary>Local date and time from the browser; <see cref="TzOffset"/> converts it to UTC.</summary>
    [BindProperty] public DateTime? ActiveFrom { get; set; }
    [BindProperty] public DateTime? ActiveTo { get; set; }
    /// <summary>Minutes behind UTC, from <c>Date.getTimezoneOffset()</c>.</summary>
    [BindProperty] public int TzOffset { get; set; }
    [BindProperty] public Guid WorkspaceId { get; set; }

    public DateTimeOffset? ActiveFromUtc { get; private set; }
    public DateTimeOffset? ActiveToUtc { get; private set; }
    public bool IsPublished { get; private set; }
    public IReadOnlyList<KbOption> Workspaces { get; private set; } = [];
    public IReadOnlyList<KbOption> AgentGroups { get; private set; } = [];
    public IReadOnlyList<KbOption> RequesterGroups { get; private set; } = [];
    public IReadOnlyList<KbOption> Departments { get; private set; } = [];
    public bool IsNew => Id is null;

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (Id is { } id)
        {
            var a = await announcements.GetForEditAsync(id, ct);
            Title = a.Title;
            BodyHtml = a.BodyHtml;
            Audience = a.Audience;
            AgentGroupIds = a.Targets.Where(t => t.Kind == KbAudienceKind.AgentGroup).Select(t => t.TargetId).ToList();
            RequesterGroupIds = a.Targets.Where(t => t.Kind == KbAudienceKind.RequesterGroup).Select(t => t.TargetId).ToList();
            DepartmentIds = a.Targets.Where(t => t.Kind == KbAudienceKind.Department).Select(t => t.TargetId).ToList();
            ActiveFromUtc = a.ActiveFrom;
            ActiveToUtc = a.ActiveTo;
            IsPublished = a.IsPublished;
            WorkspaceId = a.WorkspaceId;
        }
        else
        {
            ActiveFromUtc = time.GetUtcNow();
            WorkspaceId = Workspaces.FirstOrDefault()?.Id ?? Guid.Empty;
        }
    }

    public async Task<IActionResult> OnPostAsync(string? intent, CancellationToken ct)
    {
        var offset = TimeSpan.FromMinutes(Math.Clamp(TzOffset, -14 * 60, 14 * 60));
        DateTimeOffset? ToUtc(DateTime? local) => local is { } l ? new DateTimeOffset(DateTime.SpecifyKind(l, DateTimeKind.Unspecified) + offset, TimeSpan.Zero) : null;
        var publish = intent != "draft";
        try
        {
            await announcements.SaveAsync(Id, new KbAnnouncementInput
            {
                WorkspaceId = WorkspaceId,
                Title = Title,
                BodyHtml = BodyHtml,
                Audience = Audience,
                AgentGroupIds = AgentGroupIds,
                RequesterGroupIds = RequesterGroupIds,
                DepartmentIds = DepartmentIds,
                ActiveFrom = ToUtc(ActiveFrom) ?? time.GetUtcNow(),
                ActiveTo = ToUtc(ActiveTo),
                IsPublished = publish,
            }, ct);
            TempData.Toast(publish ? "Announcement posted" : "Draft saved");
            return Redirect("/kb/announcements");
        }
        catch (ValidationException ex)
        {
            this.AddErrors(ex);
            ActiveFromUtc = ToUtc(ActiveFrom);
            ActiveToUtc = ToUtc(ActiveTo);
            IsPublished = publish;
            await LoadAsync(ct);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var all = await db.Workspaces.AsNoTracking().Where(w => !w.IsArchived).OrderByDescending(w => w.IsPrimary).ThenBy(w => w.Name)
            .Select(w => new KbOption(w.Id, w.Name)).ToListAsync(ct);
        Workspaces = all.Where(w => viewer.CanManage(w.Id)).ToList();
        AgentGroups = await db.AgentGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => new KbOption(g.Id, g.Name)).ToListAsync(ct);
        RequesterGroups = await db.RequesterGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => new KbOption(g.Id, g.Name)).ToListAsync(ct);
        Departments = await db.Departments.AsNoTracking().OrderBy(d => d.Name).Select(d => new KbOption(d.Id, d.Name)).ToListAsync(ct);
    }
}
