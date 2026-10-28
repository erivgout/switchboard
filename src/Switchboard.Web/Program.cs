using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Events;
using Switchboard.Core.Services;
using Switchboard.UI;
using Switchboard.Web.Hosting;

if (args.Contains("--healthcheck"))
{
    // Used by the container HEALTHCHECK; no extra tools needed in the image.
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    try
    {
        var response = await probe.GetAsync("http://localhost:8080/health/live");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
var modules = ModuleLoader.Discover();

builder.Services.AddSwitchboardCore(builder.Configuration, modules);
builder.Services.AddSwitchboardUi();
builder.Services.AddSwitchboardIdentity(builder.Environment);

var keysPath = builder.Configuration["Switchboard:DataProtection:Path"];
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Switchboard");
if (!string.IsNullOrWhiteSpace(keysPath))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

builder.Services.AddRouting(o => o.LowercaseUrls = true);
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "RequestVerificationToken";
    o.Cookie.Name = "sb.af";
    o.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.AddRazorPages();
builder.Services.AddSignalR();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddSingleton<SetupTokenStore>();
builder.Services.AddSingleton<ILiveNotifier, SignalRLiveNotifier>();
builder.Services.AddEventHandler<TicketCreated, TicketLiveEvents>();
builder.Services.AddEventHandler<TicketUpdated, TicketLiveEvents>();
builder.Services.AddEventHandler<TicketConversationAdded, TicketLiveEvents>();
builder.Services.AddOptions<Switchboard.Core.Security.ContentSecurityPolicyOptions>();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddDbContextCheck<SwitchboardDbContext>("database", tags: ["ready"]);

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("uploads", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

if (builder.Configuration.GetValue<bool>("Switchboard:TrustForwardedHeaders"))
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

foreach (var module in modules.Modules)
{
    module.ConfigureServices(builder.Services, builder.Configuration);
}

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app);

if (app.Configuration.GetValue<bool>("Switchboard:TrustForwardedHeaders"))
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/status/500");
    app.UseHsts();
}

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseStatusCodePagesWithReExecute("/status/{0}");
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<ActiveUserMiddleware>();
app.UseAuthorization();
// After authorization, so endpoint-specific schemes (API keys) have set the user the limiter partitions on.
app.UseRateLimiter();
app.UseMiddleware<DomainExceptionMiddleware>();

foreach (var module in modules.Modules)
{
    module.ConfigureApp(app);
}

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
app.MapHub<LiveHub>("/hubs/live");
app.MapFileEndpoints();
app.MapRazorPages();

foreach (var module in modules.Modules)
{
    module.MapEndpoints(app);
}

app.Logger.LogInformation("Switchboard started with modules: {Modules}",
    modules.Modules.Count == 0 ? "(none)" : string.Join(", ", modules.Modules.Select(m => m.Name)));

await app.RunAsync();
return 0;

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;
