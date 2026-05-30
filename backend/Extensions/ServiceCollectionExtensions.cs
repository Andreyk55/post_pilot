using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PostPilot.Api.Data;
using PostPilot.Api.Enums;
using PostPilot.Api.Services;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Providers;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Services.Validation;
using PostPilot.Api.Settings;
using PostPilot.Api.Settings.Validators;

namespace PostPilot.Api.Extensions;

/// <summary>
/// Shared DI registrations used by both the API and the Worker.
/// Neither AddControllers nor web-only services are registered here.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core PostPilot services: DbContext, options, publishers,
    /// scheduler, media, and insights.  Both the API and the Worker call this.
    /// </summary>
    public static IServiceCollection AddPostPilotCoreServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Database ────────────────────────────────────────────────────────
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException(
                "Database connection string not found. Set 'ConnectionStrings:DefaultConnection' in appsettings.json " +
                "or 'ConnectionStrings__DefaultConnection' environment variable.");
        }

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString));

        // ── App options ──────────────────────────────────────────────────────
        services.AddOptions<AppOptions>()
            .Bind(configuration.GetSection(AppOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AppOptions>, AppOptionsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AppOptions>>().Value);

        // ── Meta OAuth options ───────────────────────────────────────────────
        services.AddOptions<MetaOptions>()
            .Bind(configuration.GetSection(MetaOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MetaOptions>, MetaOptionsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MetaOptions>>().Value);

        // Register Meta OAuth service
        services.AddHttpClient<IMetaOAuthService, MetaOAuthService>();

        // ── Provider lifecycle (generic + per-provider handlers) ─────────────
        // Generic orchestrator that enforces "one active connection per
        // (workspace, provider)" and routes disconnects to provider-specific
        // asset/post cleanup handlers.
        services.AddScoped<IProviderConnectionService, ProviderConnectionService>();
        services.AddScoped<IProviderLifecycleHandler, MetaProviderLifecycleHandler>();

        // ── Post scheduling ──────────────────────────────────────────────────
        services.AddScoped<IPostScheduler, PostScheduler>();
        // NOTE: AddHostedService<PostPublishingWorker>() is NOT here.
        //       The Worker executable registers it; the API does not.

        // ── Publishing options ───────────────────────────────────────────────
        services.AddOptions<PublishingOptions>()
            .Bind(configuration.GetSection(PublishingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PublishingOptions>, PublishingOptionsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PublishingOptions>>().Value);

        // ── Media options ────────────────────────────────────────────────────
        services.AddOptions<MediaOptions>()
            .Bind(configuration.GetSection(MediaOptions.SectionName))
            .PostConfigure<IOptions<AppOptions>>((options, appOpts) =>
            {
                if (!string.IsNullOrEmpty(appOpts.Value.PublicUrl) && string.IsNullOrEmpty(options.PublicUrl))
                {
                    options.PublicUrl = appOpts.Value.PublicUrl;
                }
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MediaOptions>, MediaOptionsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MediaOptions>>().Value);

        // ── Media storage backend options ────────────────────────────────────
        services.AddOptions<MediaStorageOptions>()
            .Bind(configuration.GetSection(MediaStorageOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MediaStorageOptions>, MediaStorageOptionsValidator>();
        // Cross-validator: reject local-disk in Server mode (silently 404s uploads).
        services.AddSingleton<IValidateOptions<MediaStorageOptions>, MediaStorageRunModeValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MediaStorageOptions>>().Value);

        // ── Feature settings ─────────────────────────────────────────────────
        var featureSettings = configuration.GetSection("Features").Get<FeatureSettings>() ?? new FeatureSettings();
        services.AddSingleton(featureSettings);

        services.AddOptions<PlatformSelectionOptions>()
            .Bind(configuration.GetSection("Features:PlatformSelection"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PlatformSelectionOptions>, PlatformSelectionOptionsValidator>();

        // Meta API URL constants
        services.AddSingleton(new MetaApiOptions());

        // ── Publishers ───────────────────────────────────────────────────────
        services.AddHttpClient<FacebookPagePublisher>();
        services.AddScoped<IPostPublisher>(sp => sp.GetRequiredService<FacebookPagePublisher>());

        services.AddHttpClient<InstagramPublisher>();
        services.AddScoped<IPostPublisher>(sp => sp.GetRequiredService<InstagramPublisher>());

        services.AddScoped<IPostPublisherResolver, PostPublisherResolver>();

        services.AddHttpClient<InstagramStoryPublisher>();
        services.AddScoped<IStoryPublisher>(sp => sp.GetRequiredService<InstagramStoryPublisher>());

        services.AddHttpClient<FacebookStoryPublisher>();
        services.AddScoped<IStoryPublisher>(sp => sp.GetRequiredService<FacebookStoryPublisher>());

        services.AddScoped<IStoryPublisherResolver, StoryPublisherResolver>();

        // ── Media service & validation ───────────────────────────────────────
        ConfigureMediaService(services);
        ConfigureMediaValidationServices(services);

        // ── Insights service (feature-flagged) ───────────────────────────────
        ConfigureInsightsService(services, featureSettings);

        return services;
    }

    private static void ConfigureMediaService(IServiceCollection services)
    {
        services.AddSingleton<IMediaStorageProvider>(sp =>
        {
            // MediaStorage.Provider is the authoritative switch for the storage backend.
            // AppOptions.RunMode is kept for legacy callers but does not select storage.
            var storageOpts = sp.GetRequiredService<MediaStorageOptions>();

            return storageOpts.ProviderType switch
            {
                MediaStorageProviderType.Supabase => new SupabaseMediaStorageProvider(
                    storageOpts,
                    sp.GetRequiredService<ILogger<SupabaseMediaStorageProvider>>()),

                MediaStorageProviderType.S3Compatible => new S3CompatibleMediaStorageProvider(
                    storageOpts,
                    sp.GetRequiredService<ILogger<S3CompatibleMediaStorageProvider>>()),

                // Default to local-disk for development without object storage.
                _ => new LocalDiskMediaStorageProvider(
                    sp.GetRequiredService<ILogger<LocalDiskMediaStorageProvider>>(),
                    sp.GetRequiredService<MediaOptions>().EffectiveBaseUrl),
            };
        });

        services.AddSingleton<IMediaService>(sp =>
        {
            var runMode = sp.GetRequiredService<AppOptions>().RunModeEnum;
            var mediaOpts = sp.GetRequiredService<MediaOptions>();
            var storageOpts = sp.GetRequiredService<MediaStorageOptions>();
            // Default publish-URL lifetime: long enough for Meta to fetch + retry, short
            // enough to limit damage if a URL leaks. 1h matches the Supabase default.
            var defaultPublishExpiry = storageOpts.IsSupabase
                ? TimeSpan.FromSeconds(storageOpts.Supabase.SignedUrlExpirySeconds)
                : TimeSpan.FromHours(1);
            return new MediaService(
                sp.GetRequiredService<IMediaStorageProvider>(),
                storageOpts,
                runMode,
                sp.GetRequiredService<ILogger<MediaService>>(),
                uploadUrlExpiration: TimeSpan.FromMinutes(mediaOpts.UploadUrlExpirationMinutes),
                maxImageFileSizeBytes: mediaOpts.MaxImageFileSizeBytes,
                maxVideoFileSizeBytes: mediaOpts.MaxVideoFileSizeBytes,
                publishingBaseUrl: mediaOpts.EffectiveBaseUrl,
                defaultPublishingUrlExpiration: defaultPublishExpiry);
        });

        services.AddScoped<IMediaUploadService, MediaUploadService>();
    }

    private static void ConfigureMediaValidationServices(IServiceCollection services)
    {
        services.AddSingleton<IImageMetadataExtractor, ImageMetadataExtractor>();
        services.AddSingleton<IVideoMetadataExtractor, FfprobeVideoMetadataExtractor>();
        services.AddScoped<IMediaValidationService, MediaValidationService>();
    }

    private static void ConfigureInsightsService(IServiceCollection services, FeatureSettings featureSettings)
    {
        if (featureSettings.EnableEngagementFetch)
        {
            services.AddHttpClient<IFacebookInsightsService, FacebookInsightsService>();
        }
        else
        {
            services.AddScoped<IFacebookInsightsService, DisabledFacebookInsightsService>();
        }
    }
}
