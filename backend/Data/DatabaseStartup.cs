using System.Reflection;
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
    ///
    /// Logs (intentionally noisy so a deploy mismatch is obvious from the boot log):
    ///   - the running assembly's InformationalVersion (confirms which build is up),
    ///   - every migration the assembly knows about,
    ///   - what's already applied vs pending BEFORE Migrate runs,
    ///   - what got applied AFTER Migrate runs.
    /// </summary>
    public static async Task RunMigrationsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("PostPilot.Migrations");

        var asm = typeof(AppDbContext).Assembly;
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString()
                   ?? "(unknown)";
        logger.LogInformation(
            "Migration runner starting — assembly={Assembly}, version={Version}",
            asm.GetName().Name, version);

        try
        {
            // What this build of the assembly knows about, in order.
            var allInAssembly = db.Database.GetMigrations().ToList();
            logger.LogInformation(
                "Migrations known to assembly ({Count}):\n  {List}",
                allInAssembly.Count,
                string.Join("\n  ", allInAssembly));

            // What's already applied in the DB.
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            logger.LogInformation(
                "Migrations already applied in DB ({Count}):\n  {List}",
                applied.Count,
                applied.Count == 0 ? "(none)" : string.Join("\n  ", applied));

            // What's pending (assembly minus applied).
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
            logger.LogInformation(
                "Pending migrations ({Count}):\n  {List}",
                pending.Count,
                pending.Count == 0 ? "(none — DB is up to date)" : string.Join("\n  ", pending));

            // Sanity check: if the assembly DOESN'T even contain the migration that
            // would add the columns the new code needs, the deploy is stale. This
            // is exactly the symptom we saw on prod (column m.Provider missing).
            // We don't crash on this — just shout — because some old assemblies
            // are legitimately deployed during rollbacks. But it makes it
            // impossible to miss in the log.
            if (pending.Count == 0 && applied.Count > 0
                && !applied.Contains("20260529120000_AddProviderIdentityAndCancellationMetadata")
                && !allInAssembly.Contains("20260529120000_AddProviderIdentityAndCancellationMetadata"))
            {
                logger.LogWarning(
                    "This build does NOT contain migration 20260529120000_AddProviderIdentityAndCancellationMetadata. " +
                    "If the running code references Provider/ProviderAccountId columns, requests will 500. " +
                    "Deploy a newer image.");
            }

            logger.LogInformation("Applying pending EF Core migrations...");
            await db.Database.MigrateAsync();

            var appliedAfter = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            var justApplied = appliedAfter.Except(applied).ToList();
            logger.LogInformation(
                "Migrations applied successfully — {NewlyAppliedCount} new, total applied={TotalApplied}.\n" +
                "Newly applied:\n  {List}",
                justApplied.Count,
                appliedAfter.Count,
                justApplied.Count == 0 ? "(none — DB was already up to date)" : string.Join("\n  ", justApplied));
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
