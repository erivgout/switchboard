using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Api.Auth;
using Switchboard.Modules.Api.Data;

namespace Switchboard.Modules.Api.Http;

/// <summary>The scope a scoped API key needs to call an endpoint.</summary>
public sealed record ApiScopeMetadata(string Scope);

/// <summary>Marks endpoints only agents may call.</summary>
public sealed record ApiAgentOnlyMetadata;

/// <summary>Request body limit for an endpoint (SR-INP-7).</summary>
public sealed record ApiBodyLimitMetadata(long MaxBytes);

public static class ApiEndpointConventions
{
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ApiScopeMetadata(scope));

    public static TBuilder AgentsOnly<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ApiAgentOnlyMetadata());

    public static TBuilder BodyLimit<TBuilder>(this TBuilder builder, long maxBytes) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ApiBodyLimitMetadata(maxBytes));

    /// <summary>Filters every endpoint of this module's groups: body limit, problem details, scopes, idempotency.</summary>
    internal static RouteGroupBuilder WithApiConventions(this RouteGroupBuilder group) =>
        group
            .AddEndpointFilter<BodyLimitFilter>()
            .AddEndpointFilter<ApiExceptionFilter>()
            .AddEndpointFilter<ApiAccessFilter>()
            .AddEndpointFilter<IdempotencyFilter>();
}

/// <summary>Caps the request body before anything reads it.</summary>
internal sealed class BodyLimitFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var limit = http.GetEndpoint()?.Metadata.GetMetadata<ApiBodyLimitMetadata>()?.MaxBytes ?? ApiJson.MaxJsonBytes;
        if (http.Request.ContentLength > limit)
        {
            return ValueTask.FromResult<object?>(ApiProblems.Result(http, 413, ApiProblems.TitleFor(413), new PayloadTooLargeException(limit).Message));
        }
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = limit;
        }
        return next(context);
    }
}

/// <summary>
/// Scoped keys may only call endpoints whose declared scope they hold; endpoints without a declared
/// scope are closed to them. Agent-only endpoints reject requesters.
/// </summary>
internal sealed class ApiAccessFilter(IActorAccessor actors, IPermissionService permissions) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var metadata = http.GetEndpoint()?.Metadata;
        var actor = actors.Current;
        var scope = metadata?.GetMetadata<ApiScopeMetadata>()?.Scope;
        if (actor.Scopes is not null && (scope is null || !ApiScopes.Allows(actor, scope)))
        {
            return ApiProblems.Result(http, 403, ApiProblems.TitleFor(403),
                scope is null ? "This API key is limited to scopes that don't cover this endpoint." : $"This API key doesn't have the {scope} scope.");
        }
        if (metadata?.GetMetadata<ApiAgentOnlyMetadata>() is not null && (await permissions.CurrentAsync(http.RequestAborted))?.IsAgent != true)
        {
            return ApiProblems.Result(http, 403, ApiProblems.TitleFor(403), "Only agents can use this endpoint.");
        }
        return await next(context);
    }
}

/// <summary>
/// <c>Idempotency-Key</c> on POST (FR-INT-1): the first successful response is stored for 24 hours and
/// replayed for the same user, key and route. The same key with a different request gets 422.
/// </summary>
internal sealed class IdempotencyFilter(IServiceScopeFactory scopes, IActorAccessor actors, IPermissionService permissions, TimeProvider time) : IEndpointFilter
{
    public const string Header = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    /// <summary>A first attempt that never finished (crash) stops blocking retries after this long.</summary>
    private static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(5);
    private const int MaxStoredBytes = 1024 * 1024;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsPost(http.Request.Method) || !http.Request.Headers.TryGetValue(Header, out var values))
        {
            return await next(context);
        }
        var key = values.ToString().Trim();
        if (values.Count != 1 || key.Length is 0 or > 255 || key.Any(c => c is < '!' or > '~'))
        {
            return ApiProblems.Result(http, 400, ApiProblems.TitleFor(400), "Idempotency-Key must be 1 to 255 visible ASCII characters.");
        }
        if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return await next(context);
        }

        var route = $"POST {http.Request.Path.Value}".ToLowerInvariant();
        if (route.Length > 450)
        {
            route = route[..450];
        }
        var hash = await HashRequestAsync(http.Request, http.RequestAborted);
        var access = await HashAccessAsync(http.RequestAborted);
        var now = time.GetUtcNow();

        await using var scope = scopes.CreateAsyncScope();
        // Its own context: the request's context may hold half-applied changes after a failed handler.
        var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
        var records = db.Set<ApiIdempotencyRecord>();
        var existing = await records.FirstOrDefaultAsync(r => r.UserId == userId && r.Key == key && r.Route == route, http.RequestAborted);
        if (existing is not null && (existing.ExpiresAt <= now || (existing.StatusCode is null && existing.CreatedAt < now - Abandoned)))
        {
            records.Remove(existing);
            await db.SaveChangesAsync(http.RequestAborted);
            existing = null;
        }
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
            {
                return ApiProblems.Result(http, 422, ApiProblems.TitleFor(422), "This Idempotency-Key was already used with a different request. Use a new key for a new request.");
            }
            if (existing.StatusCode is not { } stored)
            {
                return ApiProblems.Result(http, 409, ApiProblems.TitleFor(409), "A request with this Idempotency-Key is still running. Retry in a moment.");
            }
            if (!string.Equals(existing.AccessHash, access, StringComparison.Ordinal))
            {
                // The stored response was shaped by permissions the caller no longer has exactly.
                return ApiProblems.Result(http, 409, ApiProblems.TitleFor(409), "Your access changed since the first request with this Idempotency-Key. Send it again with a new key.");
            }
            http.Response.Headers[ReplayedHeader] = "true";
            return new ReplayResult(stored, existing.ContentType, existing.Location, existing.ETag, existing.ResponseBody ?? []);
        }

        var record = new ApiIdempotencyRecord
        {
            UserId = userId,
            Key = key,
            Route = route,
            RequestHash = hash,
            AccessHash = access,
            CreatedAt = now,
            ExpiresAt = now + Retention,
        };
        records.Add(record);
        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException)
        {
            return ApiProblems.Result(http, 409, ApiProblems.TitleFor(409), "A request with this Idempotency-Key is still running. Retry in a moment.");
        }

        object? result;
        try
        {
            result = await next(context);
        }
        catch
        {
            await ForgetAsync(db, record);
            throw;
        }

        // Render the result into a buffer so it can be stored, then copy it to the client.
        var original = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try
        {
            if (result is IResult r)
            {
                await r.ExecuteAsync(http);
            }
            else if (result is not null)
            {
                await ApiJson.Ok(result).ExecuteAsync(http);
            }
        }
        catch
        {
            http.Response.Body = original;
            await ForgetAsync(db, record);
            throw;
        }
        http.Response.Body = original;

        var status = http.Response.StatusCode;
        if (status is >= 200 and < 300 && buffer.Length <= MaxStoredBytes)
        {
            record.StatusCode = status;
            record.ContentType = http.Response.ContentType;
            record.Location = http.Response.Headers.Location.ToString() is { Length: > 0 and <= 2000 } location ? location : null;
            record.ETag = http.Response.Headers.ETag.ToString() is { Length: > 0 and <= 200 } etag ? etag : null;
            record.ResponseBody = buffer.ToArray();
            await db.SaveChangesAsync(CancellationToken.None);
        }
        else
        {
            // Failed requests aren't stored: the client may fix the request and retry with the same key.
            await ForgetAsync(db, record);
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original, http.RequestAborted);
        return Results.Empty;
    }

    private static async Task ForgetAsync(SwitchboardDbContext db, ApiIdempotencyRecord record)
    {
        db.Set<ApiIdempotencyRecord>().Remove(record);
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException)
        {
            // Already gone.
        }
    }

    internal static async Task<string> HashRequestAsync(HttpRequest request, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(value));
            sha.AppendData("\n"u8);
        }

        Append(request.QueryString.Value ?? "");
        if (request.HasFormContentType)
        {
            // Retries rebuild multipart bodies with a new boundary, so hash what the form says, not its bytes.
            // The form is cached on the request; the handler reads the same one.
            var form = await request.ReadFormAsync(ct);
            foreach (var field in form.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                Append($"{field.Key}={field.Value}");
            }
            foreach (var file in form.Files.OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.FileName, StringComparer.Ordinal))
            {
                await using var content = file.OpenReadStream();
                Append($"{file.Name}:{file.FileName}:{file.ContentType}:{Convert.ToHexStringLower(await SHA256.HashDataAsync(content, ct))}");
            }
            return Convert.ToHexStringLower(sha.GetHashAndReset());
        }

        request.EnableBuffering();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            sha.AppendData(chunk, 0, read);
        }
        request.Body.Position = 0;
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Everything that decides what the caller may see: roles, ticket scope, groups, departments, key scopes.</summary>
    private async Task<string> HashAccessAsync(CancellationToken ct)
    {
        var p = await permissions.CurrentAsync(ct);
        var text = new StringBuilder();
        if (p is not null)
        {
            text.Append(p.IsOrgAdmin).Append('|').Append(p.IsAgent).Append('|');
            foreach (var w in p.Workspaces.Values.OrderBy(w => w.WorkspaceId))
            {
                text.Append(w.WorkspaceId).Append(':').Append(w.RoleId).Append(':').Append(w.Scope).Append(':')
                    .AppendJoin(',', w.Privileges.Order(StringComparer.Ordinal)).Append(';');
            }
            text.Append('|').AppendJoin(',', p.GroupIds.Order()).Append('|').AppendJoin(',', p.ObserverGroupIds.Order())
                .Append('|').AppendJoin(',', p.LeadDepartmentIds.Order());
        }
        text.Append("|scopes:").AppendJoin(' ', actors.Current.Scopes?.Order(StringComparer.Ordinal) ?? Enumerable.Empty<string>());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private sealed class ReplayResult(int status, string? contentType, string? location, string? etag, byte[] body) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = status;
            if (contentType is not null)
            {
                httpContext.Response.ContentType = contentType;
            }
            if (location is not null)
            {
                httpContext.Response.Headers.Location = location;
            }
            if (etag is not null)
            {
                httpContext.Response.Headers.ETag = etag;
            }
            httpContext.Response.ContentLength = body.Length;
            await httpContext.Response.Body.WriteAsync(body, httpContext.RequestAborted);
        }
    }
}
