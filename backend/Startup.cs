using System.Text.Json.Serialization;
using Amazon.S3;
using PostPilot.Api.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PostPilot.Api.Data;
using PostPilot.Api.Services;
using PostPilot.Api.Services.Ai;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Services.Validation;
using PostPilot.Api.Settings;

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

        // Post scheduling: polling-based background worker
        services.AddScoped<IPostScheduler, PostScheduler>();
        services.AddHostedService<PostPublishingWorker>();

        // Configure media service based on environment
        ConfigureMediaService(services);

        // Configure media validation services
        ConfigureMediaValidationServices(services);

        // Configure publishers
        // Use AddHttpClient to register publishers with typed HttpClients
        services.AddHttpClient<FacebookPagePublisher>();
        services.AddScoped<IPostPublisher>(sp => sp.GetRequiredService<FacebookPagePublisher>());

        services.AddHttpClient<InstagramPublisher>();
        services.AddScoped<IPostPublisher>(sp => sp.GetRequiredService<InstagramPublisher>());

        services.AddScoped<IPostPublisherResolver, PostPublisherResolver>();

        // Configure story publishers
        services.AddHttpClient<InstagramStoryPublisher>();
        services.AddScoped<IStoryPublisher>(sp => sp.GetRequiredService<InstagramStoryPublisher>());

        services.AddHttpClient<FacebookStoryPublisher>();
        services.AddScoped<IStoryPublisher>(sp => sp.GetRequiredService<FacebookStoryPublisher>());

        services.AddScoped<IStoryPublisherResolver, StoryPublisherResolver>();

        // Configure feature settings
        var featureSettings = Configuration.GetSection("Features").Get<FeatureSettings>() ?? new FeatureSettings();
        services.AddSingleton(featureSettings);

        // Configure platform selection options
        services.Configure<PlatformSelectionOptions>(
            Configuration.GetSection("Features:PlatformSelection"));

        // Configure Facebook insights service for fetching post engagement
        ConfigureInsightsService(services, featureSettings);

        // Configure AI services (Gemini)
        ConfigureAiServices(services, Configuration);

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

        app.UseHttpsRedirection();

        // Correlation ID middleware: must run before routing so all logs include the id
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseCors("AllowFrontend");
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });
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

    private static void ConfigureMediaValidationServices(IServiceCollection services)
    {
        // Image metadata extractor (using ImageSharp)
        services.AddSingleton<IImageMetadataExtractor, ImageMetadataExtractor>();

        // Video metadata extractor (using ffprobe — must be on PATH)
        services.AddSingleton<IVideoMetadataExtractor, FfprobeVideoMetadataExtractor>();

        // Media validation service
        services.AddScoped<IMediaValidationService, MediaValidationService>();
    }

    private static void ConfigureInsightsService(IServiceCollection services, FeatureSettings featureSettings)
    {
        if (featureSettings.EnableEngagementFetch)
        {
            // Engagement fetch enabled: use real Facebook insights service
            services.AddHttpClient<IFacebookInsightsService, FacebookInsightsService>();
        }
        else
        {
            // Engagement fetch disabled: use no-op implementation
            services.AddScoped<IFacebookInsightsService, DisabledFacebookInsightsService>();
        }
    }

    private static void ConfigureAiServices(IServiceCollection services, IConfiguration configuration)
    {
        // Memory cache for AI responses and rate limiting
        services.AddMemoryCache();

        services.Configure<AiRateLimiterOptions>(
            configuration.GetSection("Ai:RateLimiter"));

        // Gemini settings from environment variables only (no hardcoded defaults)
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty;
        var model = Environment.GetEnvironmentVariable("GEMINI_MODEL")
            ?? throw new InvalidOperationException("Required environment variable 'GEMINI_MODEL' is missing.");

        // Optional separate vision model for image analysis (needed when primary model is Gemma)
        var visionModel = Environment.GetEnvironmentVariable("GEMINI_VISION_MODEL");

        var geminiSettings = new GeminiSettings
        {
            ApiKey = apiKey,
            Model = model,
            VisionModel = visionModel
        };

        services.AddSingleton(geminiSettings);

        // AI Provider settings
        var aiProviderSettings = new AiProviderSettings
        {
            LanguageDetectorProvider = Environment.GetEnvironmentVariable("AI_LANGUAGE_DETECTOR_PROVIDER") ?? "gemini",
            CaptionGeneratorProvider = Environment.GetEnvironmentVariable("AI_CAPTION_GENERATOR_PROVIDER") ?? "gemini"
        };
        services.AddSingleton(aiProviderSettings);

        // Google AI client with typed HttpClient
        // GoogleAiClientRouter automatically routes to GeminiTextClient or GemmaTextClient
        // based on the model name (gemma-* uses Gemma client, others use Gemini client)
        services.AddHttpClient<IGeminiClient, GoogleAiClientRouter>();

        // Register multilingual caption providers via factories (config-driven)
        services.AddSingleton<ILanguageDetector>(sp =>
            LanguageDetectorFactory.Create(
                sp.GetRequiredService<AiProviderSettings>().LanguageDetectorProvider,
                sp));

        services.AddSingleton<ICaptionGenerator>(sp =>
            CaptionGeneratorFactory.Create(
                sp.GetRequiredService<AiProviderSettings>().CaptionGeneratorProvider,
                sp));

        // Register application services
        services.AddScoped<LanguageService>();
        services.AddScoped<CaptionAssistService>();
        services.AddHttpClient<PostTimeSuggestionService>();

        // Rate limiter (in-memory for MVP)
        services.AddSingleton<IAiRateLimiter, InMemoryAiRateLimiter>();

        // Media AI services
        services.AddHttpClient<IAssetResolver, AssetResolver>();

        // Video frame extractor: use FFmpeg if available on PATH
        services.AddSingleton<IVideoFrameExtractor, FFmpegVideoFrameExtractor>();

        services.AddScoped<IMediaAiService, MediaAiService>();
    }
}
