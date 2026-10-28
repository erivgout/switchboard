using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.Modules.Api;
using Switchboard.Modules.Api.Auth;
using Switchboard.Modules.Api.Http;
using Switchboard.Testing;

namespace Switchboard.Modules.Api.Tests;

/// <summary>
/// Core plus the API module on a throwaway database, with a real HTTP pipeline (TestServer) in the same
/// middleware order as the host. Background services don't run; tests drive the outbox and webhooks.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private WebApplication _app = default!;

    public SwitchboardTestHost Host { get; private set; } = default!;
    public HttpClient Client { get; private set; } = default!;
    public IServiceProvider WebServices => _app.Services;

    public User Agent { get; private set; } = default!;
    public User Admin { get; private set; } = default!;
    public User Requester { get; private set; } = default!;
    public User OtherRequester { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        Host = await SwitchboardTestHost.CreateAsync([new ApiModule()], services => services.AddDataProtection().UseEphemeralDataProtectionProvider());

        var server = Environment.GetEnvironmentVariable("SWITCHBOARD_TEST_SQL") ?? SwitchboardTestHost.DefaultServer;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"{server};Database={Host.Database}",
            ["Switchboard:Storage:Path"] = Path.Combine(Path.GetTempPath(), Host.Database),
            ["Switchboard:BaseUrl"] = "http://switchboard.test",
        });
        var module = new ApiModule();
        var catalog = new ModuleCatalog([module]);
        builder.Services.AddSwitchboardCore(builder.Configuration, catalog);
        builder.Services.AddRouting();
        module.ConfigureServices(builder.Services, builder.Configuration);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.RemoveAll<IHostedService>();

        _app = builder.Build();
        _app.UseRouting();
        _app.UseRateLimiter();
        _app.UseAuthentication();
        _app.UseAuthorization();
        module.ConfigureApp(_app);
        module.MapEndpoints(_app);
        await _app.StartAsync();
        Client = _app.GetTestClient();

        Agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        var admin = await Host.CreateAgentAsync("Ada Admin", RoleKeys.WorkspaceAdmin);
        Admin = await Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var user = await db.Users.FirstAsync(u => u.Id == admin.Id);
            user.IsOrgAdmin = true;
            await db.SaveChangesAsync();
            return user;
        });
        Requester = await Host.CreateRequesterAsync("Dana Requester");
        OtherRequester = await Host.CreateRequesterAsync("Olive Other");
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await Host.DisposeAsync();
    }

    /// <summary>Creates a key for <paramref name="user"/> the way the profile page does, and returns the secret.</summary>
    public async Task<string> CreateKeyAsync(User user, string name = "Test key", int? days = 30, params string[] scopes) =>
        (await Host.RunAsync(SwitchboardTestHost.As(user, Channel.Web), sp =>
            sp.GetRequiredService<IApiKeyService>().CreateAsync(new CreateApiKeyRequest(name, scopes, days)))).Secret;

    public HttpRequestMessage Request(HttpMethod method, string url, string? key, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        if (body is not null)
        {
            request.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body, ApiJson.Options), Encoding.UTF8, "application/json");
        }
        return request;
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? key, object? body = null) =>
        Client.SendAsync(Request(method, url, key, body));

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Files a ticket through the API as <paramref name="key"/>'s owner and returns its id.</summary>
    public async Task<Guid> CreateTicketAsync(string key, object body)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tickets", key, body);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    public Task<T> InDbAsync<T>(Func<SwitchboardDbContext, Task<T>> query) =>
        Host.RunAsSystemAsync(sp => query(sp.GetRequiredService<SwitchboardDbContext>()));
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}

internal static class JsonExtensions
{
    public static string Str(this JsonElement e, string name) => e.GetProperty(name).GetString()!;
}
