using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
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
using PostPilot.Api.Settings.Validators;

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
        // Use ConnectionStrings__DefaultConnection env var to override (standard .NET convention).
        var connectionString = Configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException(
                "Database connection string not found. Set 'ConnectionStrings:DefaultConnection' in appsettings.json " +
                "or 'ConnectionStrings__DefaultConnection' environment variable.");
        }

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString));

        // Configure Meta OAuth settings (secrets from environment variables only)
        var appId = Environment.GetEnvironmentVariable("META_APP_ID") 
            ?? throw new InvalidOperationException("Required environment variable 'META_APP_ID' is missing.");

        var appSecret = Environment.GetEnvironmentVariable("META_APP_SECRET") 
            ?? throw new InvalidOperationException("Required environment variable 'META_APP_SECRET' is missing.");

        // For the RedirectUri, check config (use Meta__RedirectUri env var to override).
        var redirectUri = Configuration["Meta:RedirectUri"] 
            ?? throw new InvalidOperationException(
                "Meta:RedirectUri not found. Set it in appsettings.json or via 'Meta__RedirectUri' environment variable.");

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

        // Configure platform selection options (validated at startup)
        services.AddOptions<PlatformSelectionOptions>()
            .Bind(Configuration.GetSection("Features:PlatformSelection"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PlatformSelectionOptions>, PlatformSelectionOptionsValidator>();

        // Configure Meta API options (Graph API base URL, OAuth dialog URL)
        services.AddOptions<MetaApiOptions>()
            .Bind(Configuration.GetSection(MetaApiOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MetaApiOptions>, MetaApiOptionsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MetaApiOptions>>().Value);

        // Configure publishing options (polling intervals, retry settings, URL expirations)
        services.AddOptions<PublishingOptions>()
            .Bind(Configuration.GetSection(PublishingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PublishingOptions>, PublishingOptionsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PublishingOptions>>().Value);

        // Configure media options (file size limits, upload expiration, local server URL)
        services.AddOptions<MediaOptions>()
            .Bind(Configuration.GetSection(MediaOptions.SectionName))
            .PostConfigure(options =>
            {
                // TODO: Deprecate PUBLIC_URL env var — prefer Media__PublicUrl or Media:PublicUrl config key.
                var legacyPublicUrl = Environment.GetEnvironmentVariable("PUBLIC_URL");
                if (!string.IsNullOrEmpty(legacyPublicUrl) && string.IsNullOrEmpty(options.PublicUrl))
                {
                    options.PublicUrl = legacyPublicUrl;
                }
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MediaOptions>, MediaOptionsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MediaOptions>>().Value);

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
        var runModeStr = Environment.GetEnvironmentVariable("APP_RUN_MODE") ?? "local";
        var runMode = runModeStr.Equals("server", StringComparison.OrdinalIgnoreCase)
            ? Enums.AppRunMode.Server
            : Enums.AppRunMode.Local;

        // Register the run mode so other services can query it
        services.AddSingleton(typeof(Enums.AppRunMode), runMode);

        if (runMode == Enums.AppRunMode.Server)
        {
            // Server mode: register stub provider — implement IMediaStorageProvider to add a real provider.
            // Every method throws NotImplementedException until implemented.
            services.AddSingleton<IMediaStorageProvider, ServerMediaStorageProvider>();
        }
        else
        {
            // Local mode: filesystem storage
            services.AddSingleton<IMediaStorageProvider>(sp =>
            {
                var mediaOpts = sp.GetRequiredService<MediaOptions>();
                return new LocalDiskMediaStorageProvider(
                    sp.GetRequiredService<ILogger<LocalDiskMediaStorageProvider>>(),
                    mediaOpts.EffectiveBaseUrl);
            });
        }

        // Register the unified MediaService
        // Use Media__UploadUrlExpirationMinutes env var to override the config value.
        services.AddSingleton<IMediaService>(sp =>
        {
            var mediaOpts = sp.GetRequiredService<MediaOptions>();
            return new MediaService(
                sp.GetRequiredService<IMediaStorageProvider>(),
                runMode,
                sp.GetRequiredService<ILogger<MediaService>>(),
                uploadUrlExpiration: TimeSpan.FromMinutes(mediaOpts.UploadUrlExpirationMinutes),
                maxImageFileSizeBytes: mediaOpts.MaxImageFileSizeBytes,
                maxVideoFileSizeBytes: mediaOpts.MaxVideoFileSizeBytes);
        });
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

        // AI rate limiter options (validated at startup)
        services.AddOptions<AiRateLimiterOptions>()
            .Bind(configuration.GetSection("Ai:RateLimiter"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AiRateLimiterOptions>, AiRateLimiterOptionsValidator>();

        // AI cache duration options (validated at startup)
        services.AddOptions<AiCacheOptions>()
            .Bind(configuration.GetSection(AiCacheOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AiCacheOptions>, AiCacheOptionsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AiCacheOptions>>().Value);

        // Gemini settings: non-secret defaults from config, secrets from env vars (validated at startup)
        services.AddOptions<GeminiSettings>()
            .Bind(configuration.GetSection(GeminiSettings.SectionName))
            .PostConfigure(settings =>
            {
                settings.ApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty;
                settings.Model = Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? string.Empty;
                settings.VisionModel = Environment.GetEnvironmentVariable("GEMINI_VISION_MODEL");
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<GeminiSettings>, GeminiSettingsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<GeminiSettings>>().Value);

        // AI Provider settings: bound from config (use Ai__Providers__LanguageDetectorProvider etc. env vars to override)
        services.AddOptions<AiProviderSettings>()
            .Bind(configuration.GetSection(AiProviderSettings.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AiProviderSettings>, AiProviderSettingsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AiProviderSettings>>().Value);

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
