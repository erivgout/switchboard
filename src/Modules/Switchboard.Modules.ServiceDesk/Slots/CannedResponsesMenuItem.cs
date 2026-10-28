using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Switchboard.UI;

namespace Switchboard.Modules.ServiceDesk.Slots;

/// <summary>Adds "Canned responses" to the agent's user menu.</summary>
public sealed class CannedResponsesMenuItem : ViewComponent
{
    public IViewComponentResult Invoke(object? model)
    {
        if (model is not ShellModel { User.IsAgent: true })
        {
            return Content("");
        }
        return new HtmlContentViewComponentResult(new HtmlString(
            "<a class=\"dropdown-item\" href=\"/tickets/canned-responses\"><i class=\"bi bi-chat-square-quote me-2\" aria-hidden=\"true\"></i>Canned responses</a>"));
    }
}
