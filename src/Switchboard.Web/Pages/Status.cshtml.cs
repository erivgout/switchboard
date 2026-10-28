using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Switchboard.Web.Pages;

[AllowAnonymous]
[Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryToken]
public sealed class StatusModel : PageModel
{
    public int Code { get; private set; }
    public string Heading { get; private set; } = "";
    public string Message { get; private set; } = "";
    public string Icon { get; private set; } = "bi-exclamation-circle";

    public void OnGet(int code) => Fill(code);

    public void OnPost(int code) => Fill(code);

    private void Fill(int code)
    {
        Code = code;
        var detail = HttpContext.Items["sb.error.message"] as string;
        (Heading, Message, Icon) = code switch
        {
            400 => ("That didn't work", detail ?? "The request had a problem. Check what you entered and try again.", "bi-exclamation-circle"),
            401 => ("Sign in to continue", "You need to be signed in to see this page.", "bi-person-lock"),
            403 => ("You don't have access", detail ?? "Your role doesn't include this. Ask an admin if you need it.", "bi-shield-lock"),
            404 => ("Nothing here", "That page or record doesn't exist, or you can't see it.", "bi-signpost-split"),
            409 => ("Someone else changed this", detail ?? "Reload the page to see their changes, then try again.", "bi-arrow-repeat"),
            429 => ("Slow down", "Too many attempts in a short time. Wait a minute and try again.", "bi-hourglass-split"),
            _ => ("Something went wrong", "The server hit an error. Try again; if it keeps happening, tell your admin.", "bi-bug"),
        };
    }
}
