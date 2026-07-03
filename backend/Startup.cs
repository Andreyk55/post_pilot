using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
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

    // Whether the app is running in the Development environment. Drives dev-only conveniences
    // such as allowing http://localhost:* CORS origins. Optional so existing callers/tests that
    // only pass configuration default to the safe (non-development) behavior.
    private readonly bool _isDevelopment;

    public Startup(IConfiguration configuration, IWebHostEnvironment? environment = null)
    {
        Configuration = configuration;
        _isDevelopment = environment?.IsDevelopment() ?? false;
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

        // ── Public frontend origin (absolute URLs emitted by the backend) ────
        services.AddOptions<FrontendOptions>()
            .Bind(Configuration.GetSection(FrontendOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<FrontendOptions>>().Value);

        services.AddHttpContextAccessor();
        services.AddScoped<IUserProvisioningService, UserProvisioningService>();
        services.AddScoped<ICurrentUserProvider, CurrentUserProvider>();
        services.AddScoped<ICurrentWorkspaceProvider, CurrentWorkspaceProvider>();

        ConfigureAuthentication(services);

        // ── CORS ─────────────────────────────────────────────────────────────
        // Localhost dev origins are allowed ONLY in the Development environment;
        // production origins come from Auth:AllowedOrigins (preferred) or legacy
        // Cors:AllowedOrigins. Never AllowAnyOrigin. AllowCredentials is required
        // because both the private-access cookie and the session cookie are
        // sent cross-site.
        var authAllowed = Configuration
            .GetSection($"{AuthOptions.SectionName}:AllowedOrigins")
            .Get<string[]>() ?? Array.Empty<string>();
        var legacyAllowed = Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? Array.Empty<string>();
        var allowedOrigins = authAllowed
            .Concat(legacyAllowed)
            .Concat(new[]
            {
                Configuration["Auth:FrontendUrl"],
                Configuration["Frontend:BaseUrl"],
            })
            .Select(NormalizeOrigin)
            .Where(origin => !string.IsNullOrEmpty(origin))
            .Select(origin => origin!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var isDevelopment = _isDevelopment;
        services.AddCors(options =>
        {
            options.AddPolicy("AllowFrontend", policy =>
            {
                policy.SetIsOriginAllowed(origin => IsOriginAllowed(origin, isDevelopment, allowedOrigins))
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();
            });
        });
    }

    /// <summary>
    /// CORS origin policy. Configured production frontend origins are always allowed; loopback
    /// dev origins (<c>http://localhost:*</c>) are allowed ONLY in the Development environment,
    /// so production never trusts an arbitrary localhost page. Origins are compared after
    /// normalization (scheme+host+port, no trailing slash).
    /// </summary>
    internal static bool IsOriginAllowed(string origin, bool isDevelopment, IReadOnlyCollection<string> allowedOrigins)
    {
        if (string.IsNullOrEmpty(origin))
            return false;

        if (isDevelopment && origin.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase))
            return true;

        return allowedOrigins.Contains(NormalizeOrigin(origin), StringComparer.OrdinalIgnoreCase);
    }

    private void ConfigureAuthentication(IServiceCollection services)
    {
        var authOpts = Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
        var googleOpts = Configuration.GetSection(GoogleAuthOptions.SectionName).Get<GoogleAuthOptions>() ?? new GoogleAuthOptions();

        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                // API endpoints should challenge with the app cookie so
                // unauthenticated fetches get a clean 401 instead of a
                // cross-origin redirect to Google. The OAuth start endpoint
                // explicitly challenges the Google scheme.
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
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

        // Private-by-default: every endpoint requires an authenticated user UNLESS it is
        // explicitly opted out with [AllowAnonymous] (or .AllowAnonymous() for minimal
        // endpoints). This turns a forgotten [Authorize] on a future controller/action from a
        // silent public exposure into a safe 401. The intentionally public endpoints
        // (health probes, the Google login start/callback, the private-access gate, Meta's
        // data-deletion callback + status page, /api/meta/limits, and the local-mode media
        // frame route) carry [AllowAnonymous] and are unaffected.
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });
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
        // Trust forwarded headers only from known proxies — never from any peer. See
        // ConfigureForwardedHeaders for the config keys and the safe private-network default.
        ConfigureForwardedHeaders(forwardedHeaderOptions, Configuration);
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
        app.UseRouting();
        app.UseCors("AllowFrontend");
        // Maps workspace-resolution failures (stale/missing/unauthorized current
        // workspace) to explicit 409/403 responses. Wraps the rest of the pipeline so
        // it catches exceptions thrown from controllers/endpoints.
        app.UseMiddleware<WorkspaceResolutionExceptionMiddleware>();
        // Private-access gate. Runs after CORS so preflight responses still
        // carry the right headers; runs before routing/auth so blocked
        // requests never reach controllers or hit the DB.
        app.UseMiddleware<PrivateAccessMiddleware>();
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
            })).AllowAnonymous(); // liveness probe — must answer without a session (see FallbackPolicy above).

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

        // Gemini settings: ApiKey, Model, and VisionModel are provided by deployment env vars.
        services.AddOptions<GeminiSettings>()
            .Bind(configuration.GetSection(GeminiSettings.SectionName))
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

    private static string? NormalizeOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed;

        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// Restricts which peers may set X-Forwarded-* to a known set instead of trusting any peer.
    /// Explicit config wins:
    ///   <c>ForwardedHeaders:KnownProxies</c>  — array of proxy IPs (e.g. the nginx container IP)
    ///   <c>ForwardedHeaders:KnownNetworks</c> — array of CIDRs (e.g. "172.18.0.0/16")
    /// When neither is configured we fall back to loopback + RFC1918 private ranges. This keeps
    /// the Docker/nginx deployment working out of the box (the API binds to 127.0.0.1 and only
    /// nginx — on loopback or the compose network — reaches it) while still refusing forwarded
    /// headers from any public peer, so HTTPS scheme detection behind nginx is preserved. The
    /// end state is never empty (we never "trust any peer").
    /// </summary>
    internal static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        // Replace the framework defaults (IPv6 loopback only) with a set we fully control.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        var proxies = configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? Array.Empty<string>();
        foreach (var proxy in proxies)
        {
            if (IPAddress.TryParse(proxy?.Trim(), out var ip))
                options.KnownProxies.Add(ip);
        }

        var networks = configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? Array.Empty<string>();
        foreach (var cidr in networks)
        {
            if (System.Net.IPNetwork.TryParse(cidr?.Trim() ?? string.Empty, out var network))
                options.KnownIPNetworks.Add(network);
        }

        // Nothing configured → trust loopback + private (RFC1918 / ULA) ranges so the reverse
        // proxy is trusted regardless of the exact container/gateway IP, but public peers are not.
        if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
        {
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("127.0.0.0"), 8)); // 127.0.0.0/8
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.IPv6Loopback, 128));    // ::1/128
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("10.0.0.0"), 8)); // 10.0.0.0/8
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12)); // 172.16.0.0/12
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("192.168.0.0"), 16)); // 192.168.0.0/16
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("fd00::"), 8));   // fc00::/7 ULA (approx via fd00::/8)
        }
    }
}
