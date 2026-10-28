using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Switchboard.Core.Security;

namespace Switchboard.Modules.Api.Http;

/// <summary>Builds RFC 9457 problem responses for the API.</summary>
public static class ApiProblems
{
    public const string ContentType = "application/problem+json";

    public static ProblemDetails Create(HttpContext? http, int status, string title, string? detail, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        if (errors is { Count: > 0 })
        {
            problem.Extensions["errors"] = errors;
        }
        var traceId = Activity.Current?.Id ?? http?.TraceIdentifier;
        if (traceId is not null)
        {
            problem.Extensions["traceId"] = traceId;
        }
        return problem;
    }

    public static IResult Result(HttpContext? http, int status, string title, string? detail, IReadOnlyDictionary<string, string[]>? errors = null) =>
        Results.Json(Create(http, status, title, detail, errors), ApiJson.Options, ContentType, status);

    public static Task WriteAsync(HttpContext http, int status, string title, string? detail, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(Create(http, status, title, detail, errors), ApiJson.Options, ContentType, http.RequestAborted);
    }

    public static IResult NotFound(HttpContext http) => Result(http, 404, "Not found", new NotFoundException().Message);

    public static IResult Forbidden(HttpContext http, string? detail = null) =>
        Result(http, 403, "Forbidden", detail ?? new ForbiddenException().Message);

    public static string TitleFor(int status) => status switch
    {
        400 => "Invalid request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not found",
        405 => "Method not allowed",
        409 => "Conflict",
        412 => "Precondition failed",
        413 => "Payload too large",
        415 => "Unsupported media type",
        422 => "Unprocessable content",
        428 => "Precondition required",
        429 => "Too many requests",
        _ => status >= 500 ? "Server error" : "Request failed",
    };

    /// <summary>Maps domain exceptions to problem responses. Returns null for exceptions that are real errors.</summary>
    public static IResult? FromException(HttpContext http, Exception ex) => ex switch
    {
        ValidationException v => Result(http, 400, TitleFor(400), v.Message, v.Errors),
        NotFoundException n => Result(http, 404, TitleFor(404), n.Message),
        ForbiddenException f => Result(http, 403, TitleFor(403), f.Message),
        // If-Match was sent and the record moved on: 412. A lost race without If-Match: 409.
        ConflictException c => http.Request.Headers.IfMatch.Count > 0
            ? Result(http, 412, TitleFor(412), "The ticket changed since you read it. Get it again and retry with the new ETag.")
            : Result(http, 409, TitleFor(409), c.Message),
        PayloadTooLargeException p => Result(http, 413, TitleFor(413), p.Message),
        UnsupportedMediaTypeException u => Result(http, 415, TitleFor(415), u.Message),
        BadHttpRequestException b => Result(http, b.StatusCode, TitleFor(b.StatusCode), b.Message),
        JsonException => Result(http, 400, TitleFor(400), "The JSON is malformed."),
        _ => null,
    };
}

/// <summary>Turns domain exceptions thrown by API handlers into problem+json (SR-AUTHZ-6: one 404 for missing and hidden).</summary>
internal sealed class ApiExceptionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (Exception ex) when (ApiProblems.FromException(context.HttpContext, ex) is { } problem)
        {
            context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Switchboard.Api")
                .LogInformation("API {Method} {Path} ended with {Type}: {Message}", context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path, ex.GetType().Name, ex.Message);
            return problem;
        }
    }
}
