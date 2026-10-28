using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Modules;
using Switchboard.Modules.Portal.Services;

namespace Switchboard.Modules.Portal;

public sealed class PortalModule : ISwitchboardModule
{
    /// <summary>
    /// Slot on the portal home for things waiting on the requester (pending approvals, surveys).
    /// Model: <c>null</c>. Render one <c>a.prt-attention-item</c> per item.
    /// </summary>
    public const string HomeAttentionSlot = "portal.home.attention";

    public string Name => "Portal";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<PortalRequests>();
        services.AddNavItem(new NavItem(NavArea.Portal, "requests", "My requests", "bi-inbox", "/portal/requests", Order: 10));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // People picker for CC and sharing: names and emails only.
        endpoints.MapGet("/portal/people", async (string? q, PortalRequests portal, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var people = await portal.FindPeopleAsync(q, ct);
            return Results.Ok(people.Select(p => new { id = p.Id, name = p.Name, email = p.Email }));
        }).RequireAuthorization();
    }
}
