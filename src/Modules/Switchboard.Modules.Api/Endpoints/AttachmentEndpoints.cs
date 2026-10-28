using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Switchboard.Core.Api;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Api.Auth;
using Switchboard.Modules.Api.Http;

namespace Switchboard.Modules.Api.Endpoints;

/// <summary>Upload files to attach to tickets and messages, and download attachments you can see.</summary>
internal static class AttachmentEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints, long maxUploadBytes)
    {
        var g = endpoints.MapSwitchboardApi("attachments", "Attachments").WithApiConventions();

        g.MapPost("", async (HttpContext http, IAttachmentService attachments, IOptions<StorageOptions> storage, CancellationToken ct) =>
            {
                if (!http.Request.HasFormContentType)
                {
                    throw new UnsupportedMediaTypeException();
                }
                var form = await http.Request.ReadFormAsync(new FormOptions
                {
                    MultipartBodyLengthLimit = storage.Value.MaxUploadBytes + 64 * 1024,
                    ValueCountLimit = 20,
                    ValueLengthLimit = 4096,
                }, ct);
                var file = form.Files.GetFile("file") ?? throw new ValidationException("file", "Send the file in a form field named file.");
                if (form.Files.Count > 1)
                {
                    throw new ValidationException("file", "Upload one file per request.");
                }
                await using var stream = file.OpenReadStream();
                var saved = await attachments.UploadAsync(stream, file.FileName, file.ContentType, AttachmentOwners.Pending, null, form["inline"] == "true", ct);
                http.Response.Headers.Location = $"{SwitchboardApi.Prefix}/attachments/{saved.Id}";
                return ApiJson.Ok(ApiTicketReader.ToDto(saved), StatusCodes.Status201Created);
            })
            .WithName("uploadAttachment").WithSummary("Upload a file")
            .WithDescription("multipart/form-data with a field named file. Returns an id to pass in attachmentIds when creating a ticket, replying or adding a note. Unattached uploads are deleted after 24 hours.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<AttachmentDto>(StatusCodes.Status201Created)
            .BodyLimit(maxUploadBytes + 64 * 1024)
            .RequireScope(ApiScopes.TicketsWrite)
            .DisableAntiforgery();

        g.MapGet("/{id:guid}", async (Guid id, IAttachmentService attachments, CancellationToken ct) =>
            {
                var opened = await attachments.OpenAsync(id, ct) ?? throw new NotFoundException();
                await opened.Content.DisposeAsync();
                return ApiJson.Ok(ApiTicketReader.ToDto(opened.Attachment));
            })
            .WithName("getAttachment").WithSummary("Get attachment details")
            .Produces<AttachmentDto>().RequireScope(ApiScopes.TicketsRead);

        g.MapGet("/{id:guid}/content", async (Guid id, HttpContext http, IAttachmentService attachments, CancellationToken ct) =>
            {
                var opened = await attachments.OpenAsync(id, ct) ?? throw new NotFoundException();
                var headers = http.Response.Headers;
                headers[HeaderNames.XContentTypeOptions] = "nosniff";
                headers[HeaderNames.ContentSecurityPolicy] = "sandbox; default-src 'none'";
                headers[HeaderNames.CacheControl] = "private, no-store";
                var disposition = new ContentDispositionHeaderValue("attachment");
                disposition.SetHttpFileName(opened.Attachment.FileName);
                headers[HeaderNames.ContentDisposition] = disposition.ToString();
                return Results.Stream(opened.Content, opened.Attachment.ContentType, enableRangeProcessing: true);
            })
            .WithName("downloadAttachment").WithSummary("Download an attachment")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .RequireScope(ApiScopes.TicketsRead);
    }
}
