using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Api;
using Switchboard.Core.Events;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Api.Auth;
using Switchboard.Modules.Api.Endpoints;
using Switchboard.Modules.Api.Http;
using Switchboard.Modules.Api.OpenApi;
using Switchboard.Modules.Api.Pages;
using Switchboard.Modules.Api.Webhooks;

namespace Switchboard.Modules.Api;

/// <summary>
/// REST API (FR-INT-1): the <see cref="SwitchboardApi.Scheme"/> authentication scheme for personal API keys,
/// key management, ticket and directory endpoints, the OpenAPI document, and outbound webhooks (FR-INT-3).
/// </summary>
public sealed class ApiModule : ISwitchboardModule
{
    public string Name => "Api";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication()
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(SwitchboardApi.Scheme, null);
        services.AddSingleton<IPrivilegeScopeGuard, ApiKeyScopeGuard>();
        services.AddScoped<IApiKeyService, ApiKeyService>();
        services.AddScoped<ApiTicketReader>();
        services.AddScoped<ApiAccessFilter>();
        services.AddScoped<IdempotencyFilter>();

        // API JSON for every module's minimal API endpoints: camelCase, enums as camelCase strings.
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(ApiJson.EnumConverter(allowIntegers: true)));
        services.AddSwitchboardOpenApi();

        // API callers get problem+json for rate limiting too, with Retry-After (FR-INT-1).
        services.Configure<RateLimiterOptions>(o =>
        {
            var previous = o.OnRejected;
            o.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                if (!http.Request.Path.StartsWithSegments(SwitchboardApi.Prefix))
                {
                    if (previous is not null)
                    {
                        await previous(context, ct);
                    }
                    return;
                }
                if (context.Lease.TryGetMetadata(System.Threading.RateLimiting.MetadataName.RetryAfter, out var retry))
                {
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                await ApiProblems.WriteAsync(http, StatusCodes.Status429TooManyRequests, ApiProblems.TitleFor(429), "Too many requests. Wait and retry.");
            };
        });

        services.AddDataProtection();
        services.Configure<WebhookOptions>(configuration.GetSection(WebhookOptions.Section));
        services.AddSingleton<SsrfGuard>();
        services.AddSingleton<WebhookSignal>();
        services.AddScoped<WebhookService>();
        services.AddScoped<WebhookSender>();
        services.AddSingleton<WebhookDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
        services.AddHttpClient(WebhookSender.ClientName, (sp, client) =>
            {
                client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<WebhookOptions>>().Value.TimeoutSeconds + 5);
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var guard = sp.GetRequiredService<SsrfGuard>();
                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    UseCookies = false,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    MaxResponseHeadersLength = 64,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    ConnectCallback = guard.ConnectAsync,
                };
            });
        AddWebhookHandlers(services);

        services.AddSlot<ApiKeysViewComponent>("profile.sections", order: 50);
        services.AddNavItem(new NavItem(NavArea.Admin, "api-keys", "API keys", "bi-key", "/admin/api-keys", Order: 40,
            Privilege: Privileges.SettingsManage, Group: "Integrations", Description: "Keys people use to reach the REST API"));
        services.AddNavItem(new NavItem(NavArea.Admin, "webhooks", "Webhooks", "bi-broadcast", "/admin/webhooks", Order: 41,
            Privilege: Privileges.SettingsManage, Group: "Integrations", Description: "Send ticket events to other systems"));
    }

    private static void AddWebhookHandlers(IServiceCollection services)
    {
        services.AddEventHandler<TicketCreated, WebhookEventHandler>();
        services.AddEventHandler<TicketUpdated, WebhookEventHandler>();
        services.AddEventHandler<TicketStatusChanged, WebhookEventHandler>();
        services.AddEventHandler<TicketAssigned, WebhookEventHandler>();
        services.AddEventHandler<TicketConversationAdded, WebhookEventHandler>();
        services.AddEventHandler<TicketsMerged, WebhookEventHandler>();
        services.AddEventHandler<TicketDeleted, WebhookEventHandler>();
        services.AddEventHandler<TicketRestored, WebhookEventHandler>();
        services.AddEventHandler<TicketTaskChanged, WebhookEventHandler>();
    }

    public void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApiModule).Assembly);

    public void ConfigureApp(IApplicationBuilder app)
    {
        // API responses are never HTML status pages: empty error responses become problem+json.
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(SwitchboardApi.Prefix))
            {
                await next(context);
                return;
            }
            if (context.Features.Get<IStatusCodePagesFeature>() is { } pages)
            {
                pages.Enabled = false;
            }
            await next(context);
            var response = context.Response;
            if (!response.HasStarted && response.StatusCode >= 400 && response.ContentLength is null or 0 && string.IsNullOrEmpty(response.ContentType))
            {
                await ApiProblems.WriteAsync(context, response.StatusCode, ApiProblems.TitleFor(response.StatusCode), null);
            }
        });
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var maxUpload = endpoints.ServiceProvider.GetRequiredService<IOptions<StorageOptions>>().Value.MaxUploadBytes;
        TicketEndpoints.Map(endpoints);
        AttachmentEndpoints.Map(endpoints, maxUpload);
        DirectoryEndpoints.Map(endpoints);
        endpoints.MapSwitchboardOpenApi();

        // Unknown API routes: 401 without a key, else a problem+json 404, never the HTML status page.
        endpoints.MapFallback($"{SwitchboardApi.Prefix}/{{**path}}", (HttpContext http) => ApiProblems.NotFound(http))
            .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { AuthenticationSchemes = SwitchboardApi.Scheme })
            .ExcludeFromDescription();
    }
}
