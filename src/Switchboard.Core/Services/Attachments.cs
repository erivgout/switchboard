using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;

namespace Switchboard.Core.Services;

public sealed class StorageOptions
{
    /// <summary>Directory for the local file store.</summary>
    public string Path { get; set; } = "App_Data/files";
    public long MaxUploadBytes { get; set; } = 25 * 1024 * 1024;
}

/// <summary>Blob storage behind a provider adapter (local disk now; S3-compatible later).</summary>
public interface IFileStore
{
    Task<string> SaveAsync(Stream content, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public sealed class LocalFileStore(IOptions<StorageOptions> options) : IFileStore
{
    private string Root => System.IO.Path.GetFullPath(options.Value.Path);

    public async Task<string> SaveAsync(Stream content, CancellationToken ct = default)
    {
        var key = $"{DateTime.UtcNow:yyyy/MM}/{Guid.NewGuid():N}";
        var path = Resolve(key);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
        return key;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<Stream>(File.OpenRead(Resolve(key)));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = Resolve(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, key));
        if (!full.StartsWith(Root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid storage key.");
        }
        return full;
    }
}

/// <summary>Decides who may read attachments of one owner type. Modules register policies for their owners.</summary>
public interface IAttachmentAccessPolicy
{
    string OwnerType { get; }
    Task<bool> CanReadAsync(Attachment attachment, CancellationToken ct);
}

public interface IAttachmentService
{
    /// <summary>Validates (SR-INP-5), stores and records a file. Use <see cref="AttachmentOwners.Pending"/> before the owner exists.</summary>
    Task<Attachment> UploadAsync(Stream content, string fileName, string contentType, string ownerType, Guid? ownerId, bool inline = false, CancellationToken ct = default);

    /// <summary>Moves the current user's pending uploads onto an owner. Ignores ids the user didn't upload.</summary>
    Task<IReadOnlyList<Attachment>> ClaimPendingAsync(IEnumerable<Guid> attachmentIds, string ownerType, Guid ownerId, CancellationToken ct = default);

    /// <summary>Opens an attachment after checking the owner's access policy; null when not found or not allowed.</summary>
    Task<(Attachment Attachment, Stream Content)?> OpenAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Attachment>> ListAsync(string ownerType, Guid ownerId, CancellationToken ct = default);

    /// <summary>True for types safe to show inline in the browser (raster images, PDF).</summary>
    bool IsInlineSafe(Attachment attachment);
}

public sealed class AttachmentService(
    SwitchboardDbContext db,
    IFileStore store,
    IActorAccessor actors,
    IEnumerable<IAttachmentAccessPolicy> policies,
    IOptions<StorageOptions> options,
    TimeProvider time) : IAttachmentService
{
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".com", ".msi", ".scr", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".hta", ".cpl", ".jar", ".lnk", ".reg",
    };

    private static readonly HashSet<string> InlineSafeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf",
    };

    public async Task<Attachment> UploadAsync(Stream content, string fileName, string contentType, string ownerType, Guid? ownerId, bool inline = false, CancellationToken ct = default)
    {
        var safeName = SanitizeFileName(fileName);
        if (BlockedExtensions.Contains(Path.GetExtension(safeName)))
        {
            throw new ValidationException("file", $"Files of type {Path.GetExtension(safeName)} can't be uploaded.");
        }

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length > options.Value.MaxUploadBytes)
        {
            throw new ValidationException("file", $"Files must be smaller than {options.Value.MaxUploadBytes / (1024 * 1024)} MB.");
        }
        if (buffer.Length == 0)
        {
            throw new ValidationException("file", "The file is empty.");
        }

        buffer.Position = 0;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(buffer, ct));
        buffer.Position = 0;
        var key = await store.SaveAsync(buffer, ct);

        var attachment = new Attachment
        {
            OwnerType = ownerType,
            OwnerId = ownerId,
            FileName = safeName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Split(';')[0].Trim(),
            Size = buffer.Length,
            StorageKey = key,
            Sha256 = hash,
            IsInline = inline,
            UploadedById = actors.Current.UserId,
            CreatedAt = time.GetUtcNow(),
        };
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(ct);
        return attachment;
    }

    public async Task<IReadOnlyList<Attachment>> ClaimPendingAsync(IEnumerable<Guid> attachmentIds, string ownerType, Guid ownerId, CancellationToken ct = default)
    {
        var ids = attachmentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }
        var actor = actors.Current;
        var pending = await db.Attachments
            .Where(a => ids.Contains(a.Id) && a.OwnerType == AttachmentOwners.Pending)
            .ToListAsync(ct);
        var claimed = pending.Where(a => actor.IsInternal || a.UploadedById == actor.UserId).ToList();
        foreach (var a in claimed)
        {
            a.OwnerType = ownerType;
            a.OwnerId = ownerId;
        }
        return claimed;
    }

    public async Task<(Attachment Attachment, Stream Content)?> OpenAsync(Guid id, CancellationToken ct = default)
    {
        var attachment = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (attachment is null)
        {
            return null;
        }

        var actor = actors.Current;
        var allowed = attachment.OwnerType == AttachmentOwners.Pending
            ? actor.IsInternal || (actor.UserId is not null && attachment.UploadedById == actor.UserId)
            : await policies.Where(p => p.OwnerType == attachment.OwnerType).ToAsyncAny(p => p.CanReadAsync(attachment, ct));
        if (!allowed)
        {
            return null;
        }

        return (attachment, await store.OpenReadAsync(attachment.StorageKey, ct));
    }

    public async Task<IReadOnlyList<Attachment>> ListAsync(string ownerType, Guid ownerId, CancellationToken ct = default) =>
        await db.Attachments.AsNoTracking()
            .Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

    public bool IsInlineSafe(Attachment attachment) => InlineSafeTypes.Contains(attachment.ContentType);

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName ?? "file");
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        name = name.Trim().TrimStart('.');
        if (name.Length == 0)
        {
            name = "file";
        }
        return name.Length > 200 ? name[..200] : name;
    }
}

internal static class AsyncEnumerableExtensions
{
    public static async Task<bool> ToAsyncAny<T>(this IEnumerable<T> source, Func<T, Task<bool>> predicate)
    {
        foreach (var item in source)
        {
            if (await predicate(item))
            {
                return true;
            }
        }
        return false;
    }
}
