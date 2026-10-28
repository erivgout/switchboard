using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Modules;

namespace Switchboard.UI;

public enum ToastKind
{
    Success,
    Error,
}

/// <summary>User feedback after an action: TempData for redirects, HX-Trigger for htmx responses.</summary>
public static class Feedback
{
    public const string TempDataKey = "sb.toast";

    /// <summary>Show a toast on the next page (POST-redirect-GET).</summary>
    public static void Toast(this ITempDataDictionary tempData, string message, ToastKind kind = ToastKind.Success) =>
        tempData[TempDataKey] = $"{(kind == ToastKind.Error ? "error" : "success")}|{message}";

    /// <summary>Show a toast after an htmx request (sets the HX-Trigger header).</summary>
    public static void HxToast(this HttpResponse response, string message, ToastKind kind = ToastKind.Success)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["sb:toast"] = new { message, kind = kind == ToastKind.Error ? "error" : "success" },
        });
        response.Headers["HX-Trigger"] = payload;
    }

    /// <summary>True when the request came from htmx.</summary>
    public static bool IsHtmx(this HttpRequest request) => request.Headers.ContainsKey("HX-Request");
}

public static class UiServiceCollectionExtensions
{
    public static IServiceCollection AddSwitchboardUi(this IServiceCollection services)
    {
        services.AddScoped<IShellContext, ShellContext>();
        return services;
    }
}
