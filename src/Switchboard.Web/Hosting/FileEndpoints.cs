using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Net.Http.Headers;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Web.Hosting;

/// <summary>Attachment download and upload (SR-DATA-5, SR-INP-5).</summary>
public static class FileEndpoints
{
    public static void MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/files/{id:guid}/{name?}", async (Guid id, IAttachmentService attachments, HttpContext http, CancellationToken ct) =>
        {
            var opened = await attachments.OpenAsync(id, ct);
            if (opened is not { } file)
            {
                return Results.NotFound();
            }

            var inline = attachments.IsInlineSafe(file.Attachment) && http.Request.Query["download"] != "1";
            http.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            http.Response.Headers[HeaderNames.ContentSecurityPolicy] = "sandbox; default-src 'none'; img-src 'self'; style-src 'unsafe-inline'";
            http.Response.Headers[HeaderNames.CacheControl] = "private, max-age=300";
            var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
            disposition.SetHttpFileName(file.Attachment.FileName);
            http.Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
            return Results.Stream(file.Content, inline ? file.Attachment.ContentType : "application/octet-stream", enableRangeProcessing: true);
        }).AllowAnonymous(); // Each owner type's IAttachmentAccessPolicy decides; public KB images load signed out.

        app.MapPost("/uploads", async (HttpContext http, IAntiforgery antiforgery, IAttachmentService attachments, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(http);
            if (!http.Request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Send the file as multipart/form-data." });
            }
            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null)
            {
                return Results.BadRequest(new { error = "Choose a file to upload." });
            }
            try
            {
                await using var stream = file.OpenReadStream();
                var saved = await attachments.UploadAsync(stream, file.FileName, file.ContentType, AttachmentOwners.Pending, null, form["inline"] == "true", ct);
                return Results.Ok(new
                {
                    saved.Id,
                    saved.FileName,
                    saved.Size,
                    saved.ContentType,
                    url = $"/files/{saved.Id}/{Uri.EscapeDataString(saved.FileName)}",
                });
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireRateLimiting("uploads").DisableAntiforgery();
    }
}
