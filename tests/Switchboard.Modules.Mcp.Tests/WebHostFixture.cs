using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Testing;

namespace Switchboard.Modules.Mcp.Tests;

/// <summary>
/// The real Switchboard host (with the MCP module) in memory, on its own demo database. Drives the browser
/// side of OAuth (sign-in, consent, approvals) and the agent side (registration, tokens, JSON-RPC).
/// </summary>
public sealed partial class WebHostFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string BaseUrl = "http://localhost";
    public const string Resource = BaseUrl + "/mcp";
    public const string RedirectUri = "http://127.0.0.1:43123/callback";

    private readonly string _database = $"sb_test_mcpweb_{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var server = Environment.GetEnvironmentVariable("SWITCHBOARD_TEST_SQL") ?? SwitchboardTestHost.DefaultServer;
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", $"{server};Database={_database}");
        builder.UseSetting("Switchboard:BaseUrl", BaseUrl);
        builder.UseSetting("Switchboard:DemoData", "true");
        builder.UseSetting("Switchboard:Storage:Path", Path.Combine(Path.GetTempPath(), _database));
    }

    public Task InitializeAsync()
    {
        _ = Server; // starts the host: schema, seed and demo data
        return Task.CompletedTask;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>().Database.EnsureDeletedAsync();
        }
        await DisposeAsync();
    }

    /// <summary>A client like an agent app: no cookies, no redirects followed.</summary>
    public HttpClient Api() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    /// <summary>A browser signed in as <paramref name="email"/>.</summary>
    public async Task<HttpClient> BrowserAsync(string email = DemoAccounts.Agent)
    {
        // Sign-ins are rate limited per address, so each demo user signs in once per fixture.
        await _signIn.WaitAsync();
        try
        {
            if (!_browsers.TryGetValue(email, out var cached))
            {
                _browsers[email] = cached = await SignInAsync(email);
            }
            return cached;
        }
        finally
        {
            _signIn.Release();
        }
    }

    private readonly Dictionary<string, HttpClient> _browsers = [];
    private readonly SemaphoreSlim _signIn = new(1, 1);

    private async Task<HttpClient> SignInAsync(string email)
    {
        var browser = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var page = await browser.GetStringAsync("/account/login");
        var response = await browser.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = DemoAccounts.Password,
            ["__RequestVerificationToken"] = Antiforgery(page),
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return browser;
    }

    public T Db<T>(Func<SwitchboardDbContext, T> query)
    {
        using var scope = Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>());
    }

    public async Task<T> DbAsync<T>(Func<SwitchboardDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>());
    }

    public static string Antiforgery(string html) =>
        WebUtility.HtmlDecode(AntiforgeryPattern().Match(html).Groups[1].Value);

    // ------------------------------------------------------------------ OAuth, by hand

    public sealed record Pkce(string Verifier, string Challenge)
    {
        public static Pkce Create()
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            return new Pkce(verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        }
    }

    public sealed record Tokens(string AccessToken, string? RefreshToken, int ExpiresIn, string Scope);

    public async Task<string> RegisterClientAsync(HttpClient api, string name = "Test agent", string redirectUri = RedirectUri)
    {
        var response = await api.PostAsJsonAsync("/oauth/register", new { client_name = name, redirect_uris = new[] { redirectUri }, token_endpoint_auth_method = "none" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        return body!["client_id"]!.GetValue<string>();
    }

    public static string AuthorizeUrl(string clientId, Pkce pkce, string scope, string? resource = Resource, string redirectUri = RedirectUri, string method = "S256")
    {
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["state"] = "s-123",
            ["code_challenge"] = method == "plain" ? pkce.Verifier : pkce.Challenge,
            ["code_challenge_method"] = method,
            ["resource"] = resource,
        };
        return "/oauth/authorize?" + string.Join("&", query.Where(p => p.Value is not null).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}"));
    }

    /// <summary>Opens the consent page in the browser and allows (or denies); returns the redirect back to the app.</summary>
    public static async Task<Uri> ConsentAsync(HttpClient browser, string authorizeUrl, bool allow = true, IEnumerable<string>? onlyScopes = null)
    {
        var page = await browser.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var form = new List<KeyValuePair<string, string>>();
        foreach (Match m in HiddenInputPattern().Matches(html))
        {
            form.Add(KeyValuePair.Create(WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)));
        }
        var offered = CheckedScopePattern().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        foreach (var scope in onlyScopes ?? offered)
        {
            form.Add(KeyValuePair.Create("GrantedScopes", scope));
        }
        form.Add(KeyValuePair.Create("__RequestVerificationToken", Antiforgery(html)));
        form.Add(KeyValuePair.Create("ExpiryDays", "30"));
        form.Add(KeyValuePair.Create("Decision", allow ? "allow" : "deny"));
        var response = await browser.PostAsync("/oauth/authorize", new FormUrlEncodedContent(form));
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"consent returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return response.Headers.Location!;
    }

    public static string? QueryValue(Uri uri, string name) =>
        uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => p[0] == name).Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();

    public async Task<(HttpStatusCode Status, JsonObject Body)> TokenAsync(HttpClient api, Dictionary<string, string> form)
    {
        var response = await api.PostAsync("/oauth/token", new FormUrlEncodedContent(form));
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonObject>())!);
    }

    /// <summary>The whole flow: register, sign in, consent, exchange the code.</summary>
    public async Task<(string ClientId, Tokens Tokens)> ConnectAsync(HttpClient browser, string scope = "profile tickets:read tickets:write tickets:reply", string resource = Resource)
    {
        var api = Api();
        var clientId = await RegisterClientAsync(api);
        var pkce = Pkce.Create();
        var back = await ConsentAsync(browser, AuthorizeUrl(clientId, pkce, scope, resource));
        var code = QueryValue(back, "code") ?? throw new InvalidOperationException($"No code in {back}");
        var (status, body) = await TokenAsync(api, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = pkce.Verifier,
            ["resource"] = resource,
        });
        Assert.True(status == HttpStatusCode.OK, body.ToJsonString());
        return (clientId, new Tokens(body["access_token"]!.GetValue<string>(), body["refresh_token"]?.GetValue<string>(), body["expires_in"]!.GetValue<int>(), body["scope"]?.GetValue<string>() ?? ""));
    }

    // ------------------------------------------------------------------ MCP over raw HTTP

    public sealed record RpcResponse(HttpStatusCode Status, string? SessionId, JsonObject? Message, string? WwwAuthenticate);

    public async Task<RpcResponse> RpcAsync(HttpClient api, string? token, string method, JsonObject? parameters = null, string? sessionId = null, string path = "/mcp", string? origin = null, int id = 1)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? [] }),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
        if (sessionId is not null)
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
            request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        }
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }
        var response = await api.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonObject? message = null;
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            var data = text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()).LastOrDefault(l => l.Length > 0);
            message = data is null ? null : JsonNode.Parse(data) as JsonObject;
        }
        else if (text.TrimStart().StartsWith('{'))
        {
            message = JsonNode.Parse(text) as JsonObject;
        }
        response.Headers.TryGetValues("Mcp-Session-Id", out var sessions);
        return new RpcResponse(response.StatusCode, sessions?.FirstOrDefault(), message, response.Headers.WwwAuthenticate.ToString());
    }

    /// <summary>initialize + notifications/initialized; returns the session id.</summary>
    public async Task<string> InitializeAsync(HttpClient api, string token, string path = "/mcp")
    {
        var init = await RpcAsync(api, token, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-11-25",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "raw-test", ["version"] = "1" },
        }, path: path);
        Assert.True(init.Status == HttpStatusCode.OK, $"initialize returned {(int)init.Status}");
        Assert.NotNull(init.SessionId);
        using var notify = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }),
        };
        notify.Headers.Accept.ParseAdd("application/json");
        notify.Headers.Accept.ParseAdd("text/event-stream");
        notify.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        notify.Headers.Add("Mcp-Session-Id", init.SessionId);
        notify.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        await api.SendAsync(notify);
        return init.SessionId!;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryPattern();

    [GeneratedRegex("<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\"")]
    private static partial Regex HiddenInputPattern();

    [GeneratedRegex("name=\"GrantedScopes\" value=\"([^\"]+)\"[^>]*checked")]
    private static partial Regex CheckedScopePattern();
}
