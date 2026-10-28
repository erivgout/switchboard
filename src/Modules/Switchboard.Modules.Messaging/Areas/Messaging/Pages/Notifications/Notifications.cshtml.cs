using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Messaging.Notifications;
using Switchboard.UI;

namespace Switchboard.Modules.Messaging.Pages;

public static class NotificationIcons
{
    public static string For(string type) => type switch
    {
        NotificationCatalog.AgentAssigned => "bi-person-check",
        NotificationCatalog.AgentGroupAssigned => "bi-people",
        NotificationCatalog.AgentNewInGroup => "bi-inbox",
        NotificationCatalog.AgentRequesterReplied => "bi-reply",
        NotificationCatalog.AgentMentioned => "bi-at",
        _ => "bi-bell",
    };

    /// <summary>Tells the bell its new count after an htmx action.</summary>
    public static void SetBellCount(HttpResponse response, int unread, string? toast = null)
    {
        var events = new Dictionary<string, object> { ["sb:bell-count"] = new { unread } };
        if (toast is not null)
        {
            events["sb:toast"] = new { message = toast, kind = "success" };
        }
        response.Headers["HX-Trigger"] = JsonSerializer.Serialize(events);
    }
}

public sealed record NotificationItemModel(NotificationListItem Item, bool WithActions);

[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class NotificationsModel(NotificationInbox inbox) : PageModel
{
    public const int PageSize = 30;

    [BindProperty(SupportsGet = true, Name = "view")]
    public string? Filter { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public bool UnreadOnly => string.Equals(Filter, "unread", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<NotificationListItem> Items { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int UnreadCount { get; private set; }
    public int MatchingCount { get; private set; }
    public bool HasMore => PageNumber * PageSize < MatchingCount;

    public async Task OnGetAsync(CancellationToken ct)
    {
        PageNumber = Math.Max(1, PageNumber);
        TotalCount = await inbox.CountAsync(false, ct);
        UnreadCount = await inbox.CountAsync(true, ct);
        MatchingCount = UnreadOnly ? UnreadCount : TotalCount;
        Items = await inbox.ListAsync(UnreadOnly, (PageNumber - 1) * PageSize, PageSize, ct);
    }

    public async Task<IActionResult> OnPostMarkAllReadAsync(CancellationToken ct)
    {
        var count = await inbox.MarkAllReadAsync(ct);
        var message = count == 1 ? "1 notification marked as read" : $"{count} notifications marked as read";
        if (Request.IsHtmx())
        {
            NotificationIcons.SetBellCount(Response, 0, message);
            return await MenuAsync(ct);
        }
        TempData.Toast(message);
        return RedirectToPage(new { view = Filter });
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id, bool read, CancellationToken ct)
    {
        await inbox.SetReadAsync(id, read, ct);
        var item = (await inbox.ListAsync(false, 0, 100, ct)).FirstOrDefault(i => i.Id == id);
        NotificationIcons.SetBellCount(Response, await inbox.UnreadCountAsync(ct));
        if (!Request.IsHtmx())
        {
            return RedirectToPage(new { view = Filter });
        }
        return item is null ? Content("") : Partial("_NotificationItem", new NotificationItemModel(item, true));
    }

    private async Task<IActionResult> MenuAsync(CancellationToken ct) =>
        Partial("_NotificationMenu", await NotificationMenuModel.LoadAsync(inbox, ct));
}

public sealed record NotificationMenuModel(IReadOnlyList<NotificationListItem> Items, int Unread)
{
    public static async Task<NotificationMenuModel> LoadAsync(NotificationInbox inbox, CancellationToken ct) =>
        new(await inbox.ListAsync(false, 0, 8, ct), await inbox.UnreadCountAsync(ct));
}

/// <summary>The bell's dropdown, loaded by htmx when it opens.</summary>
[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class NotificationMenuPageModel(NotificationInbox inbox) : PageModel
{
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var model = await NotificationMenuModel.LoadAsync(inbox, ct);
        NotificationIcons.SetBellCount(Response, model.Unread);
        return Partial("_NotificationMenu", model);
    }
}

/// <summary>Marks a notification read and follows its link. Only local links are followed.</summary>
[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class NotificationOpenModel(NotificationInbox inbox) : PageModel
{
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var link = await inbox.OpenAsync(id, ct);
        return LocalRedirect(link is { Length: > 0 } && Url.IsLocalUrl(link) ? link : "/notifications");
    }
}

[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class NotificationPreferencesModel(PreferenceService preferences, IActorAccessor actors) : PageModel
{
    public IActionResult OnGet() => LocalRedirect("/profile#notifications");

    public async Task<IActionResult> OnPostAsync(string[]? keys, string[]? inapp, string[]? email, CancellationToken ct)
    {
        var userId = actors.Current.UserId ?? throw new ForbiddenException();
        var inApp = (inapp ?? []).ToHashSet(StringComparer.Ordinal);
        var byEmail = (email ?? []).ToHashSet(StringComparer.Ordinal);
        var choices = (keys ?? [])
            .Where(k => PreferenceService.AgentEvents.Any(e => e.Key == k))
            .Distinct()
            .ToDictionary(k => k, k => new ChannelPreference(byEmail.Contains(k), inApp.Contains(k)));
        await preferences.SaveAsync(userId, choices, ct);
        TempData.Toast("Notification settings saved");
        return LocalRedirect("/profile#notifications");
    }
}
