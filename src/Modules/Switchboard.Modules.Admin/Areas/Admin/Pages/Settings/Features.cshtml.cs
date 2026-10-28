using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Settings;

/// <summary>Module toggles for everyone and per workspace (FR-ORG-8).</summary>
[Authorize(Policy = SwitchboardPolicies.OrgAdmin)]
public sealed class FeaturesModel(AdminSettingsService settings) : AdminPageModel
{
    public IReadOnlyList<FeatureState> Features { get; private set; } = [];
    public IReadOnlyList<Workspace> Workspaces { get; private set; } = [];

    public async Task OnGetAsync() => (Features, Workspaces) = await settings.GetFeaturesAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        (Features, Workspaces) = await settings.GetFeaturesAsync();
        var form = Request.Form;
        var instance = Features.ToDictionary(f => f.Module, f => form[$"on.{f.Module}"].Contains("true"));
        var overrides = new Dictionary<(string, Guid), bool?>();
        if (Workspaces.Count > 1)
        {
            foreach (var f in Features)
            {
                foreach (var w in Workspaces)
                {
                    overrides[(f.Module, w.Id)] = form[$"ws.{f.Module}.{w.Id}"].ToString() switch
                    {
                        "on" => true,
                        "off" => false,
                        _ => null,
                    };
                }
            }
        }
        await settings.SaveFeaturesAsync(instance, overrides);
        TempData.Toast("Features saved");
        return RedirectToPage();
    }

    public static string Describe(string module) => module switch
    {
        "ServiceDesk" => "The agent ticket queue and ticket pages.",
        "Portal" => "The requester portal.",
        "Knowledge" => "Knowledge base articles for agents and requesters.",
        "Catalog" => "The service catalog of requestable items.",
        "Approvals" => "Approval steps on requests and changes.",
        "ServiceLevels" => "Service level targets and business hours.",
        "Automation" => "Rules that act on tickets automatically.",
        "Messaging" => "Email in and out, and notifications.",
        "Insights" => "Dashboards and reports.",
        "Api" => "The REST API and personal API keys.",
        "Mcp" => "Access for AI agents through MCP.",
        _ => "",
    };

    public static string Title(string module) => module switch
    {
        "ServiceDesk" => "Service desk",
        "ServiceLevels" => "Service levels",
        "Api" => "REST API",
        "Mcp" => "AI agent access (MCP)",
        _ => module,
    };
}
