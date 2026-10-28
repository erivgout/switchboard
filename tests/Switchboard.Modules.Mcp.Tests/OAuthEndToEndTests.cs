using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Switchboard.Core;
using Switchboard.Core.Domain;
using Switchboard.Modules.Mcp.Data;
using SdkClient = ModelContextProtocol.Client.McpClient;

namespace Switchboard.Modules.Mcp.Tests;

/// <summary>
/// The real host over HTTP: OAuth 2.1 (DCR, PKCE, consent, resource-bound tokens, refresh rotation) and the
/// MCP endpoint, driven by the official C# MCP client and by raw requests.
/// </summary>
public sealed partial class OAuthEndToEndTests(WebHostFixture web) : IClassFixture<WebHostFixture>
{
    [Fact]
    public async Task Official_client_runs_the_whole_flow_with_out_of_band_confirmation()
    {
        var browser = await web.BrowserAsync(DemoAccounts.Agent);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(WebHostFixture.Resource),
            TransportMode = HttpTransportMode.StreamableHttp,
            OAuth = new ClientOAuthOptions
            {
                RedirectUri = new Uri(WebHostFixture.RedirectUri),
                DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "E2E Agent" },
                AuthorizationCallbackHandler = (context, _) => CompleteAsync(browser, context),
            },
        }, web.Api());

        await using var client = await SdkClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = "2025-11-25" });
        Assert.Equal("switchboard", client.ServerInfo?.Name);

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "search_tickets");
        Assert.Contains(tools, t => t.Name == "reply_to_ticket");
        var reply = tools.First(t => t.Name == "reply_to_ticket").ProtocolTool;
        Assert.True(reply.Annotations!.OpenWorldHint);
        Assert.False(reply.Annotations.ReadOnlyHint);
        Assert.NotNull(reply.OutputSchema);

        var search = await client.CallToolAsync("search_tickets", new Dictionary<string, object?> { ["query"] = "\"Outlook keeps asking\"" });
        Assert.NotEqual(true, search.IsError);
        var ticket = search.StructuredContent!.Value.GetProperty("tickets")[0];
        Assert.Equal("untrusted", ticket.GetProperty("subject").GetProperty("trust").GetString());
        var displayId = ticket.GetProperty("display_id").GetString()!;

        var replied = await client.CallToolAsync("reply_to_ticket", new Dictionary<string, object?> { ["ticket_id"] = displayId, ["body"] = "Signing out of Office fixed it for others. Can you try?" });
        Assert.True(replied.IsError);
        var pending = replied.StructuredContent!.Value.GetProperty("pending_action");
        var pendingId = pending.GetProperty("id").GetString()!;
        Assert.Equal("confirmation_required", replied.StructuredContent.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.StartsWith($"{WebHostFixture.BaseUrl}/agent-actions/", pending.GetProperty("confirm_url").GetString());

        // The person approves in Switchboard, signed in as themselves.
        var page = await browser.GetStringAsync($"/agent-actions/{pendingId}");
        Assert.Contains("What will happen", page);
        Assert.Contains("Dana Whitfield", page);
        var approve = await browser.PostAsync($"/agent-actions/{pendingId}?handler=Approve", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebHostFixture.Antiforgery(page),
        }));
        Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);

        var outcome = await client.CallToolAsync("get_pending_action", new Dictionary<string, object?> { ["id"] = pendingId });
        Assert.Equal("done", outcome.StructuredContent!.Value.GetProperty("status").GetString());

        var sent = await web.DbAsync(db => db.TicketConversations.AsNoTracking()
            .Where(c => c.Kind == ConversationKind.AgentReply && c.Channel == Channel.Mcp).ToListAsync());
        Assert.Single(sent);
        var activity = await web.DbAsync(db => db.ActivityEntries.AsNoTracking().Where(a => a.Action == "replied" && a.Channel == Channel.Mcp).SingleAsync());
        Assert.Equal("E2E Agent", activity.ClientName);
        Assert.Equal("Sam Rivera", activity.ActorName);
    }

    /// <summary>Plays the user's part in the browser: consent, then hand the code and issuer back to the client.</summary>
    private static async Task<AuthorizationResult?> CompleteAsync(HttpClient browser, AuthorizationCallbackContext context)
    {
        var back = await WebHostFixture.ConsentAsync(browser, context.AuthorizationUri.PathAndQuery);
        return new AuthorizationResult
        {
            Code = WebHostFixture.QueryValue(back, "code"),
            State = WebHostFixture.QueryValue(back, "state"),
            Iss = WebHostFixture.QueryValue(back, "iss"),
        };
    }

    [Fact]
    public async Task Clients_with_elicitation_confirm_in_the_client()
    {
        var browser = await web.BrowserAsync(DemoAccounts.Supervisor);
        var asked = new List<string>();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(WebHostFixture.Resource),
            TransportMode = HttpTransportMode.StreamableHttp,
            OAuth = new ClientOAuthOptions
            {
                RedirectUri = new Uri(WebHostFixture.RedirectUri),
                DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "Eliciting Agent" },
                AuthorizationCallbackHandler = (context, _) => CompleteAsync(browser, context),
            },
        }, web.Api());
        await using var client = await SdkClient.CreateAsync(transport, new McpClientOptions
        {
            ProtocolVersion = "2025-11-25",
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, _) =>
                {
                    asked.Add(request!.Message);
                    return ValueTask.FromResult(new ElicitResult
                    {
                        Action = "accept",
                        Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(true) },
                    });
                },
            },
        });

        var search = await client.CallToolAsync("search_tickets", new Dictionary<string, object?> { ["query"] = "\"VPN drops\"" });
        var displayId = search.StructuredContent!.Value.GetProperty("tickets")[0].GetProperty("display_id").GetString()!;
        var reply = await client.CallToolAsync("reply_to_ticket", new Dictionary<string, object?> { ["ticket_id"] = displayId, ["body"] = "Glad it's holding up." });
        Assert.NotEqual(true, reply.IsError);
        var message = Assert.Single(asked);
        Assert.Contains("Marcus Chen", message);
        Assert.Contains("Glad it's holding up.", message);
    }

    [Fact]
    public async Task Unauthenticated_and_cookie_requests_get_401_with_resource_metadata()
    {
        var anonymous = await web.RpcAsync(web.Api(), null, "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
        Assert.Contains($"resource_metadata=\"{WebHostFixture.BaseUrl}/.well-known/oauth-protected-resource/mcp\"", anonymous.WwwAuthenticate);

        // A signed-in browser session never authenticates the MCP endpoint.
        var browser = await web.BrowserAsync();
        var cookie = await web.RpcAsync(browser, null, "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, cookie.Status);

        var metadata = await web.Api().GetFromJsonAsync<JsonObject>("/.well-known/oauth-protected-resource/mcp");
        Assert.Equal(WebHostFixture.Resource, metadata!["resource"]!.GetValue<string>());
        var server = await web.Api().GetFromJsonAsync<JsonObject>("/.well-known/oauth-authorization-server");
        Assert.Equal(metadata["authorization_servers"]![0]!.GetValue<string>(), server!["issuer"]!.GetValue<string>());
        Assert.Equal(["S256"], server["code_challenge_methods_supported"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.EndsWith("/oauth/register", server["registration_endpoint"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tokens_are_bound_to_their_endpoint()
    {
        await web.DbAsync(async db =>
        {
            if (!await db.Set<McpServerConfiguration>().AnyAsync(c => c.Slug == "sales"))
            {
                db.Set<McpServerConfiguration>().Add(new McpServerConfiguration { Slug = "sales", Name = "Sales", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
            return true;
        });
        var browser = await web.BrowserAsync();
        var (_, tokens) = await web.ConnectAsync(browser, resource: WebHostFixture.Resource + "/sales");
        var api = web.Api();

        var wrongEndpoint = await web.RpcAsync(api, tokens.AccessToken, "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongEndpoint.Status);
        Assert.Contains("invalid_token", wrongEndpoint.WwwAuthenticate);

        var session = await web.InitializeAsync(api, tokens.AccessToken, "/mcp/sales");
        var ok = await web.RpcAsync(api, tokens.AccessToken, "tools/list", sessionId: session, path: "/mcp/sales", id: 2);
        Assert.Equal(HttpStatusCode.OK, ok.Status);

        // A token from another server (or a made-up one) is refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.RpcAsync(api, "eyJhbGciOiJub25lIn0.e30.", "tools/list")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.RpcAsync(api, "sb_" + new string('a', 43), "tools/list")).Status);
    }

    [Fact]
    public async Task Revoked_or_expired_grants_are_refused_on_the_next_request()
    {
        var browser = await web.BrowserAsync();
        var (clientId, tokens) = await web.ConnectAsync(browser);
        var api = web.Api();
        var session = await web.InitializeAsync(api, tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await web.RpcAsync(api, tokens.AccessToken, "tools/list", sessionId: session, id: 2)).Status);

        var grantId = await web.DbAsync(db => db.Set<McpGrant>().Where(g => g.ClientId == clientId).Select(g => g.Id).SingleAsync());
        // The user disconnects the agent from their profile.
        var profile = await browser.GetStringAsync("/profile");
        Assert.Contains("Connected agents", profile);
        Assert.Contains("Test agent", profile);
        var revoke = await browser.PostAsync("/agent-actions?handler=Revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grantId"] = grantId.ToString(),
            ["returnUrl"] = "/profile",
            ["__RequestVerificationToken"] = WebHostFixture.Antiforgery(profile),
        }));
        Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.RpcAsync(api, tokens.AccessToken, "tools/list", sessionId: session, id: 3)).Status);
        var refresh = await web.TokenAsync(api, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.RefreshToken!, ["client_id"] = clientId });
        Assert.Equal(HttpStatusCode.BadRequest, refresh.Status);

        // Expired grants are refused the same way.
        var (otherClient, other) = await web.ConnectAsync(browser);
        await web.DbAsync(db => db.Set<McpGrant>().Where(g => g.ClientId == otherClient).ExecuteUpdateAsync(s => s.SetProperty(g => g.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.RpcAsync(api, other.AccessToken, "tools/list")).Status);
    }

    [Fact]
    public async Task Reusing_a_refresh_token_revokes_the_grant()
    {
        var browser = await web.BrowserAsync();
        var (clientId, tokens) = await web.ConnectAsync(browser);
        var api = web.Api();
        Assert.NotNull(tokens.RefreshToken);
        Assert.InRange(tokens.ExpiresIn, 890, 900);

        var first = await web.TokenAsync(api, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.RefreshToken!, ["client_id"] = clientId });
        Assert.True(first.Status == HttpStatusCode.OK, first.Body.ToJsonString());
        var rotated = first.Body["refresh_token"]!.GetValue<string>();
        Assert.NotEqual(tokens.RefreshToken, rotated);

        var replay = await web.TokenAsync(api, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.RefreshToken!, ["client_id"] = clientId });
        Assert.Equal(HttpStatusCode.BadRequest, replay.Status);

        var grant = await web.DbAsync(db => db.Set<McpGrant>().AsNoTracking().SingleAsync(g => g.ClientId == clientId));
        Assert.Equal("refresh_reuse", grant.RevokedReason);
        var afterReuse = await web.TokenAsync(api, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = rotated, ["client_id"] = clientId });
        Assert.Equal(HttpStatusCode.BadRequest, afterReuse.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.RpcAsync(api, first.Body["access_token"]!.GetValue<string>(), "tools/list")).Status);
    }

    [Fact]
    public async Task Authorization_requests_must_use_s256_and_an_exact_redirect_uri()
    {
        var browser = await web.BrowserAsync();
        var api = web.Api();
        var clientId = await web.RegisterClientAsync(api);
        var pkce = WebHostFixture.Pkce.Create();

        // Rejected either on an error page or by redirecting back with an error; never with a code.
        static async Task AssertRejectedAsync(HttpResponseMessage response)
        {
            var location = response.Headers.Location?.ToString() ?? "";
            Assert.DoesNotContain("code=", location);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest || location.Contains("error=invalid_request"),
                $"{response.StatusCode} {location} {await response.Content.ReadAsStringAsync()}");
        }
        await AssertRejectedAsync(await browser.GetAsync(WebHostFixture.AuthorizeUrl(clientId, pkce, "profile", method: "plain")));
        await AssertRejectedAsync(await browser.GetAsync(WebHostFixture.AuthorizeUrl(clientId, pkce, "profile").Replace("code_challenge", "x_challenge")));

        // A redirect URI that doesn't match exactly is never redirected to.
        var mismatch = await browser.GetAsync(WebHostFixture.AuthorizeUrl(clientId, pkce, "profile", redirectUri: WebHostFixture.RedirectUri + "/extra"));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Null(mismatch.Headers.Location);

        var unknownResource = await browser.GetAsync(WebHostFixture.AuthorizeUrl(clientId, pkce, "profile", resource: "https://evil.example.com/mcp"));
        Assert.Contains("this Switchboard doesn&#x27;t serve", await unknownResource.Content.ReadAsStringAsync());

        // Denying sends the user back with access_denied, and no grant exists.
        var denied = await WebHostFixture.ConsentAsync(browser, WebHostFixture.AuthorizeUrl(clientId, pkce, "profile tickets:read"), allow: false);
        Assert.Equal("access_denied", WebHostFixture.QueryValue(denied, "error"));
        Assert.False(await web.DbAsync(db => db.Set<McpGrant>().AnyAsync(g => g.ClientId == clientId)));
    }

    [Fact]
    public async Task Consent_can_narrow_scopes_and_requesters_get_requester_scopes()
    {
        var api = web.Api();
        var requester = await web.BrowserAsync(DemoAccounts.Requester);
        var clientId = await web.RegisterClientAsync(api);
        var pkce = WebHostFixture.Pkce.Create();
        var page = await requester.GetStringAsync(WebHostFixture.AuthorizeUrl(clientId, pkce, "profile tickets:read users:read tickets:delete"));
        Assert.Contains("Your own requests", page);
        Assert.DoesNotContain("value=\"users:read\"", page);
        Assert.DoesNotContain("value=\"tickets:delete\"", page);

        var back = await WebHostFixture.ConsentAsync(requester, WebHostFixture.AuthorizeUrl(clientId, pkce, "profile tickets:read users:read"), onlyScopes: ["profile"]);
        var code = WebHostFixture.QueryValue(back, "code")!;
        var token = await web.TokenAsync(api, new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = WebHostFixture.RedirectUri,
            ["client_id"] = clientId, ["code_verifier"] = pkce.Verifier, ["resource"] = WebHostFixture.Resource,
        });
        Assert.Equal(HttpStatusCode.OK, token.Status);
        var grant = await web.DbAsync(db => db.Set<McpGrant>().AsNoTracking().SingleAsync(g => g.ClientId == clientId));
        Assert.Equal(["profile"], grant.Scopes);

        var session = await web.InitializeAsync(api, token.Body["access_token"]!.GetValue<string>());
        var list = await web.RpcAsync(api, token.Body["access_token"]!.GetValue<string>(), "tools/list", sessionId: session, id: 2);
        var names = list.Message!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("whoami", names);
        Assert.DoesNotContain("search_tickets", names);
    }

    [Fact]
    public async Task Sessions_belong_to_one_connection_and_origins_are_checked()
    {
        var sam = await web.BrowserAsync(DemoAccounts.Agent);
        var priya = await web.BrowserAsync(DemoAccounts.Supervisor);
        var (_, samTokens) = await web.ConnectAsync(sam);
        var (_, priyaTokens) = await web.ConnectAsync(priya);
        var api = web.Api();
        var samSession = await web.InitializeAsync(api, samTokens.AccessToken);

        var hijack = await web.RpcAsync(api, priyaTokens.AccessToken, "tools/list", sessionId: samSession, id: 5);
        Assert.True(hijack.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"got {hijack.Status}");

        var evil = await web.RpcAsync(api, samTokens.AccessToken, "tools/list", sessionId: samSession, origin: "https://evil.example.com", id: 6);
        Assert.Equal(HttpStatusCode.Forbidden, evil.Status);
        var own = await web.RpcAsync(api, samTokens.AccessToken, "tools/list", sessionId: samSession, origin: WebHostFixture.BaseUrl, id: 7);
        Assert.Equal(HttpStatusCode.OK, own.Status);
    }

    [Fact]
    public async Task Personal_api_keys_work_only_when_allowed_and_keep_their_scopes()
    {
        var key = "sb_" + new string('k', 43);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        await web.DbAsync(async db =>
        {
            var sam = await db.Users.FirstAsync(u => u.Email == DemoAccounts.Agent);
            db.ApiKeys.Add(new ApiKey { UserId = sam.Id, Name = "Read only", Prefix = key[..11], KeyHash = hash, Scopes = ["profile", "tickets:read"], CreatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        var api = web.Api();

        // Off by default (MCP-14).
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.RpcAsync(api, key, "tools/list")).Status);

        await web.DbAsync(async db =>
        {
            db.Settings.Add(new Setting { Key = McpSettings.ApiKeysAllowed, ValueJson = "true", UpdatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        try
        {
            var session = await web.InitializeAsync(api, key);
            var list = await web.RpcAsync(api, key, "tools/list", sessionId: session, id: 2);
            var names = list.Message!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
            Assert.Contains("search_tickets", names);
            Assert.DoesNotContain("reply_to_ticket", names);

            var call = await web.RpcAsync(api, key, "tools/call", new JsonObject { ["name"] = "whoami", ["arguments"] = new JsonObject() }, session, id: 3);
            Assert.True(call.Message!["result"]!["structuredContent"]!["connection"]!["via_api_key"]!.GetValue<bool>());
            var audit = await web.DbAsync(db => db.AuditEntries.AsNoTracking().Where(a => a.Action == "mcp.tool.called").OrderByDescending(a => a.Sequence).FirstAsync());
            Assert.Contains("\"viaApiKey\":true", audit.DetailsJson);
        }
        finally
        {
            await web.DbAsync(db => db.Settings.Where(s => s.Key == McpSettings.ApiKeysAllowed).ExecuteDeleteAsync());
        }
    }

    [Fact]
    public async Task Admin_can_turn_off_open_registration()
    {
        var admin = await web.BrowserAsync(DemoAccounts.Admin);
        var page = await admin.GetStringAsync("/admin/mcp");
        var save = await admin.PostAsync("/admin/mcp?handler=Settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Enabled"] = "true",
            ["OpenRegistration"] = "false",
            ["__RequestVerificationToken"] = WebHostFixture.Antiforgery(page),
        }));
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        try
        {
            var response = await web.Api().PostAsJsonAsync("/oauth/register", new { client_name = "Late", redirect_uris = new[] { WebHostFixture.RedirectUri } });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var agentPage = await (await web.BrowserAsync(DemoAccounts.Agent)).GetAsync("/admin/mcp");
            Assert.True(agentPage.StatusCode == HttpStatusCode.Forbidden || agentPage.Headers.Location?.ToString().Contains("403") == true, $"agent got {agentPage.StatusCode}");
        }
        finally
        {
            var again = await admin.GetStringAsync("/admin/mcp");
            await admin.PostAsync("/admin/mcp?handler=Settings", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Enabled"] = "true",
                ["OpenRegistration"] = "true",
                ["__RequestVerificationToken"] = WebHostFixture.Antiforgery(again),
            }));
        }
    }
}
