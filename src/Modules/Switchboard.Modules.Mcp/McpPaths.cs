using Microsoft.AspNetCore.Http;

namespace Switchboard.Modules.Mcp;

/// <summary>URLs of the MCP endpoint and the built-in authorization server.</summary>
public static class McpPaths
{
    public const string Mcp = "/mcp";
    public const string Authorize = "/oauth/authorize";
    public const string Token = "/oauth/token";
    public const string Revoke = "/oauth/revoke";
    public const string Register = "/oauth/register";
    public const string ProtectedResourceMetadata = "/.well-known/oauth-protected-resource";
    public const string AgentActions = "/agent-actions";
    public const string Admin = "/admin/mcp";

    /// <summary>The endpoint's resource identifier (RFC 8707): <c>{base}/mcp</c> or <c>{base}/mcp/{slug}</c>.</summary>
    public static string Resource(string baseUrl, string slug) =>
        $"{baseUrl.TrimEnd('/')}{Mcp}{(slug.Length == 0 ? "" : "/" + slug)}";

    /// <summary>The configuration slug a request path addresses, or null when it isn't an MCP endpoint.</summary>
    public static string? SlugOf(PathString path)
    {
        if (!path.StartsWithSegments(Mcp, StringComparison.OrdinalIgnoreCase, out var rest))
        {
            return null;
        }
        var tail = rest.Value?.Trim('/') ?? "";
        return tail.Contains('/') ? null : tail.ToLowerInvariant();
    }

    /// <summary>A resource indicator the authorization server will issue tokens for, normalized; null if unknown.</summary>
    public static string? NormalizeResource(string baseUrl, string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return Resource(baseUrl, "");
        }
        if (!Uri.TryCreate(resource.Trim(), UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query))
        {
            return null;
        }
        var baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        if (!string.Equals(uri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var slug = SlugOf(new PathString(uri.AbsolutePath.TrimEnd('/')));
        return slug is null ? null : Resource(baseUrl, slug);
    }

    public static bool IsValidSlug(string slug) =>
        slug.Length is > 0 and <= 40 && slug.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') && slug[0] != '-';
}
