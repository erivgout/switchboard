using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Switchboard.Core.Security;

namespace Switchboard.Web.Hosting;

/// <summary>Strict security headers on every response (SR-INP-2).</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IOptions<ContentSecurityPolicyOptions> csp)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            if (!headers.ContainsKey("Content-Security-Policy"))
            {
                var o = csp.Value;
                headers["Content-Security-Policy"] = string.Join("; ",
                    "default-src 'self'",
                    "script-src 'self'",
                    "style-src 'self' 'unsafe-inline'",
                    $"img-src 'self' data: blob: https: {string.Join(' ', o.ImgSrc)}".TrimEnd(),
                    "font-src 'self'",
                    $"connect-src 'self' ws: wss: {string.Join(' ', o.ConnectSrc)}".TrimEnd(),
                    $"frame-src 'self' {string.Join(' ', o.FrameSrc)}".TrimEnd(),
                    "frame-ancestors 'none'",
                    $"form-action 'self' {string.Join(' ', o.FormAction)}".TrimEnd(),
                    "base-uri 'self'",
                    "object-src 'none'",
                    "manifest-src 'self'",
                    "worker-src 'self'");
            }
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            return Task.CompletedTask;
        });
        return next(context);
    }
}

/// <summary>
/// Maps domain exceptions to HTTP results. API, MCP and htmx callers get problem details;
/// page requests get the matching status page.
/// </summary>
public sealed class DomainExceptionMiddleware(RequestDelegate next, ILogger<DomainExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (Map(ex) is { } status && !context.Response.HasStarted)
        {
            if (status == StatusCodes.Status500InternalServerError)
            {
                throw;
            }
            logger.LogInformation("Request {Path} ended with {Status}: {Message}", context.Request.Path, status, ex.Message);
            context.Response.Clear();
            context.Response.StatusCode = status;
            var wantsProblem = context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/mcp")
                || context.Request.Headers.ContainsKey("HX-Request") || context.Request.Headers.Accept.ToString().Contains("application/json");
            if (wantsProblem)
            {
                var problem = new ProblemDetails
                {
                    Status = status,
                    Title = status switch
                    {
                        403 => "Forbidden",
                        404 => "Not found",
                        409 => "Conflict",
                        _ => "Invalid request",
                    },
                    Detail = ex.Message,
                };
                if (ex is ValidationException v)
                {
                    problem.Extensions["errors"] = v.Errors;
                }
                await context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
                return;
            }
            context.Items["sb.error.message"] = ex.Message;
            // Status code pages re-execute /status/{code}.
        }
    }

    private static int? Map(Exception ex) => ex switch
    {
        NotFoundException => StatusCodes.Status404NotFound,
        ForbiddenException => StatusCodes.Status403Forbidden,
        ConflictException => StatusCodes.Status409Conflict,
        ValidationException => StatusCodes.Status400BadRequest,
        _ => null,
    };
}
