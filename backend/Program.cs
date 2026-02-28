using Microsoft.Extensions.Logging.Console;
using PostPilot.Api;
using PostPilot.Api.Middleware;

// Entry point: long-running ASP.NET Core server (Kestrel)

var builder = WebApplication.CreateBuilder(args);

// ── Additional config: load backend/config/ layered files ─────────────────
// Precedence (last wins): appsettings.json → appsettings.{env}.json
//   → config/appsettings.common.json → config/appsettings.{appEnv}.json → env vars
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
    .AddEnvironmentVariables();

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

// Configure the middleware pipeline
startup.Configure(app, app.Environment);

app.Run();
