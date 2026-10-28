using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Services;

namespace Switchboard.Modules.Mcp.Server;

/// <summary>
/// Runs first in the pipeline: refuses /mcp requests from unknown browser origins (MCP-5, DNS-rebinding
/// protection), answers CORS for allowed origins, and opens the public OAuth endpoints to browser-based
/// clients (no cookies are involved there, so a wildcard origin is safe).
/// </summary>
internal sealed class McpEdgeStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<McpEdgeMiddleware>();
        next(app);
    };
}

internal sealed class McpEdgeMiddleware(RequestDelegate next, IOptions<SwitchboardOptions> options)
{
    private const string AllowHeaders = "Authorization, Content-Type, Accept, Mcp-Session-Id, Mcp-Protocol-Version, Mcp-Method, Mcp-Name, Last-Event-ID";
    private const string ExposeHeaders = "Mcp-Session-Id, WWW-Authenticate";

    private static readonly string[] PublicOAuthPaths =
    [
        "/.well-known/oauth-protected-resource",
        "/.well-known/oauth-authorization-server",
        "/.well-known/openid-configuration",
        "/.well-known/jwks",
        McpPaths.Token,
        McpPaths.Register,
        McpPaths.Revoke,
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (McpPaths.SlugOf(path) is not null)
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0)
            {
                if (!await IsAllowedOriginAsync(context, origin))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        error = new { code = -32600, message = "Requests from this origin aren't allowed." },
                    });
                    return;
                }
                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.Vary = "Origin";
                context.Response.Headers.AccessControlExposeHeaders = ExposeHeaders;
                if (HttpMethods.IsOptions(context.Request.Method))
                {
                    Preflight(context, "GET, POST, DELETE");
                    return;
                }
            }
        }
        else if (PublicOAuthPaths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.Headers.AccessControlAllowOrigin = "*";
            context.Response.Headers.AccessControlExposeHeaders = "WWW-Authenticate";
            if (HttpMethods.IsOptions(context.Request.Method) && context.Request.Headers.ContainsKey("Access-Control-Request-Method"))
            {
                Preflight(context, "GET, POST");
                return;
            }
        }

        await next(context);
    }

    private static void Preflight(HttpContext context, string methods)
    {
        context.Response.Headers.AccessControlAllowMethods = methods;
        context.Response.Headers.AccessControlAllowHeaders = AllowHeaders;
        context.Response.Headers.AccessControlMaxAge = "600";
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task<bool> IsAllowedOriginAsync(HttpContext context, string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }
        var own = new Uri(options.Value.BaseUrl);
        if (SameOrigin(originUri, own))
        {
            return true;
        }
        var settings = context.RequestServices.GetRequiredService<ISettingsService>();
        var allowed = await settings.GetAsync<string[]>(McpSettings.AllowedOrigins, [], ct: context.RequestAborted);
        return allowed.Any(a => Uri.TryCreate(a, UriKind.Absolute, out var u) && SameOrigin(originUri, u));
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}
