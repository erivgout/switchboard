using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Switchboard.Core.Data;

namespace Switchboard.Web.Hosting;

/// <summary>
/// Used by <c>dotnet ef</c> to build the full model (Core plus every module) for migrations:
/// <c>dotnet dotnet-ef migrations add &lt;Name&gt; -p src/Switchboard.Web -s src/Switchboard.Web -o Data/Migrations</c>.
/// Build without <c>SWITCHBOARD_MODULES</c> so every module is included.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SwitchboardDbContext>
{
    public SwitchboardDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Server=localhost,14333;Database=Switchboard;User Id=sa;Password=Switchboard-Dev-2026!;TrustServerCertificate=True;Encrypt=True";
        var options = new DbContextOptionsBuilder<SwitchboardDbContext>()
            .UseSqlServer(connection, sql => sql.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.GetName().Name))
            .Options;
        return new SwitchboardDbContext(options, ModuleLoader.Discover());
    }
}
