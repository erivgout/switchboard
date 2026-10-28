using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Testing;

namespace Switchboard.Core.Tests;

public sealed class AuditChainTests : IAsyncLifetime
{
    private SwitchboardTestHost _host = default!;

    public async Task InitializeAsync() => _host = await SwitchboardTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Sealed_chain_verifies_and_detects_tampering()
    {
        await _host.RunAsync(Actor.System("Test"), async sp =>
        {
            var audit = sp.GetRequiredService<IAuditLog>();
            for (var i = 0; i < 3; i++)
            {
                await audit.WriteAsync("test.event", "thing", i.ToString(), new { i });
            }
        });

        var sealer = _host.Services.GetRequiredService<AuditChainSealer>();
        Assert.Equal(3, await sealer.SealAsync(default));

        await _host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            Assert.Null(await AuditChainSealer.VerifyAsync(db, default));

            // Tamper with the middle entry: verification must point at it (INV-AUD-1).
            var middle = await db.AuditEntries.OrderBy(a => a.Sequence).Skip(1).FirstAsync();
            middle.DetailsJson = "{\"i\":99}";
            await db.SaveChangesAsync();
            Assert.Equal(middle.Sequence, await AuditChainSealer.VerifyAsync(db, default));
            return true;
        });
    }
}
