using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api;
using PostPilot.Api.Data;
using PostPilot.Api.Middleware;
using PostPilot.Api.Services.Ai;
using PostPilot.Api.Settings;

// Entry point: long-running ASP.NET Core server (Kestrel)

var builder = WebApplication.CreateBuilder(args);

// ── Additional config: load backend/config/ layered files ─────────────────
// Precedence (last wins): appsettings.json → appsettings.{env}.json
//   → config/appsettings.common.json → config/appsettings.{appEnv}.json → env vars
//   → legacy flat env vars (backward compat, deprecated)
var aspEnv = builder.Environment.EnvironmentName; // e.g. "Development"
var appEnv = aspEnv switch
{
    "Development" => "local",
    "Staging"     => "dev",
    "Production"  => "prod",
    _             => aspEnv.ToLowerInvariant()
};

builder.Configuration
    .AddJsonFile("config/appsettings.common.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"config/appsettings.{appEnv}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddFlatEnvironmentVariables();

// ── Console logging: single-line with timestamp + scopes ──────────────────
builder.Logging.AddSimpleConsole(options =>
{
    options.IncludeScopes = true;
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    options.ColorBehavior = LoggerColorBehavior.Enabled;
});

// ── EnableEfSql toggle: flip EF Core Database.Command back to Information ──
var enableEfSql = builder.Configuration.GetValue<bool>("Logging:EnableEfSql");
if (enableEfSql)
{
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Information);
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Query", LogLevel.Information);
}

// Use Startup class for DI and middleware configuration
var startup = new Startup(builder.Configuration);
startup.ConfigureServices(builder.Services);

var app = builder.Build();

// ── Startup configuration log ────────────────────────────────────────────
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PostPilot.Startup");
    var appOpts = app.Services.GetRequiredService<IOptions<AppOptions>>().Value;
    var geminiOpts = app.Services.GetRequiredService<IOptions<GeminiSettings>>().Value;
    logger.LogInformation(
        "PostPilot started — RunMode={RunMode}, PublicUrl={PublicUrl}, GeminiModel={Model}, GeminiVisionModel={VisionModel}",
        appOpts.RunMode,
        string.IsNullOrEmpty(appOpts.PublicUrl) ? "(none)" : "(set)",
        geminiOpts.Model,
        geminiOpts.VisionModel);
}

// Configure the middleware pipeline
startup.Configure(app, app.Environment);

// ── Run EF Core migrations (API only — Worker does NOT run migrations) ────
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var migrLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("PostPilot.Migrations");
    migrLogger.LogInformation("Applying pending EF Core migrations...");
    await db.Database.MigrateAsync();
    migrLogger.LogInformation("Migrations applied successfully.");
}

app.Run();
