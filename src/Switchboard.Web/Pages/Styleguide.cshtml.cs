using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;

namespace Switchboard.Web.Pages;

/// <summary>Living reference of the design system. Development only.</summary>
[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class StyleguideModel(IWebHostEnvironment env) : PageModel
{
    public IActionResult OnGet() => env.IsDevelopment() ? Page() : NotFound();
}
