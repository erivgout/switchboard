namespace Switchboard.Core.Security;

/// <summary>The caller lacks permission. Surfaces as 403, or as 404 where existence must not leak (SR-AUTHZ-6).</summary>
public sealed class ForbiddenException(string message = "You don't have permission to do that.") : Exception(message);

/// <summary>The record does not exist or the caller cannot see it (same response either way).</summary>
public sealed class NotFoundException(string message = "That record doesn't exist or you can't see it.") : Exception(message);

/// <summary>The record changed since the caller loaded it (optimistic concurrency).</summary>
public sealed class ConflictException(string message = "Someone else changed this record. Reload to see their changes.") : Exception(message);

/// <summary>Input failed validation. <see cref="Errors"/> maps field keys to messages.</summary>
public sealed class ValidationException : Exception
{
    public ValidationException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] })
    {
    }

    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base(string.Join(" ", errors.SelectMany(e => e.Value)))
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
