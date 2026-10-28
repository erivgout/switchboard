using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;

namespace Switchboard.Web.Hosting;

/// <summary>One-time token that protects first-run setup (SR-DEP-2). Lives only in memory.</summary>
public sealed class SetupTokenStore
{
    public string? Token { get; private set; }

    public void Issue() => Token ??= Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', 'x').Replace('/', 'y');

    public bool Verify(string? candidate) =>
        Token is not null && candidate is not null
        && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(Token), System.Text.Encoding.UTF8.GetBytes(candidate.Trim()));

    public void Revoke() => Token = null;
}

public static class DatabaseInitializer
{
    /// <summary>Creates or migrates the schema, seeds defaults, and issues a setup token when needed.</summary>
    public static async Task InitializeAsync(WebApplication app, CancellationToken ct = default)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SwitchboardDbContext>();
        var options = sp.GetRequiredService<IOptions<SwitchboardOptions>>().Value;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Switchboard.Startup");
        var env = app.Environment;

        if (env.IsDevelopment() && app.Configuration.GetValue<bool>("Switchboard:ResetDatabase"))
        {
            logger.LogWarning("Switchboard:ResetDatabase is set: dropping the development database");
            await db.Database.EnsureDeletedAsync(ct);
        }

        if (db.Database.GetMigrations().Any())
        {
            await db.Database.MigrateAsync(ct);
        }
        else
        {
            // Before the first migration exists (early development) the schema is created directly.
            await db.Database.EnsureCreatedAsync(ct);
        }

        using var _ = sp.GetRequiredService<IActorAccessor>().BeginScope(Actor.System("Setup"));
        await CoreSeeder.SeedAsync(sp, ct);
        foreach (var module in sp.GetRequiredService<ModuleCatalog>().Modules)
        {
            await module.SeedAsync(sp, ct);
        }

        if (options.DemoData && env.IsDevelopment())
        {
            await DemoSeeder.SeedAsync(sp, ct);
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync(ct);
            if (org.SetupCompletedAt is null)
            {
                org.Name = "Northwind Field Services";
                org.SetupCompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            logger.LogInformation("Demo data ready. Sign in as {Admin} / {Password}", DemoAccounts.Admin, DemoAccounts.Password);
        }

        var needsSetup = await db.Organizations.AnyAsync(o => o.SetupCompletedAt == null, ct)
            && !await db.Users.AnyAsync(u => u.IsOrgAdmin, ct);
        if (needsSetup)
        {
            var tokens = sp.GetRequiredService<SetupTokenStore>();
            tokens.Issue();
            logger.LogWarning("""

                ============================================================
                 Switchboard is not set up yet.
                 Open {BaseUrl}/setup and enter this setup token:

                     {Token}

                 The token works once and changes on every restart.
                ============================================================
                """, options.BaseUrl.TrimEnd('/'), tokens.Token);
        }
    }
}
