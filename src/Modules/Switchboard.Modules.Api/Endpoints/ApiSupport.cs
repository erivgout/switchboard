using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Api.Http;

namespace Switchboard.Modules.Api.Endpoints;

/// <summary>Strong ETags derived from the ticket's row version.</summary>
public static class ETags
{
    public static string Format(byte[] rowVersion) => $"\"{Base64Url.EncodeToString(rowVersion)}\"";

    /// <summary>Parses a single strong ETag or <c>*</c> (null: any version). False when it can't match anything.</summary>
    public static bool TryParseIfMatch(string header, out byte[]? expected)
    {
        expected = null;
        var value = header.Trim();
        if (value == "*")
        {
            return true;
        }
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"' || value.Contains(','))
        {
            return false;
        }
        try
        {
            expected = Base64Url.DecodeFromChars(value.AsSpan(1, value.Length - 2));
            return expected.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Opaque list cursor. It carries the whole query, so following it needs no other parameters.</summary>
internal sealed record ListCursor(string? Q, string? Sort, int Page, int PerPage, bool AsRequester, string? Extra = null)
{
    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(this, ApiJson.Options));

    public static ListCursor Decode(string value)
    {
        try
        {
            var cursor = JsonSerializer.Deserialize<ListCursor>(Base64Url.DecodeFromChars(value), ApiJson.Options);
            if (cursor is { Page: >= 1 and <= Paging.MaxPage, PerPage: >= 1 and <= Paging.MaxPerPage })
            {
                return cursor;
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
        }
        throw new ValidationException("cursor", "That cursor isn't valid. Start again from the first page.");
    }
}

internal static class Paging
{
    public const int DefaultPerPage = 25;
    public const int MaxPerPage = 100;
    public const int MaxPage = 10_000;

    public static int Int(HttpRequest request, string name, int fallback, int min, int max)
    {
        var raw = request.Query[name].ToString();
        if (raw.Length == 0)
        {
            return fallback;
        }
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            throw new ValidationException(name, $"Use a whole number from {min} to {max}.");
        }
        return value;
    }

    public static string? Text(HttpRequest request, string name, int maxLength)
    {
        var raw = request.Query[name].ToString().Trim();
        if (raw.Length > maxLength)
        {
            throw new ValidationException(name, $"Keep {name} under {maxLength} characters.");
        }
        return raw.Length == 0 ? null : raw;
    }

    public static Guid? Id(HttpRequest request, string name)
    {
        var raw = request.Query[name].ToString();
        if (raw.Length == 0)
        {
            return null;
        }
        return Guid.TryParse(raw, out var id) ? id : throw new ValidationException(name, "Use an id (a GUID).");
    }

    /// <summary>Sets <c>meta.nextCursor</c> and an RFC 8288 <c>Link: rel="next"</c> header.</summary>
    public static PageMeta Meta(HttpContext http, int total, ListCursor current)
    {
        string? next = null;
        if (current.Page * current.PerPage < total && current.Page < MaxPage)
        {
            next = (current with { Page = current.Page + 1 }).Encode();
            http.Response.Headers.Append("Link", $"<{http.Request.PathBase}{http.Request.Path}?cursor={next}>; rel=\"next\"");
        }
        return new PageMeta(total, current.Page, current.PerPage, next);
    }
}

internal static class ApiBodies
{
    /// <summary>HTML wins; plain text is converted. Sending both is an error.</summary>
    public static string Html(IHtmlContent html, string? bodyHtml, string? bodyText, string field = "body")
    {
        if (bodyHtml is not null && bodyText is not null)
        {
            throw new ValidationException(field, $"Send {field}Html or {field}Text, not both.");
        }
        return bodyHtml ?? (bodyText is null ? "" : html.FromPlainText(bodyText));
    }

    public static IReadOnlyList<Guid>? Ids(IReadOnlyList<Guid>? ids, string field, int max = 50) =>
        ids is { Count: > 0 } && ids.Count > max ? throw new ValidationException(field, $"Send at most {max} ids.") : ids;
}

/// <summary>How a caller sees tickets: agents get the agent view unless they ask for <c>?as=requester</c>.</summary>
internal static class ApiViews
{
    public static async Task<bool> RequesterModeAsync(HttpRequest request, IPermissionService permissions, CancellationToken ct)
    {
        var mode = request.Query["as"].ToString();
        if (mode.Length > 0 && mode is not ("requester" or "agent"))
        {
            throw new ValidationException("as", "Use as=requester or as=agent.");
        }
        var isAgent = (await permissions.CurrentAsync(ct))?.IsAgent == true;
        return !isAgent || mode == "requester";
    }
}
