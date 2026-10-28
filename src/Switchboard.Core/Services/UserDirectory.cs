using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;

namespace Switchboard.Core.Services;

/// <summary>Look up and create people by email across primary and secondary addresses (INV-ID-2).</summary>
public interface IUserDirectory
{
    Task<User?> FindByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>Returns the user with this email, creating a requester if allowed. Throws <see cref="ValidationException"/> otherwise.</summary>
    Task<User> EnsureRequesterAsync(string email, string? displayName, CancellationToken ct = default);

    /// <summary>True when the address belongs to no user yet (primary or secondary).</summary>
    Task<bool> IsEmailFreeAsync(string email, Guid? exceptUserId = null, CancellationToken ct = default);

    static string Normalize(string email) => email.Trim().ToUpperInvariant();

    static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254)
        {
            return false;
        }
        try
        {
            var parsed = new MailAddress(email.Trim());
            return parsed.Address.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase) && parsed.Host.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class UserDirectory(SwitchboardDbContext db, ISettingsService settings, TimeProvider time) : IUserDirectory
{
    public async Task<User?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalized = IUserDirectory.Normalize(email);
        var user = db.Users.Local.FirstOrDefault(u => u.NormalizedEmail == normalized)
            ?? await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
        if (user is not null)
        {
            return user;
        }
        var secondary = await db.UserEmails.AsNoTracking().FirstOrDefaultAsync(e => e.NormalizedEmail == normalized, ct);
        return secondary is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == secondary.UserId, ct);
    }

    public async Task<bool> IsEmailFreeAsync(string email, Guid? exceptUserId = null, CancellationToken ct = default)
    {
        var normalized = IUserDirectory.Normalize(email);
        var primary = await db.Users.AnyAsync(u => u.NormalizedEmail == normalized && u.Id != exceptUserId, ct);
        var secondary = await db.UserEmails.AnyAsync(e => e.NormalizedEmail == normalized && e.UserId != exceptUserId, ct);
        return !primary && !secondary;
    }

    public async Task<User> EnsureRequesterAsync(string email, string? displayName, CancellationToken ct = default)
    {
        if (!IUserDirectory.IsValidEmail(email))
        {
            throw new ValidationException("requester", "Enter a valid email address.");
        }

        var existing = await FindByEmailAsync(email, ct);
        if (existing is not null)
        {
            return existing;
        }

        if (!await settings.GetAsync(SettingKeys.AutoCreateRequestersFromEmail, true, ct: ct))
        {
            throw new ValidationException("requester", "No requester has that email, and new requesters can't be created automatically.");
        }

        var trimmed = email.Trim();
        var domain = trimmed[(trimmed.IndexOf('@') + 1)..].ToLowerInvariant();
        var departments = await db.Departments.AsNoTracking().Select(d => new { d.Id, d.Domains }).ToListAsync(ct);
        var department = departments.FirstOrDefault(d => d.Domains.Any(x => string.Equals(x, domain, StringComparison.OrdinalIgnoreCase)));

        var user = new User
        {
            Email = trimmed,
            NormalizedEmail = IUserDirectory.Normalize(trimmed),
            UserName = trimmed,
            NormalizedUserName = IUserDirectory.Normalize(trimmed),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? trimmed[..trimmed.IndexOf('@')] : displayName.Trim(),
            DepartmentId = department?.Id,
            CreatedAt = time.GetUtcNow(),
        };
        db.Users.Add(user);
        return user;
    }
}
