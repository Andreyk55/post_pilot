using System.Text.Json.Serialization;
using Amazon.S3;
using Amazon.Scheduler;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Services;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;

namespace PostPilot.Api;

public class Startup
{
    public IConfiguration Configuration { get; }

    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        // Add controllers with JSON options
        services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            });

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        // Configure PostgreSQL with EF Core
        // In Lambda, connection string can come from environment variables or AWS Secrets Manager
        var connectionString = Configuration.GetConnectionString("DefaultConnection")
                               ?? Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");

        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException(
                "Database connection string not found. Set 'ConnectionStrings:DefaultConnection' in appsettings.json " +
                "or 'DB_CONNECTION_STRING' environment variable.");
        }

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString));

        // Configure Meta OAuth settings (secrets from environment variables only)
        var appId = Environment.GetEnvironmentVariable("META_APP_ID") 
            ?? throw new InvalidOperationException("Required environment variable 'META_APP_ID' is missing.");

        var appSecret = Environment.GetEnvironmentVariable("META_APP_SECRET") 
            ?? throw new InvalidOperationException("Required environment variable 'META_APP_SECRET' is missing.");

        // For the RedirectUri, we check the Config, then Env Var, and finally fallback to localhost
        var redirectUri = Configuration["Meta:RedirectUri"] 
            ?? Environment.GetEnvironmentVariable("META_REDIRECT_URI") 
            ?? throw new InvalidOperationException("RedirectUri is missing.");

        var metaSettings = new MetaOAuthSettings
        {
            AppId = appId,
            AppSecret = appSecret,
            RedirectUri = redirectUri
        };
        
        services.AddSingleton(metaSettings);

        // Register Meta OAuth service
        services.AddHttpClient<IMetaOAuthService, MetaOAuthService>();

        // Configure scheduler based on environment
        ConfigureScheduler(services);

        // Configure media service based on environment
        ConfigureMediaService(services);

        // Configure publishers
        // Use AddHttpClient to register FacebookPagePublisher with a typed HttpClient
        // and also register it as IPostPublisher
        services.AddHttpClient<FacebookPagePublisher>();
        services.AddScoped<IPostPublisher>(sp => sp.GetRequiredService<FacebookPagePublisher>());
        services.AddScoped<IPostPublisherResolver, PostPublisherResolver>();

        // Configure CORS for frontend
        services.AddCors(options =>
        {
            options.AddPolicy("AllowFrontend", policy =>
            {
                policy.SetIsOriginAllowed(origin => origin.StartsWith("http://localhost:") || origin.StartsWith("https://"))
                      .AllowAnyHeader()
                      .AllowAnyMethod();
            });
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        // Configure the HTTP request pipeline.
        if (env.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // Note: UseHttpsRedirection is typically not needed in Lambda as API Gateway handles HTTPS
        // But we'll include it for compatibility with local development
        if (!IsRunningInLambda())
        {
            app.UseHttpsRedirection();
        }

        app.UseCors("AllowFrontend");
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });
    }

    private static bool IsRunningInLambda()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LAMBDA_TASK_ROOT"));
    }

    private static void ConfigureScheduler(IServiceCollection services)
    {
        var schedulerType = Environment.GetEnvironmentVariable("SCHEDULER_TYPE") ?? "Local";

        if (schedulerType.Equals("EventBridge", StringComparison.OrdinalIgnoreCase))
        {
            // Production: AWS EventBridge Scheduler
            var publisherLambdaArn = Environment.GetEnvironmentVariable("PUBLISHER_LAMBDA_ARN");
            var schedulerRoleArn = Environment.GetEnvironmentVariable("SCHEDULER_ROLE_ARN");

            if (string.IsNullOrEmpty(publisherLambdaArn))
            {
                throw new InvalidOperationException(
                    "PUBLISHER_LAMBDA_ARN environment variable is required when SCHEDULER_TYPE=EventBridge");
            }

            if (string.IsNullOrEmpty(schedulerRoleArn))
            {
                throw new InvalidOperationException(
                    "SCHEDULER_ROLE_ARN environment variable is required when SCHEDULER_TYPE=EventBridge");
            }

            var settings = new EventBridgeSchedulerSettings
            {
                ScheduleGroupName = Environment.GetEnvironmentVariable("EVENTBRIDGE_SCHEDULE_GROUP")
                                    ?? "postpilot-schedules",
                PublisherLambdaArn = publisherLambdaArn,
                SchedulerRoleArn = schedulerRoleArn
            };

            services.AddSingleton(settings);
            services.AddSingleton<IAmazonScheduler, AmazonSchedulerClient>();
            services.AddScoped<IPostScheduler, EventBridgePostScheduler>();
        }
        else
        {
            // Local development: Polling-based scheduler
            services.AddScoped<IPostScheduler, LocalPostScheduler>();
            services.AddHostedService<LocalSchedulerBackgroundService>();
        }
    }

    private static void ConfigureMediaService(IServiceCollection services)
    {
        var bucketName = Environment.GetEnvironmentVariable("MEDIA_BUCKET_NAME");

        if (!string.IsNullOrEmpty(bucketName))
        {
            // Production: AWS S3
            services.AddSingleton<IAmazonS3, AmazonS3Client>();
            services.AddSingleton<IMediaService>(sp =>
                new S3MediaService(
                    sp.GetRequiredService<IAmazonS3>(),
                    bucketName,
                    sp.GetRequiredService<ILogger<S3MediaService>>()));
        }
        else
        {
            // Local development: File system storage
            services.AddSingleton<IMediaService>(sp =>
                new LocalMediaService(sp.GetRequiredService<ILogger<LocalMediaService>>()));
        }
    }
}
