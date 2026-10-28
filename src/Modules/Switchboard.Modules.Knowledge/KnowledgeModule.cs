using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core;
using Switchboard.Core.Agents;
using Switchboard.Core.Api;
using Switchboard.Core.Events;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Knowledge.Agents;
using Switchboard.Modules.Knowledge.Api;
using Switchboard.Modules.Knowledge.Components;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Seed;
using Switchboard.Modules.Knowledge.Services;

namespace Switchboard.Modules.Knowledge;

/// <summary>Knowledge base: articles, folders, approval, feedback, announcements and deflection (SRS §3.10, FR-POR-7).</summary>
public sealed class KnowledgeModule : ISwitchboardModule
{
    public string Name => "Knowledge";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IKnowledgeVisibility, KnowledgeVisibility>();
        services.AddScoped<IKnowledgeService, KnowledgeService>();
        services.AddScoped<IKbStructureService, KbStructureService>();
        services.AddScoped<IKbAnnouncementService, KbAnnouncementService>();
        services.AddScoped<IAttachmentAccessPolicy, KbArticleAttachmentPolicy>();
        services.AddScoped<IAttachmentAccessPolicy, KbAnnouncementAttachmentPolicy>();
        services.AddEventHandler<TicketCreated, KbDeflectionTicketHandler>();
        services.AddHostedService<KbDemoSeeder>();

        services.AddPrivileges(
            new PrivilegeDefinition(KbPrivileges.View, "View the knowledge base, including drafts", "Knowledge"),
            new PrivilegeDefinition(KbPrivileges.Write, "Write and edit article drafts", "Knowledge"),
            new PrivilegeDefinition(KbPrivileges.Publish, "Publish, approve and archive articles", "Knowledge"),
            new PrivilegeDefinition(KbPrivileges.Manage, "Manage categories, folders, announcements and trash", "Knowledge"));

        services.AddNavItem(new NavItem(NavArea.Agent, "kb", "Knowledge", "bi-journal-text", "/kb", Order: 40, Privilege: KbPrivileges.View));
        services.AddNavItem(new NavItem(NavArea.Portal, "kb", "Knowledge", "bi-journal-text", "/portal/kb", Order: 30));

        services.AddSlot<KbDashboardAnnouncements>("dashboard.widgets", order: 10);
        services.AddSlot<KbPortalHome>("portal.home", order: 20);
        services.AddSlot<KbPortalSearchResults>("portal.search", order: 20);
        services.AddSlot<KbDeflectionAside>("portal.newticket.aside", order: 10);
        services.AddSlot<KbAgentSearchResults>("search.results", order: 30);
        services.AddSlot<KbTicketSuggestions>("ticket.sidebar", order: 40);
        services.AddSlot<KbTicketActions>("ticket.actions", order: 40);

        services.AddAgentTool<SearchKnowledgeTool>();
        services.AddAgentTool<GetArticleTool>();
        services.AddAgentTool<CreateArticleDraftTool>();
        services.AddAgentTool<UpdateArticleDraftTool>();
        services.AddAgentTool<SubmitArticleForReviewTool>();
        services.AddAgentTool<PublishArticleTool>();
    }

    public void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(KnowledgeModule).Assembly);

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        KbApi.Map(endpoints);

        // Deflection suggestions for the portal's "Report a problem" form (FR-POR-7). Published, visible articles only.
        endpoints.MapGet("/portal/kb/suggest", async (string? q, IKnowledgeService knowledge, CancellationToken ct) =>
        {
            var hits = await knowledge.SuggestAsync((q ?? "").Length > 200 ? q![..200] : q ?? "", 5, ct);
            return Results.Json(hits.Select(h => new { id = h.Id, title = h.Title, snippet = h.Snippet, url = $"/portal/kb/{h.Id}?ref=deflect" }));
        }).AllowAnonymous().RequireRateLimiting(SwitchboardApi.RateLimitPolicy);

        // Related-article picker in the editor.
        endpoints.MapGet("/kb/pick", async (string? q, Guid? exclude, IKnowledgeService knowledge, CancellationToken ct) =>
        {
            var items = await knowledge.PickAsync(q ?? "", exclude, ct);
            return Results.Json(items.Select(i => new { id = i.Id, displayId = i.DisplayId, title = i.Title }));
        }).RequireAuthorization(SwitchboardPolicies.Privilege(KbPrivileges.View));
    }

    public Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        KbSeeder.SeedAsync(services, cancellationToken);
}
