using Microsoft.Extensions.Logging.Console;
using PostPilot.Api;
using PostPilot.Api.Middleware;

// This is the entry point for local development (dotnet run)
// For AWS Lambda deployment, LambdaEntryPoint.cs is used instead

var builder = WebApplication.CreateBuilder(args);

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

// Use the shared Startup class for consistency with Lambda
var startup = new Startup(builder.Configuration);
startup.ConfigureServices(builder.Services);

var app = builder.Build();

// Configure the middleware pipeline
startup.Configure(app, app.Environment);

app.Run();
