using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Api.Auth;
using Switchboard.Testing;

namespace Switchboard.Modules.Api.Tests;

/// <summary>Personal API keys (FR-IAM-20, SR-AUTHN-7): who gets in, who gets 401, and what the principal looks like.</summary>
[Collection(ApiCollection.Name)]
public sealed class ApiKeyAuthenticationTests(ApiFixture f)
{
    [Fact]
    public async Task Valid_key_authenticates_as_its_owner_through_the_api_channel()
    {
        var key = await f.CreateKeyAsync(f.Agent);
        Assert.Matches("^sb_[0-9A-Za-z]{43}$", key);

        var response = await f.SendAsync(HttpMethod.Get, "/api/v1/me", key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await ApiFixture.JsonAsync(response);
        Assert.Equal(f.Agent.Id, me.GetProperty("id").GetGuid());
        Assert.True(me.GetProperty("isAgent").GetBoolean());
        Assert.False(me.TryGetProperty("scopes", out _));
    }

    [Fact]
    public async Task Keys_are_stored_hashed_with_a_display_prefix()
    {
        var key = await f.CreateKeyAsync(f.Agent, "Hash check");
        var stored = await f.InDbAsync(db => db.ApiKeys.AsNoTracking().FirstAsync(k => k.Name == "Hash check"));
        Assert.Equal(ApiKeyFormat.Hash(key), stored.KeyHash);
        Assert.DoesNotContain(key, stored.KeyHash);
        Assert.Equal(key[..11], stored.Prefix);
    }

    [Fact]
    public async Task Basic_auth_with_the_key_as_user_name_works()
    {
        var key = await f.CreateKeyAsync(f.Agent);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key}:X")));
        Assert.Equal(HttpStatusCode.OK, (await f.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Missing_key_gets_401_problem_json_with_a_bearer_challenge_and_no_redirect()
    {
        var response = await f.SendAsync(HttpMethod.Get, "/api/v1/tickets", key: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Null(response.Headers.Location);
        Assert.Equal(401, (await ApiFixture.JsonAsync(response)).GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("xx_0123456789012345678901234567890123456789012")]   // wrong prefix
    [InlineData("sb_short")]                                       // right prefix, malformed
    [InlineData("sb_0123456789012345678901234567890123456789012")] // well formed, unknown
    [InlineData("sb_01234567890123456789012345678901234567890!$")]  // bad characters
    public async Task Unknown_or_malformed_keys_get_401(string key)
    {
        var response = await f.SendAsync(HttpMethod.Get, "/api/v1/me", key);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoked_key_gets_401()
    {
        var key = await f.CreateKeyAsync(f.Agent, "To revoke");
        Assert.Equal(HttpStatusCode.OK, (await f.SendAsync(HttpMethod.Get, "/api/v1/me", key)).StatusCode);
        var id = await f.InDbAsync(db => db.ApiKeys.Where(k => k.KeyHash == ApiKeyFormat.Hash(key)).Select(k => k.Id).FirstAsync());

        await f.Host.RunAsync(SwitchboardTestHost.As(f.Agent), sp => sp.GetRequiredService<IApiKeyService>().RevokeAsync(id));

        Assert.Equal(HttpStatusCode.Unauthorized, (await f.SendAsync(HttpMethod.Get, "/api/v1/me", key)).StatusCode);
    }

    [Fact]
    public async Task Expired_key_gets_401()
    {
        var key = await f.CreateKeyAsync(f.Agent, "To expire");
        await f.InDbAsync(db => db.ApiKeys.Where(k => k.KeyHash == ApiKeyFormat.Hash(key))
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.SendAsync(HttpMethod.Get, "/api/v1/me", key)).StatusCode);
    }

    [Fact]
    public async Task Deactivated_users_keys_get_401()
    {
        var user = await f.Host.CreateAgentAsync("Leaving Agent");
        var key = await f.CreateKeyAsync(user);
        Assert.Equal(HttpStatusCode.OK, (await f.SendAsync(HttpMethod.Get, "/api/v1/me", key)).StatusCode);

        await f.InDbAsync(db => db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await f.SendAsync(HttpMethod.Get, "/api/v1/me", key)).StatusCode);
    }

    [Fact]
    public async Task Last_use_is_recorded()
    {
        var key = await f.CreateKeyAsync(f.Agent, "Last used");
        await f.SendAsync(HttpMethod.Get, "/api/v1/me", key);
        var used = await f.InDbAsync(db => db.ApiKeys.Where(k => k.KeyHash == ApiKeyFormat.Hash(key)).Select(k => k.LastUsedAt).FirstAsync());
        Assert.NotNull(used);
    }

    [Fact]
    public async Task Create_and_revoke_are_audited()
    {
        var key = await f.CreateKeyAsync(f.Agent, "Audited");
        var id = await f.InDbAsync(db => db.ApiKeys.Where(k => k.KeyHash == ApiKeyFormat.Hash(key)).Select(k => k.Id).FirstAsync());
        await f.Host.RunAsync(SwitchboardTestHost.As(f.Agent), sp => sp.GetRequiredService<IApiKeyService>().RevokeAsync(id));

        var actions = await f.InDbAsync(db => db.AuditEntries.Where(a => a.ObjectId == id.ToString()).Select(a => a.Action).ToListAsync());
        Assert.Contains("api_keys.created", actions);
        Assert.Contains("api_keys.revoked", actions);
        var details = await f.InDbAsync(db => db.AuditEntries.Where(a => a.ObjectId == id.ToString()).Select(a => a.DetailsJson).ToListAsync());
        Assert.All(details, d => Assert.DoesNotContain(key, d));
    }

    [Fact]
    public async Task People_cannot_revoke_or_see_someone_elses_key()
    {
        var key = await f.CreateKeyAsync(f.Agent, "Not yours");
        var id = await f.InDbAsync(db => db.ApiKeys.Where(k => k.KeyHash == ApiKeyFormat.Hash(key)).Select(k => k.Id).FirstAsync());

        await Assert.ThrowsAsync<NotFoundException>(() =>
            f.Host.RunAsync(SwitchboardTestHost.As(f.Requester, Channel.Portal), sp => sp.GetRequiredService<IApiKeyService>().RevokeAsync(id)));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            f.Host.RunAsync(SwitchboardTestHost.As(f.Agent), sp => sp.GetRequiredService<IApiKeyService>().ListAllAsync(null, null, 1, 50)));

        // Admins with settings.manage can.
        await f.Host.RunAsync(SwitchboardTestHost.As(f.Admin), sp => sp.GetRequiredService<IApiKeyService>().RevokeAsync(id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.SendAsync(HttpMethod.Get, "/api/v1/me", key)).StatusCode);
    }

    [Fact]
    public async Task A_key_cannot_mint_keys()
    {
        var actor = SwitchboardTestHost.As(f.Agent, Channel.Api);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            f.Host.RunAsync(actor, sp => sp.GetRequiredService<IApiKeyService>().CreateAsync(new CreateApiKeyRequest("Child", null, 30))));
    }

    [Fact]
    public async Task Admin_lifetime_limit_applies_to_new_keys()
    {
        await f.Host.RunAsync(SwitchboardTestHost.As(f.Admin), sp => sp.GetRequiredService<IApiKeyService>().SetMaxLifetimeDaysAsync(60));
        try
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(() => f.CreateKeyAsync(f.Agent, "Too long", days: null));
            Assert.Contains("expiresInDays", ex.Errors.Keys);
            await Assert.ThrowsAsync<ValidationException>(() => f.CreateKeyAsync(f.Agent, "Too long", days: 90));
            Assert.StartsWith("sb_", await f.CreateKeyAsync(f.Agent, "Fits", days: 30));
        }
        finally
        {
            await f.Host.RunAsync(SwitchboardTestHost.As(f.Admin), sp => sp.GetRequiredService<IApiKeyService>().SetMaxLifetimeDaysAsync(null));
        }
    }

    [Fact]
    public async Task Browser_cookies_are_not_api_credentials()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Add("Cookie", "sb.auth=anything");
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Unknown_api_routes_get_problem_json_404_for_callers_with_a_key()
    {
        var key = await f.CreateKeyAsync(f.Agent);
        var response = await f.SendAsync(HttpMethod.Get, "/api/v1/no-such-thing", key);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.SendAsync(HttpMethod.Get, "/api/v1/no-such-thing", null)).StatusCode);
    }

    [Fact]
    public async Task Users_actor_from_a_key_is_on_the_api_channel()
    {
        var key = await f.CreateKeyAsync(f.Agent);
        var id = await f.CreateTicketAsync(key, new { subject = "Channel check" });
        var entry = await f.InDbAsync(db => db.ActivityEntries.AsNoTracking().FirstAsync(a => a.SubjectId == id && a.Action == "created"));
        Assert.Equal(Channel.Api, entry.Channel);
        Assert.Equal(f.Agent.Id, entry.ActorUserId);
    }
}
