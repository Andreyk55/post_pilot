using Microsoft.Extensions.Logging.Console;
using PostPilot.Api.Extensions;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Settings;

// ── PostPilot Worker — Generic Host, background-only process ──────────────
// Runs PostPublishingWorker (the scheduling/publishing loop).
// It does NOT expose any HTTP endpoints and does NOT run EF migrations
// (migrations are applied by the API container on startup).

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((ctx, config) =>
    {
        // Mirror the config loading from PostPilot.Api/Program.cs so that the
        // same layered config files and env-var overrides are used by both processes.
        var aspEnv = ctx.HostingEnvironment.EnvironmentName;
        var appEnv = aspEnv switch
        {
            "Development" => "local",
            "Staging"     => "dev",
            "Production"  => "prod",
            _             => aspEnv.ToLowerInvariant()
        };

        config
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{aspEnv}.json", optional: true, reloadOnChange: true)
            .AddJsonFile("config/appsettings.common.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"config/appsettings.{appEnv}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .AddFlatEnvironmentVariables(); // maps APP_RUN_MODE, META_APP_* etc.
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(options =>
        {
            options.IncludeScopes   = true;
            options.SingleLine      = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            options.ColorBehavior   = LoggerColorBehavior.Disabled; // no ANSI codes in container logs
        });
    })
    .ConfigureServices((ctx, services) =>
    {
        // Core PostPilot services: DbContext, options, publishers, scheduler,
        // media, insights — identical to what the API registers.
        services.AddPostPilotCoreServices(ctx.Configuration);

        // Register the background worker loop.
        // The API container does NOT register this; only the worker container does.
        services.AddHostedService<PostPublishingWorker>();
    })
    .Build();

// ── Startup log (category differs from API for easy grep) ─────────────────
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("PostPilot.Publisher.Startup");

startupLogger.LogInformation(
    "PostPilot.Publisher started — publishing loop is active in this container only");

await host.RunAsync();
