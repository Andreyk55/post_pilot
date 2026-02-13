using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PostPilot.Api.Data;
using PostPilot.Api.Services;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;

namespace PostPilot.Api.Lambdas;

/// <summary>
/// Shared DI configuration for Dispatcher and Publisher Lambda functions.
/// </summary>
public static class LambdaStartup
{
    public static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Database connection
        var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
            ?? throw new InvalidOperationException("DB_CONNECTION_STRING environment variable is required.");

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString));

        // Meta OAuth settings
        var appId = Environment.GetEnvironmentVariable("META_APP_ID")
            ?? throw new InvalidOperationException("META_APP_ID environment variable is required.");

        var appSecret = Environment.GetEnvironmentVariable("META_APP_SECRET")
            ?? throw new InvalidOperationException("META_APP_SECRET environment variable is required.");

        var redirectUri = Environment.GetEnvironmentVariable("META_REDIRECT_URI")
            ?? "https://localhost"; // Not used in Lambda publishing, but required by MetaOAuthSettings

        var metaSettings = new MetaOAuthSettings
        {
            AppId = appId,
            AppSecret = appSecret,
            RedirectUri = redirectUri
        };

        services.AddSingleton(metaSettings);

        // HTTP client for Meta API
        services.AddHttpClient<IMetaOAuthService, MetaOAuthService>();

        // Publisher services
        services.AddHttpClient<FacebookPagePublisher>();
        services.AddScoped<IPostPublisher>(sp => sp.GetRequiredService<FacebookPagePublisher>());

        services.AddHttpClient<InstagramPublisher>();
        services.AddScoped<IPostPublisher>(sp => sp.GetRequiredService<InstagramPublisher>());

        services.AddScoped<IPostPublisherResolver, PostPublisherResolver>();

        // Scheduler (no-op for Lambda - we don't schedule retries via EventBridge from Publisher Lambda)
        // Instead, Publisher Lambda sets NextRetryAt and Dispatcher picks it up
        services.AddScoped<IPostScheduler, NoOpPostScheduler>();

        // Logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        return services.BuildServiceProvider();
    }
}

/// <summary>
/// No-op scheduler for Lambda execution - retries are handled by setting NextRetryAt
/// and letting Dispatcher pick them up on next poll.
/// </summary>
public class NoOpPostScheduler : IPostScheduler
{
    public Task<ScheduleResult> ScheduleAsync(Entities.Post post, CancellationToken cancellationToken = default)
        => Task.FromResult(new ScheduleResult(true, "lambda-noop"));

    public Task<ScheduleResult> RescheduleAsync(Entities.Post post, CancellationToken cancellationToken = default)
        => Task.FromResult(new ScheduleResult(true, "lambda-noop"));

    public Task CancelScheduleAsync(Entities.Post post, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<ScheduleResult> ScheduleRetryAsync(Entities.Post post, DateTime retryAt, CancellationToken cancellationToken = default)
        => Task.FromResult(new ScheduleResult(true, "lambda-noop"));
}
