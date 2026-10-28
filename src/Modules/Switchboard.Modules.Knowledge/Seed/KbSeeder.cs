using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Knowledge.Domain;

namespace Switchboard.Modules.Knowledge.Seed;

/// <summary>Idempotent defaults for every instance.</summary>
internal static class KbSeeder
{
    /// <summary>
    /// Agents write articles and supervisors publish them by default. Applied once, so an admin who later
    /// removes these privileges from a built-in role keeps that choice. (kb.view reaches agents through
    /// Core's ".view" convention.)
    /// </summary>
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<SwitchboardDbContext>();
        var settings = services.GetRequiredService<ISettingsService>();
        if (await settings.GetAsync(KbSettingKeys.RolesSeeded, false, ct: ct))
        {
            return;
        }

        var grants = new Dictionary<string, string[]>
        {
            [RoleKeys.Agent] = [KbPrivileges.View, KbPrivileges.Write],
            [RoleKeys.Supervisor] = [KbPrivileges.View, KbPrivileges.Write, KbPrivileges.Publish],
            [RoleKeys.ReadOnly] = [KbPrivileges.View],
        };
        foreach (var (key, privileges) in grants)
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Key == key, ct);
            if (role is not null)
            {
                role.Privileges = role.Privileges.Union(privileges).ToList();
            }
        }
        await settings.SetAsync(KbSettingKeys.RolesSeeded, true, ct: ct);
        await db.SaveChangesAsync(ct);
    }
}
