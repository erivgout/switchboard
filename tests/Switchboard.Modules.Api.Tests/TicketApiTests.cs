using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;

namespace Switchboard.Modules.Api.Tests;

/// <summary>Ticket endpoints: requester visibility, idempotency, ETags, PATCH and paging.</summary>
[Collection(ApiCollection.Name)]
public sealed class TicketApiTests(ApiFixture f)
{
    // ---------------------------------------------------------------- requester visibility (INV-TKT-7, SR-AUTHZ-7)

    [Fact]
    public async Task Requesters_never_receive_private_notes_forwards_or_bcc()
    {
        var agent = await f.CreateKeyAsync(f.Agent);
        var requester = await f.CreateKeyAsync(f.Requester);
        var id = await f.CreateTicketAsync(requester, new { subject = "VPN drops", descriptionText = "Every hour" });

        Assert.Equal(HttpStatusCode.Created, (await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/notes", agent, new { bodyText = "PRIVATE-NOTE" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/forward", agent, new { to = new[] { "vendor@example.com" }, bodyText = "FORWARDED" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/reply", agent, new { bodyText = "PUBLIC-REPLY", bcc = new[] { "hidden-bcc@example.com" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/notes", agent, new { bodyText = "PUBLIC-NOTE", isPrivate = false })).StatusCode);

        foreach (var url in new[] { $"/api/v1/tickets/{id}?include=conversations", $"/api/v1/tickets/{id}/conversations" })
        {
            var text = await (await f.SendAsync(HttpMethod.Get, url, requester)).Content.ReadAsStringAsync();
            Assert.Contains("PUBLIC-REPLY", text);
            Assert.Contains("PUBLIC-NOTE", text);
            Assert.DoesNotContain("PRIVATE-NOTE", text);
            Assert.DoesNotContain("FORWARDED", text);
            Assert.DoesNotContain("hidden-bcc", text);
            Assert.DoesNotContain("vendor@example.com", text);
        }

        // An agent asking as a requester only sees tickets they could see in the portal.
        Assert.Equal(HttpStatusCode.NotFound, (await f.SendAsync(HttpMethod.Get, $"/api/v1/tickets/{id}/conversations?as=requester", agent)).StatusCode);

        var agentView = await (await f.SendAsync(HttpMethod.Get, $"/api/v1/tickets/{id}/conversations", agent)).Content.ReadAsStringAsync();
        Assert.Contains("PRIVATE-NOTE", agentView);
    }

    [Fact]
    public async Task Requester_ticket_view_has_no_internal_fields()
    {
        var requester = await f.CreateKeyAsync(f.Requester);
        var id = await f.CreateTicketAsync(requester, new { subject = "Fields check" });
        var json = await ApiFixture.JsonAsync(await f.SendAsync(HttpMethod.Get, $"/api/v1/tickets/{id}", requester));
        foreach (var internalField in new[] { "group", "agent", "priority", "tags", "isSpam", "dueBy", "source", "workspaceId" })
        {
            Assert.False(json.TryGetProperty(internalField, out _), internalField);
        }
        Assert.True(json.GetProperty("status").TryGetProperty("label", out _));
        Assert.False(json.GetProperty("status").TryGetProperty("id", out _));
    }

    [Fact]
    public async Task Attachments_on_private_notes_are_not_downloadable_by_the_requester()
    {
        var agent = await f.CreateKeyAsync(f.Agent);
        var requester = await f.CreateKeyAsync(f.Requester);
        var id = await f.CreateTicketAsync(requester, new { subject = "Attachment visibility" });

        var upload = new MultipartFormDataContent { { new ByteArrayContent("internal diagram"u8.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") } }, "file", "diagram.txt" } };
        var request = f.Request(HttpMethod.Post, "/api/v1/attachments", agent);
        request.Content = upload;
        var uploaded = await f.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var attachmentId = (await ApiFixture.JsonAsync(uploaded)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Created, (await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/notes", agent, new { bodyText = "See attached", attachmentIds = new[] { attachmentId } })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await f.SendAsync(HttpMethod.Get, $"/api/v1/attachments/{attachmentId}/content", agent)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.SendAsync(HttpMethod.Get, $"/api/v1/attachments/{attachmentId}/content", requester)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.SendAsync(HttpMethod.Get, $"/api/v1/attachments/{attachmentId}", requester)).StatusCode);
    }

    [Fact]
    public async Task Hidden_and_missing_tickets_get_the_same_404()
    {
        var other = await f.CreateKeyAsync(f.OtherRequester);
        var mine = await f.CreateKeyAsync(f.Requester);
        var id = await f.CreateTicketAsync(mine, new { subject = "Not for Olive" });

        var hidden = await f.SendAsync(HttpMethod.Get, $"/api/v1/tickets/{id}", other);
        var missing = await f.SendAsync(HttpMethod.Get, $"/api/v1/tickets/{Guid.NewGuid()}", other);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal((await ApiFixture.JsonAsync(missing)).Str("detail"), (await ApiFixture.JsonAsync(hidden)).Str("detail"));
        Assert.Equal(HttpStatusCode.NotFound, (await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/messages", other, new { bodyText = "sneaky" })).StatusCode);
    }

    [Fact]
    public async Task Requesters_cannot_set_agent_fields_or_use_agent_endpoints()
    {
        var requester = await f.CreateKeyAsync(f.Requester);
        var response = await f.SendAsync(HttpMethod.Post, "/api/v1/tickets", requester, new { subject = "Make it urgent", priority = "urgent" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ApiFixture.JsonAsync(response)).GetProperty("errors").TryGetProperty("priority", out _));

        var id = await f.CreateTicketAsync(requester, new { subject = "Agent endpoints" });
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Patch, $"/api/v1/tickets/{id}", requester, new { subject = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/notes", requester, new { bodyText = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Get, "/api/v1/groups", requester)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/messages", requester, new { bodyText = "More detail" })).StatusCode);
    }

    // ---------------------------------------------------------------- idempotency (FR-INT-1)

    [Fact]
    public async Task Same_idempotency_key_replays_the_first_response_without_creating_twice()
    {
        var key = await f.CreateKeyAsync(f.Agent);
        var body = new { subject = "Idempotent create " + Guid.NewGuid() };

        async Task<HttpResponseMessage> Post(object b)
        {
            var request = f.Request(HttpMethod.Post, "/api/v1/tickets", key, b);
            request.Headers.Add("Idempotency-Key", "create-once");
            return await f.Client.SendAsync(request);
        }

        var first = await Post(body);
        var second = await Post(body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal((await ApiFixture.JsonAsync(first)).Str("id"), (await ApiFixture.JsonAsync(second)).Str("id"));
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.NotNull(first.Headers.ETag);
        Assert.Equal(first.Headers.ETag, second.Headers.ETag);
        Assert.Equal(1, await f.InDbAsync(db => db.Tickets.CountAsync(t => t.Subject == body.subject)));

        var mismatch = await Post(new { subject = "Something else" });
        Assert.Equal((HttpStatusCode)422, mismatch.StatusCode);
        Assert.Equal("application/problem+json", mismatch.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Replay_is_refused_once_the_callers_access_has_changed()
    {
        var agent = await f.Host.CreateAgentAsync("Moving Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        var key = await f.CreateKeyAsync(agent);
        async Task<HttpResponseMessage> Post()
        {
            var request = f.Request(HttpMethod.Post, "/api/v1/tickets", key, new { subject = "Before the move" });
            request.Headers.Add("Idempotency-Key", "move-1");
            return await f.Client.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.Created, (await Post()).StatusCode);
        await f.InDbAsync(db => db.AgentGroupMembers.Where(m => m.UserId == agent.Id).ExecuteDeleteAsync());

        var replay = await Post();
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.False(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(1, await f.InDbAsync(db => db.Tickets.CountAsync(t => t.Subject == "Before the move" && t.RequesterId == agent.Id)));
    }

    [Fact]
    public async Task Idempotency_keys_are_per_user_and_failed_requests_are_not_stored()
    {
        var sam = await f.CreateKeyAsync(f.Agent);
        var admin = await f.CreateKeyAsync(f.Admin);

        async Task<HttpResponseMessage> Post(string key, object b)
        {
            var request = f.Request(HttpMethod.Post, "/api/v1/tickets", key, b);
            request.Headers.Add("Idempotency-Key", "shared-key-name");
            return await f.Client.SendAsync(request);
        }

        // A validation failure isn't remembered: fixing the body and retrying with the same key works.
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(sam, new { subject = "" })).StatusCode);
        var fixedUp = await Post(sam, new { subject = "Fixed after 400" });
        Assert.Equal(HttpStatusCode.Created, fixedUp.StatusCode);
        Assert.False(fixedUp.Headers.Contains("Idempotent-Replayed"));

        // Another user with the same key name gets their own request.
        var other = await Post(admin, new { subject = "Fixed after 400" });
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.NotEqual((await ApiFixture.JsonAsync(fixedUp)).Str("id"), (await ApiFixture.JsonAsync(other)).Str("id"));
    }

    // ---------------------------------------------------------------- ETag / If-Match

    [Fact]
    public async Task Patch_with_a_stale_etag_gets_412_and_changes_nothing()
    {
        var key = await f.CreateKeyAsync(f.Agent);
        var id = await f.CreateTicketAsync(key, new { subject = "ETag ticket" });

        var get = await f.SendAsync(HttpMethod.Get, $"/api/v1/tickets/{id}", key);
        var etag = get.Headers.ETag;
        Assert.NotNull(etag);
        Assert.False(etag!.IsWeak);

        var first = f.Request(HttpMethod.Patch, $"/api/v1/tickets/{id}", key, new { priority = "high" });
        first.Headers.IfMatch.Add(etag);
        var ok = await f.Client.SendAsync(first);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.NotEqual(etag, ok.Headers.ETag);

        var stale = f.Request(HttpMethod.Patch, $"/api/v1/tickets/{id}", key, new { priority = "low" });
        stale.Headers.IfMatch.Add(etag);
        var conflict = await f.Client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.PreconditionFailed, conflict.StatusCode);
        Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType?.MediaType);

        var current = await ApiFixture.JsonAsync(await f.SendAsync(HttpMethod.Get, $"/api/v1/tickets/{id}", key));
        Assert.Equal("high", current.Str("priority"));

        var garbage = f.Request(HttpMethod.Patch, $"/api/v1/tickets/{id}", key, new { priority = "low" });
        garbage.Headers.TryAddWithoutValidation("If-Match", "\"not-base64!\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await f.Client.SendAsync(garbage)).StatusCode);

        var any = f.Request(HttpMethod.Patch, $"/api/v1/tickets/{id}", key, new { priority = "medium" });
        any.Headers.TryAddWithoutValidation("If-Match", "*");
        Assert.Equal(HttpStatusCode.OK, (await f.Client.SendAsync(any)).StatusCode);
    }

    // ---------------------------------------------------------------- PATCH mapping

    [Fact]
    public async Task Patch_rejects_unknown_read_only_and_mistyped_fields_with_field_errors()
    {
        var key = await f.CreateKeyAsync(f.Agent);
        var id = await f.CreateTicketAsync(key, new { subject = "Patch rules" });

        var response = await f.SendAsync(HttpMethod.Patch, $"/api/v1/tickets/{id}", key,
            """{ "subject": "ok", "colour": "blue", "number": 7, "priority": 4, "groupId": "nope", "dueBy": "tomorrow" }""");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await ApiFixture.JsonAsync(response)).GetProperty("errors");
        foreach (var field in new[] { "colour", "number", "priority", "groupId", "dueBy" })
        {
            Assert.True(errors.TryGetProperty(field, out _), field);
        }
        Assert.False(errors.TryGetProperty("subject", out _));

        var unchanged = await ApiFixture.JsonAsync(await f.SendAsync(HttpMethod.Get, $"/api/v1/tickets/{id}", key));
        Assert.Equal("Patch rules", unchanged.Str("subject"));
    }

    [Fact]
    public async Task Patch_applies_only_sent_fields_and_null_clears()
    {
        var key = await f.CreateKeyAsync(f.Agent);
        var group = await f.InDbAsync(db => db.AgentGroups.Where(g => g.Name == "Service Desk").Select(g => g.Id).FirstAsync());
        var id = await f.CreateTicketAsync(key, new { subject = "Partial", groupId = group, tags = new[] { "keep" } });

        var response = await f.SendAsync(HttpMethod.Patch, $"/api/v1/tickets/{id}", key, """{ "groupId": null, "type": "serviceRequest" }""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ticket = await ApiFixture.JsonAsync(response);
        Assert.False(ticket.TryGetProperty("group", out _));
        Assert.Equal("serviceRequest", ticket.Str("type"));
        Assert.Equal("Partial", ticket.Str("subject"));
        Assert.Equal("keep", ticket.GetProperty("tags")[0].GetString());
    }

    [Fact]
    public async Task Create_rejects_unknown_fields()
    {
        var key = await f.CreateKeyAsync(f.Agent);
        var response = await f.SendAsync(HttpMethod.Post, "/api/v1/tickets", key, """{ "subject": "x", "surprise": true }""");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ApiFixture.JsonAsync(response)).GetProperty("errors").TryGetProperty("surprise", out _));
    }

    [Fact]
    public async Task Oversized_bodies_get_413()
    {
        var key = await f.CreateKeyAsync(f.Agent);
        var huge = JsonSerializer.Serialize(new { subject = "big", descriptionHtml = new string('x', 2 * 1024 * 1024 + 10) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await f.SendAsync(HttpMethod.Post, "/api/v1/tickets", key, huge)).StatusCode);
    }

    // ---------------------------------------------------------------- list, paging, lifecycle

    [Fact]
    public async Task List_pages_with_an_opaque_cursor_and_filters_with_the_query_language()
    {
        var key = await f.CreateKeyAsync(f.Agent);
        var tag = "page" + Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 3; i++)
        {
            await f.CreateTicketAsync(key, new { subject = $"Paged {i}", tags = new[] { tag } });
        }

        var first = await f.SendAsync(HttpMethod.Get, $"/api/v1/tickets?q=tag:{tag}&per_page=2&sort=number", key);
        var page1 = await ApiFixture.JsonAsync(first);
        Assert.Equal(2, page1.GetProperty("data").GetArrayLength());
        Assert.Equal(3, page1.GetProperty("meta").GetProperty("total").GetInt32());
        var cursor = page1.GetProperty("meta").Str("nextCursor");
        Assert.Contains("rel=\"next\"", first.Headers.GetValues("Link").Single());

        var page2 = await ApiFixture.JsonAsync(await f.SendAsync(HttpMethod.Get, $"/api/v1/tickets?cursor={cursor}", key));
        Assert.Single(page2.GetProperty("data").EnumerateArray());
        Assert.False(page2.GetProperty("meta").TryGetProperty("nextCursor", out _));
        Assert.Equal("Paged 2", page2.GetProperty("data")[0].Str("subject"));

        Assert.Equal(HttpStatusCode.BadRequest, (await f.SendAsync(HttpMethod.Get, "/api/v1/tickets?cursor=garbage", key)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.SendAsync(HttpMethod.Get, "/api/v1/tickets?per_page=1000", key)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.SendAsync(HttpMethod.Get, "/api/v1/tickets?q=%28%28%28", key)).StatusCode);
    }

    [Fact]
    public async Task Trash_restore_spam_merge_links_children_and_tasks()
    {
        // Trash and spam need tickets.delete, which the plain agent role doesn't have.
        var agentKey = await f.CreateKeyAsync(f.Agent);
        var key = await f.CreateKeyAsync(f.Admin);
        var a = await f.CreateTicketAsync(key, new { subject = "Primary" });
        var b = await f.CreateTicketAsync(key, new { subject = "Duplicate" });

        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Delete, $"/api/v1/tickets/{b}", agentKey)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await f.SendAsync(HttpMethod.Delete, $"/api/v1/tickets/{b}", key)).StatusCode);
        Assert.NotNull(await f.InDbAsync(db => db.Tickets.Where(t => t.Id == b).Select(t => t.DeletedAt).FirstAsync()));
        Assert.Equal(HttpStatusCode.OK, (await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{b}/restore", key)).StatusCode);
        Assert.True((await ApiFixture.JsonAsync(await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{b}/spam", key))).GetProperty("isSpam").GetBoolean());
        Assert.False((await ApiFixture.JsonAsync(await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{b}/unspam", key))).GetProperty("isSpam").GetBoolean());

        var link = await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{a}/links", key, new { targetType = "ticket", targetId = b, linkType = "related" });
        Assert.Equal(HttpStatusCode.Created, link.StatusCode);
        var linkId = (await ApiFixture.JsonAsync(link)).Str("id");
        Assert.Equal(HttpStatusCode.NotFound, (await f.SendAsync(HttpMethod.Delete, $"/api/v1/tickets/{b}/links/{linkId}", key)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await f.SendAsync(HttpMethod.Delete, $"/api/v1/tickets/{a}/links/{linkId}", key)).StatusCode);

        var child = await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{a}/children", key, new { subject = "Child" });
        Assert.Equal(HttpStatusCode.Created, child.StatusCode);
        Assert.Equal(a.ToString(), (await ApiFixture.JsonAsync(child)).Str("parentId"));
        Assert.Single((await ApiFixture.JsonAsync(await f.SendAsync(HttpMethod.Get, $"/api/v1/tickets/{a}/children", key))).GetProperty("data").EnumerateArray());

        var task = await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{a}/tasks", key, new { title = "Call vendor" });
        Assert.Equal(HttpStatusCode.Created, task.StatusCode);
        var taskId = (await ApiFixture.JsonAsync(task)).Str("id");
        var done = await f.SendAsync(HttpMethod.Patch, $"/api/v1/tickets/{a}/tasks/{taskId}", key, new { status = "completed" });
        Assert.Equal("completed", (await ApiFixture.JsonAsync(done)).Str("status"));
        Assert.Equal(HttpStatusCode.BadRequest, (await f.SendAsync(HttpMethod.Patch, $"/api/v1/tickets/{a}/tasks/{taskId}", key, new { priority = "high" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.SendAsync(HttpMethod.Patch, $"/api/v1/tickets/{b}/tasks/{taskId}", key, new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await f.SendAsync(HttpMethod.Delete, $"/api/v1/tickets/{a}/tasks/{taskId}", key)).StatusCode);

        var merged = await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{a}/merge", key, new { ticketIds = new[] { b } });
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        Assert.Equal(a, await f.InDbAsync(db => db.Tickets.Where(t => t.Id == b).Select(t => t.MergedIntoId).FirstAsync()));
    }

    [Fact]
    public async Task Enums_are_camel_case_strings()
    {
        var key = await f.CreateKeyAsync(f.Agent);
        var id = await f.CreateTicketAsync(key, new { subject = "Enum check", type = "serviceRequest", priority = "urgent" });
        var ticket = await ApiFixture.JsonAsync(await f.SendAsync(HttpMethod.Get, $"/api/v1/tickets/{id}", key));
        Assert.Equal("serviceRequest", ticket.Str("type"));
        Assert.Equal("urgent", ticket.Str("priority"));
        Assert.Equal("open", ticket.GetProperty("status").Str("category"));
        Assert.Equal(HttpStatusCode.BadRequest, (await f.SendAsync(HttpMethod.Post, "/api/v1/tickets", key, """{ "subject": "n", "priority": 4 }""")).StatusCode);
    }

    [Fact]
    public async Task Openapi_document_covers_the_api_and_describes_the_key_scheme()
    {
        var response = await f.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await ApiFixture.JsonAsync(response);
        Assert.StartsWith("3.1", doc.Str("openapi"));
        var paths = doc.GetProperty("paths");
        foreach (var path in new[] { "/api/v1/tickets", "/api/v1/tickets/{id}", "/api/v1/tickets/{id}/conversations", "/api/v1/attachments", "/api/v1/requesters", "/api/v1/ticket-fields" })
        {
            Assert.True(paths.TryGetProperty(path, out _), path);
        }
        Assert.All(paths.EnumerateObject(), p => Assert.StartsWith("/api/v1/", p.Name));
        var schemes = doc.GetProperty("components").GetProperty("securitySchemes");
        Assert.Equal("bearer", schemes.GetProperty("apiKey").Str("scheme"));
        Assert.True(doc.GetProperty("security").GetArrayLength() > 0);
    }
}
