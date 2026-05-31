using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using PostPilot.Api.Extensions;
using PostPilot.Api.Middleware;
using PostPilot.Api.Services.Ai;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.PrivateAccess;
using PostPilot.Api.Settings;
using PostPilot.Api.Settings.Validators;

namespace PostPilot.Api;

public class Startup
{
    /// <summary>
    /// Scheme name for the temporary cookie that holds the Google identity
    /// during the OAuth round-trip. Distinct from the main app session cookie
    /// scheme so the two never collide.
    /// </summary>
    public const string ExternalAuthScheme = "PostPilotExternal";

    public IConfiguration Configuration { get; }

    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        // ── Web / API-specific ───────────────────────────────────────────────
        services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            });

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        // ── Core services (shared with Worker) ───────────────────────────────
        // Registers DbContext, options, publishers, scheduler, media, insights.
        // NOTE: AddHostedService<PostPublishingWorker>() is NOT called here.
        //       The worker container registers it.  See PostPilot.Worker/Program.cs.
        services.AddPostPilotCoreServices(Configuration);

        // ── AI services (API-only) ────────────────────────────────────────────
        ConfigureAiServices(services, Configuration);

        // ── Private-access gate (temporary single-password protection) ───────
        services.AddOptions<PrivateAccessOptions>()
            .Bind(Configuration.GetSection(PrivateAccessOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PrivateAccessOptions>>().Value);
        services.AddSingleton<IPrivateAccessTokenService, PrivateAccessTokenService>();

        // ── Real-user auth (Google OAuth + cookie session) ───────────────────
        services.AddOptions<AuthOptions>()
            .Bind(Configuration.GetSection(AuthOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AuthOptions>>().Value);

        services.AddOptions<GoogleAuthOptions>()
            .Bind(Configuration.GetSection(GoogleAuthOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<GoogleAuthOptions>>().Value);

        services.AddHttpContextAccessor();
        services.AddScoped<IUserProvisioningService, UserProvisioningService>();
        services.AddScoped<ICurrentUserProvider, CurrentUserProvider>();
        services.AddScoped<ICurrentWorkspaceProvider, CurrentWorkspaceProvider>();

        ConfigureAuthentication(services);

        // ── CORS ─────────────────────────────────────────────────────────────
        // Localhost dev origins are always allowed; production origins come
        // from Auth:AllowedOrigins (preferred) or legacy Cors:AllowedOrigins.
        // Never AllowAnyOrigin in production. AllowCredentials is required
        // because both the private-access cookie and the session cookie are
        // sent cross-site.
        var authAllowed = Configuration
            .GetSection($"{AuthOptions.SectionName}:AllowedOrigins")
            .Get<string[]>() ?? Array.Empty<string>();
        var legacyAllowed = Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? Array.Empty<string>();
        var allowedOrigins = authAllowed.Concat(legacyAllowed).Distinct().ToArray();

        services.AddCors(options =>
        {
            options.AddPolicy("AllowFrontend", policy =>
            {
                policy.SetIsOriginAllowed(origin =>
                          origin.StartsWith("http://localhost:") ||
                          Array.IndexOf(allowedOrigins, origin) >= 0)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();
            });
        });
    }

    private void ConfigureAuthentication(IServiceCollection services)
    {
        var authOpts = Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
        var googleOpts = Configuration.GetSection(GoogleAuthOptions.SectionName).Get<GoogleAuthOptions>() ?? new GoogleAuthOptions();

        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
            })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = authOpts.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
                // Cross-site Vercel → VPS deployments need SameSite=None+Secure.
                // Locally on plain HTTP we fall back to Lax so the cookie sticks.
                options.Cookie.SameSite = authOpts.RequireHttpsCookies
                    ? SameSiteMode.None
                    : SameSiteMode.Lax;
                options.Cookie.SecurePolicy = authOpts.RequireHttpsCookies
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;
                if (!string.IsNullOrEmpty(authOpts.CookieDomain))
                {
                    options.Cookie.Domain = authOpts.CookieDomain;
                }
                options.ExpireTimeSpan = TimeSpan.FromDays(30);
                options.SlidingExpiration = true;

                // API endpoints should never 302-redirect to a login page —
                // return clean status codes so the SPA can react.
                options.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddCookie(ExternalAuthScheme, options =>
            {
                // Short-lived cookie that stores the external (Google) identity
                // between the redirect-out and the redirect-back. The real app
                // session cookie is issued by AuthController after provisioning.
                options.Cookie.Name = "postpilot_ext_google";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = authOpts.RequireHttpsCookies
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddGoogle(GoogleDefaults.AuthenticationScheme, options =>
            {
                options.ClientId = googleOpts.ClientId;
                options.ClientSecret = googleOpts.ClientSecret;
                // Google handler signs the user into this temporary cookie
                // scheme; AuthController re-signs them under the real app
                // cookie after find-or-create.
                options.SignInScheme = ExternalAuthScheme;
                options.CallbackPath = "/signin-google";
                options.SaveTokens = false; // we do not need Google's access token
                // Google's userinfo returns "picture" as a top-level string URL —
                // map it into a stable claim type the controller looks up.
                options.ClaimActions.MapJsonKey("urn:google:picture", "picture");
            });

        services.AddAuthorization();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        // Honor X-Forwarded-* from the reverse proxy so Request.Scheme/Host
        // reflect the public origin, not the internal http://api:5122. Must
        // run before anything that inspects scheme/host.
        var forwardedHeaderOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor
                             | ForwardedHeaders.XForwardedProto
                             | ForwardedHeaders.XForwardedHost,
        };
        // Trust any proxy on the compose network — we don't know nginx's
        // container IP up front and the API is not directly reachable.
        forwardedHeaderOptions.KnownNetworks.Clear();
        forwardedHeaderOptions.KnownProxies.Clear();
        app.UseForwardedHeaders(forwardedHeaderOptions);

        // Configure the HTTP request pipeline.
        if (env.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // HTTPS redirect is controlled by config so plain-HTTP deployments
        // (e.g. behind an nginx that hasn't been given a cert yet) don't bounce
        // every request to a non-existent https://. Defaults to ON.
        var enableHttpsRedirect = Configuration.GetValue<bool?>("App:EnableHttpsRedirect") ?? true;
        if (enableHttpsRedirect)
        {
            app.UseHttpsRedirection();
        }

        // Correlation ID middleware: must run before routing so all logs include the id
        app.UseMiddleware<CorrelationIdMiddleware>();
        // Maps workspace-resolution failures (stale/missing/unauthorized current
        // workspace) to explicit 409/403 responses. Wraps the rest of the pipeline so
        // it catches exceptions thrown from controllers/endpoints.
        app.UseMiddleware<WorkspaceResolutionExceptionMiddleware>();
        app.UseCors("AllowFrontend");
        // Private-access gate. Runs after CORS so preflight responses still
        // carry the right headers; runs before routing/auth so blocked
        // requests never reach controllers or hit the DB.
        app.UseMiddleware<PrivateAccessMiddleware>();
        app.UseRouting();
        // Authentication / authorization for real-user endpoints. Order:
        // routing → auth → endpoints, so [Authorize] controllers see the
        // resolved ClaimsPrincipal.
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints =>
        {
            // Liveness probe used by host nginx and uptime checks. Cheap —
            // does NOT touch the DB. For a DB-aware check, use /api/internal/health.
            endpoints.MapGet("/health", () => Results.Ok(new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow
            }));

            endpoints.MapControllers();
        });
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

        // Gemini settings: all from "Gemini" section (ApiKey from env var, rest from appsettings)
        services.AddOptions<GeminiSettings>()
            .Bind(configuration.GetSection(GeminiSettings.SectionName))
            .PostConfigure(settings =>
            {
                if (string.IsNullOrWhiteSpace(settings.VisionModel))
                    settings.VisionModel = settings.Model;
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<GeminiSettings>, GeminiSettingsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<GeminiSettings>>().Value);

        // AI Provider settings
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
