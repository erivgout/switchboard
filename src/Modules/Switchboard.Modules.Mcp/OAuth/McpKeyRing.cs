using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using Switchboard.Core.Data;
using Switchboard.Modules.Mcp.Data;

namespace Switchboard.Modules.Mcp.OAuth;

/// <summary>
/// Persistent RSA keys for signing and encrypting tokens, stored in the database and protected with
/// ASP.NET Core Data Protection, so tokens survive restarts and work across instances.
/// </summary>
internal sealed class McpKeyRing(IServiceScopeFactory scopes, IDataProtectionProvider protection) : IConfigureOptions<OpenIddictServerOptions>
{
    private const string Purpose = "Switchboard.Mcp.OAuthKeys";
    private const string Signing = "signing";
    private const string Encryption = "encryption";

    public void Configure(OpenIddictServerOptions options)
    {
        var protector = protection.CreateProtector(Purpose);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();

        var signing = LoadOrCreate(db, protector, Signing);
        var encryption = LoadOrCreate(db, protector, Encryption);

        options.SigningCredentials.Add(new SigningCredentials(signing, SecurityAlgorithms.RsaSha256));
        options.EncryptionCredentials.Add(new EncryptingCredentials(encryption, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
    }

    private static RsaSecurityKey LoadOrCreate(SwitchboardDbContext db, IDataProtector protector, string use)
    {
        var row = db.Set<McpSigningKey>().AsNoTracking().Where(k => k.Use == use).OrderBy(k => k.CreatedAt).FirstOrDefault();
        if (row is null)
        {
            using var generated = RSA.Create(2048);
            row = new McpSigningKey
            {
                Use = use,
                ProtectedKey = protector.Protect(generated.ExportRSAPrivateKeyPem()),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Set<McpSigningKey>().Add(row);
            db.SaveChanges();
            // Another instance may have created one at the same time: everyone uses the oldest.
            row = db.Set<McpSigningKey>().AsNoTracking().Where(k => k.Use == use).OrderBy(k => k.CreatedAt).First();
        }

        var rsa = RSA.Create();
        rsa.ImportFromPem(protector.Unprotect(row.ProtectedKey));
        return new RsaSecurityKey(rsa) { KeyId = row.Id.ToString("N")[..16] };
    }
}
