using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PostPilot.Api.Data;

/// <summary>
/// Used only by `dotnet ef migrations add` / `database update`. The project's
/// real runtime config lives in ConfigurationFiles/ which the design-time
/// tooling does not auto-load, so we read the connection string from the
/// POSTPILOT_DESIGN_CONNECTION env var (falling back to the standard
/// ConnectionStrings__DefaultConnection or the local dev default). The
/// schema this generates does not depend on the connection string — any
/// reachable PostgreSQL instance will do.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("POSTPILOT_DESIGN_CONNECTION") ??
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection") ??
            "Host=localhost;Port=5432;Database=postpilot;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AppDbContext(options);
    }
}
