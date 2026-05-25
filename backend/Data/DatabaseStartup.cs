using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PostPilot.Api.Data;

/// <summary>
/// Database-related startup helpers shared by the API and the Worker.
/// Keeps Program.cs short and password handling in one place.
/// </summary>
public static class DatabaseStartup
{
    /// <summary>
    /// Logs Host / Port / Database / Username / SslMode for the configured
    /// ConnectionStrings:DefaultConnection. Password is never logged.
    /// </summary>
    public static void LogDatabaseInfo(IConfiguration configuration, ILogger logger)
    {
        var conn = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrEmpty(conn))
            return;

        var b = new NpgsqlConnectionStringBuilder(conn);
        logger.LogInformation(
            "Database — Host={Host}, Port={Port}, Database={Database}, Username={Username}, SslMode={SslMode}",
            b.Host, b.Port, b.Database, b.Username, b.SslMode);
    }

    /// <summary>
    /// Applies pending EF Core migrations and translates Npgsql failures into
    /// a one-word category in the log so the operator can see at a glance
    /// whether it's auth / ssl / network / etc. API-only — the Worker does
    /// not call this.
    /// </summary>
    public static async Task RunMigrationsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("PostPilot.Migrations");

        logger.LogInformation("Applying pending EF Core migrations...");
        try
        {
            await db.Database.MigrateAsync();
            logger.LogInformation("Migrations applied successfully.");
        }
        catch (NpgsqlException ex)
        {
            var category = CategorizeNpgsqlFailure(ex);
            logger.LogCritical(ex,
                "Database connection failed during migration — category={Category}, sqlState={SqlState}",
                category, ex.SqlState ?? "(none)");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "EF Core migration failed.");
            throw;
        }
    }

    private static string CategorizeNpgsqlFailure(NpgsqlException ex) => ex.SqlState switch
    {
        "28P01" or "28000"            => "auth",              // invalid_password / invalid_authorization
        "3D000"                       => "database-missing",
        "08006" or "08001" or "08004" => "network",
        _ when ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) => "ssl",
        _ when ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase) => "ssl",
        _                             => "connection",
    };
}
