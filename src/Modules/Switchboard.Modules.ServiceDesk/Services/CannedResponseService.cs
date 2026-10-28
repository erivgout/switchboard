using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.ServiceDesk.Data;

namespace Switchboard.Modules.ServiceDesk.Services;

public sealed record CannedResponseInput(Guid? FolderId, string Title, string BodyHtml);

public sealed record CannedFolderInput(string Name, CannedVisibility Visibility, Guid? GroupId);

/// <summary>A response the current user can insert, with its folder for grouping.</summary>
public sealed record CannedResponseItem(Guid Id, string Title, string Preview, Guid FolderId, string FolderName, CannedVisibility Visibility);

/// <summary>
/// Canned responses (FR-AUT-7). Folders carry the visibility: personal (owner only), shared (all agents)
/// or per group (members and observers). Shared and group folders need <c>settings.manage</c> to change.
/// </summary>
public sealed partial class CannedResponseService(
    SwitchboardDbContext db,
    IActorAccessor actors,
    IPermissionService permissions,
    ITicketService tickets,
    IHtmlContent html,
    IAuditLog audit)
{
    public const string PersonalFolderName = "My responses";

    /// <summary>Placeholders agents can use, with what they become.</summary>
    public static IReadOnlyList<(string Token, string Meaning)> Placeholders { get; } =
    [
        ("{{requester.name}}", "Requester's full name"),
        ("{{requester.first_name}}", "Requester's first name"),
        ("{{requester.email}}", "Requester's email address"),
        ("{{ticket.id}}", "Ticket ID, like INC-1042"),
        ("{{ticket.subject}}", "Ticket subject"),
        ("{{ticket.status}}", "Current status"),
        ("{{ticket.priority}}", "Current priority"),
        ("{{group.name}}", "Assigned group"),
        ("{{agent.name}}", "Your full name"),
        ("{{agent.first_name}}", "Your first name"),
        ("{{agent.email}}", "Your email address"),
    ];

    // ---------------------------------------------------------------- reading

    /// <summary>Folders the current user can see.</summary>
    public async Task<IReadOnlyList<SdCannedResponseFolder>> VisibleFoldersAsync(CancellationToken ct = default)
    {
        var query = await VisibleFolderQueryAsync(ct);
        return await query.AsNoTracking().OrderBy(f => f.Visibility).ThenBy(f => f.Name).ToListAsync(ct);
    }

    /// <summary>Folders the current user can add responses to.</summary>
    public async Task<IReadOnlyList<SdCannedResponseFolder>> EditableFoldersAsync(CancellationToken ct = default)
    {
        var folders = await VisibleFoldersAsync(ct);
        var manage = await CanManageSharedAsync(ct);
        var me = actors.Current.UserId;
        return folders.Where(f => f.Visibility == CannedVisibility.Personal ? f.OwnerId == me : manage).ToList();
    }

    public async Task<IReadOnlyList<CannedResponseItem>> SearchAsync(string? q, int take = 50, CancellationToken ct = default)
    {
        var folders = await VisibleFolderQueryAsync(ct);
        var query = db.Set<SdCannedResponse>().AsNoTracking().Where(r => folders.Any(f => f.Id == r.FolderId));
        var term = (q ?? "").Trim();
        if (term.Length > 0)
        {
            query = query.Where(r => r.Title.Contains(term) || r.BodyHtml.Contains(term));
        }
        var rows = await query
            .OrderBy(r => r.Title)
            .Take(Math.Clamp(take, 1, 200))
            .Select(r => new { r.Id, r.Title, r.BodyHtml, r.FolderId, FolderName = r.Folder!.Name, r.Folder.Visibility })
            .ToListAsync(ct);
        return rows.Select(r => new CannedResponseItem(r.Id, r.Title, Preview(r.BodyHtml), r.FolderId, r.FolderName, r.Visibility)).ToList();
    }

    public async Task<SdCannedResponse> GetAsync(Guid id, CancellationToken ct = default)
    {
        var folders = await VisibleFolderQueryAsync(ct);
        return await db.Set<SdCannedResponse>().AsNoTracking().Include(r => r.Folder)
            .FirstOrDefaultAsync(r => r.Id == id && folders.Any(f => f.Id == r.FolderId), ct)
            ?? throw new NotFoundException();
    }

    /// <summary>The response body with placeholders filled in for a ticket. Values are HTML-encoded.</summary>
    public async Task<string> RenderAsync(Guid responseId, Guid ticketId, CancellationToken ct = default)
    {
        var response = await GetAsync(responseId, ct);
        var ticket = await tickets.GetForAgentAsync(ticketId, ct);
        var requester = await db.Users.AsNoTracking().Where(u => u.Id == ticket.RequesterId)
            .Select(u => new { u.DisplayName, u.Email, u.IsForgotten }).FirstOrDefaultAsync(ct);
        var agent = actors.Current.UserId is { } me
            ? await db.Users.AsNoTracking().Where(u => u.Id == me).Select(u => new { u.DisplayName, u.Email }).FirstOrDefaultAsync(ct)
            : null;
        var group = ticket.GroupId is { } g ? await db.AgentGroups.AsNoTracking().Where(x => x.Id == g).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["requester.name"] = requester?.DisplayName,
            ["requester.first_name"] = FirstName(requester?.DisplayName),
            ["requester.email"] = requester is { IsForgotten: false } ? requester.Email : null,
            ["ticket.id"] = ticket.DisplayId,
            ["ticket.subject"] = ticket.Subject,
            ["ticket.status"] = ticket.Status?.Name,
            ["ticket.priority"] = ticket.Priority.ToString(),
            ["group.name"] = group,
            ["agent.name"] = agent?.DisplayName,
            ["agent.first_name"] = FirstName(agent?.DisplayName),
            ["agent.email"] = agent?.Email,
        };
        return html.Sanitize(FillPlaceholders(response.BodyHtml, values));
    }

    /// <summary>
    /// Replaces <c>{{key}}</c> tokens with HTML-encoded values. Unknown tokens stay as typed so the agent
    /// notices them; known tokens without a value become empty.
    /// </summary>
    public static string FillPlaceholders(string bodyHtml, IReadOnlyDictionary<string, string?> values) =>
        PlaceholderPattern().Replace(bodyHtml ?? "", m =>
            values.TryGetValue(m.Groups["key"].Value, out var value) ? WebUtility.HtmlEncode(value ?? "") : m.Value);

    // ---------------------------------------------------------------- writing

    public async Task<SdCannedResponse> CreateAsync(CannedResponseInput input, CancellationToken ct = default)
    {
        var folder = input.FolderId is { } fid ? await LoadFolderForWriteAsync(fid, ct) : await EnsurePersonalFolderAsync(ct);
        var (title, body) = Validate(input);
        var response = new SdCannedResponse { FolderId = folder.Id, Title = title, BodyHtml = body, CreatedById = actors.Current.UserId };
        db.Set<SdCannedResponse>().Add(response);
        AuditShared(folder, "servicedesk.canned_created", response);
        await db.SaveChangesAsync(ct);
        return response;
    }

    public async Task<SdCannedResponse> UpdateAsync(Guid id, CannedResponseInput input, CancellationToken ct = default)
    {
        var response = await db.Set<SdCannedResponse>().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException();
        await LoadFolderForWriteAsync(response.FolderId, ct);
        var target = input.FolderId is { } fid && fid != response.FolderId ? await LoadFolderForWriteAsync(fid, ct) : null;
        var (title, body) = Validate(input);
        response.Title = title;
        response.BodyHtml = body;
        if (target is not null)
        {
            response.FolderId = target.Id;
        }
        var folder = await db.Set<SdCannedResponseFolder>().AsNoTracking().FirstAsync(f => f.Id == response.FolderId, ct);
        AuditShared(folder, "servicedesk.canned_updated", response);
        await db.SaveChangesAsync(ct);
        return response;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var response = await db.Set<SdCannedResponse>().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException();
        var folder = await LoadFolderForWriteAsync(response.FolderId, ct);
        db.Set<SdCannedResponse>().Remove(response);
        AuditShared(folder, "servicedesk.canned_deleted", response);
        await db.SaveChangesAsync(ct);
    }

    public async Task<SdCannedResponseFolder> CreateFolderAsync(CannedFolderInput input, CancellationToken ct = default)
    {
        var me = RequireUser();
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 100)
        {
            throw new ValidationException("name", name.Length == 0 ? "Name the folder." : "Keep the folder name under 100 characters.");
        }
        var folder = new SdCannedResponseFolder { Name = name, Visibility = input.Visibility };
        switch (input.Visibility)
        {
            case CannedVisibility.Personal:
                folder.OwnerId = me;
                break;
            case CannedVisibility.Shared:
                await permissions.DemandAsync(Privileges.SettingsManage, ct: ct);
                break;
            case CannedVisibility.Group:
                await permissions.DemandAsync(Privileges.SettingsManage, ct: ct);
                folder.GroupId = input.GroupId is { } g && await db.AgentGroups.AnyAsync(x => x.Id == g, ct)
                    ? g
                    : throw new ValidationException("groupId", "Choose the group that can use this folder.");
                break;
            default:
                throw new ValidationException("visibility", "Choose who can use the folder.");
        }
        db.Set<SdCannedResponseFolder>().Add(folder);
        if (folder.Visibility != CannedVisibility.Personal)
        {
            audit.Record("servicedesk.canned_folder_created", "canned_folder", folder.Id.ToString(), new { folder.Name, folder.Visibility, folder.GroupId });
        }
        await db.SaveChangesAsync(ct);
        return folder;
    }

    public async Task DeleteFolderAsync(Guid id, CancellationToken ct = default)
    {
        var folder = await LoadFolderForWriteAsync(id, ct);
        if (await db.Set<SdCannedResponse>().AnyAsync(r => r.FolderId == id, ct))
        {
            throw new ValidationException("folder", "Move or delete the responses in this folder first.");
        }
        db.Set<SdCannedResponseFolder>().Remove(folder);
        if (folder.Visibility != CannedVisibility.Personal)
        {
            audit.Record("servicedesk.canned_folder_deleted", "canned_folder", folder.Id.ToString(), new { folder.Name });
        }
        await db.SaveChangesAsync(ct);
    }

    public Task<bool> CanManageSharedAsync(CancellationToken ct = default) => permissions.HasAsync(Privileges.SettingsManage, ct: ct);

    /// <summary>True when the current user may change responses in the folder.</summary>
    public async Task<bool> CanEditFolderAsync(SdCannedResponseFolder folder, CancellationToken ct = default) =>
        folder.Visibility == CannedVisibility.Personal ? folder.OwnerId == actors.Current.UserId : await CanManageSharedAsync(ct);

    // ---------------------------------------------------------------- helpers

    private async Task<IQueryable<SdCannedResponseFolder>> VisibleFolderQueryAsync(CancellationToken ct)
    {
        var me = RequireUser();
        var p = await permissions.CurrentAsync(ct) ?? throw new ForbiddenException();
        if (!p.IsAgent)
        {
            throw new ForbiddenException();
        }
        var groups = p.GroupIds.Concat(p.ObserverGroupIds).ToList();
        var all = p.IsOrgAdmin || p.Has(Privileges.SettingsManage);
        return db.Set<SdCannedResponseFolder>().Where(f =>
            (f.Visibility == CannedVisibility.Personal && f.OwnerId == me)
            || f.Visibility == CannedVisibility.Shared
            || (f.Visibility == CannedVisibility.Group && (all || (f.GroupId != null && groups.Contains(f.GroupId.Value)))));
    }

    private async Task<SdCannedResponseFolder> LoadFolderForWriteAsync(Guid id, CancellationToken ct)
    {
        var visible = await VisibleFolderQueryAsync(ct);
        var folder = await visible.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException();
        if (!await CanEditFolderAsync(folder, ct))
        {
            throw new ForbiddenException("Only an admin can change shared and group responses.");
        }
        return folder;
    }

    private async Task<SdCannedResponseFolder> EnsurePersonalFolderAsync(CancellationToken ct)
    {
        var me = RequireUser();
        var folder = await db.Set<SdCannedResponseFolder>()
            .FirstOrDefaultAsync(f => f.Visibility == CannedVisibility.Personal && f.OwnerId == me, ct);
        if (folder is null)
        {
            folder = new SdCannedResponseFolder { Name = PersonalFolderName, Visibility = CannedVisibility.Personal, OwnerId = me };
            db.Set<SdCannedResponseFolder>().Add(folder);
        }
        return folder;
    }

    private (string Title, string Body) Validate(CannedResponseInput input)
    {
        var title = (input.Title ?? "").Trim();
        if (title.Length is 0 or > 200)
        {
            throw new ValidationException("title", title.Length == 0 ? "Give the response a title." : "Keep the title under 200 characters.");
        }
        if ((input.BodyHtml ?? "").Length > 100_000)
        {
            throw new ValidationException("body", "The response is too long.");
        }
        var body = html.Sanitize(input.BodyHtml);
        if (html.ToPlainText(body).Length == 0)
        {
            throw new ValidationException("body", "Write the response text.");
        }
        return (title, body);
    }

    private void AuditShared(SdCannedResponseFolder folder, string action, SdCannedResponse response)
    {
        if (folder.Visibility != CannedVisibility.Personal)
        {
            audit.Record(action, "canned_response", response.Id.ToString(), new { response.Title, folder = folder.Name });
        }
    }

    private string Preview(string bodyHtml)
    {
        var text = html.ToPlainText(bodyHtml).ReplaceLineEndings(" ");
        return text.Length <= 140 ? text : text[..140].TrimEnd() + "…";
    }

    private static string? FirstName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim().Split(' ', 2)[0];

    private Guid RequireUser() =>
        actors.Current.UserId is { } id && !actors.Current.IsInternal ? id : throw new ForbiddenException();

    [GeneratedRegex(@"\{\{\s*(?<key>[a-z_]+\.[a-z_]+)\s*\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderPattern();
}
