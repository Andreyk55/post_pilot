using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PostPilot.Api.Controllers;
using Xunit;

namespace PostPilot.Api.Tests.Auth;

/// <summary>
/// Pins F1: the app is private-by-default. <see cref="Startup.ConfigureServices"/> installs a
/// <c>FallbackPolicy</c> that requires an authenticated user, so any endpoint WITHOUT an explicit
/// opt-out is protected — a forgotten [Authorize] on a future controller/action can no longer be
/// a silent public exposure. The intentionally public endpoints are pinned to carry
/// [AllowAnonymous]; the private controllers are pinned to carry [Authorize] with no anonymous
/// opt-out.
///
/// This project has no WebApplicationFactory (see MediaPublicFetchTests for the same note), so the
/// "unauthenticated → 401" property is asserted at the authorization-policy layer (the fallback
/// policy the middleware enforces) and at the attribute layer, rather than over a live HTTP pipeline.
/// </summary>
public class FallbackAuthorizationPolicyTests
{
    // ── The fallback policy itself ────────────────────────────────────────────────

    [Fact]
    public async Task Fallback_policy_requires_an_authenticated_user()
    {
        var services = new ServiceCollection();
        new Startup(BuildConfig()).ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();
        var fallback = await policyProvider.GetFallbackPolicyAsync();

        // A null fallback policy would mean "public by default" — exactly the state F1 fixes.
        Assert.NotNull(fallback);
        Assert.Contains(fallback!.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
    }

    // ── Intentionally public endpoints keep [AllowAnonymous] ──────────────────────

    [Fact]
    public void Health_probe_is_anonymous()
    {
        AssertMethodAnonymous(typeof(InternalController), nameof(InternalController.Health));
    }

    [Fact]
    public void Google_login_start_and_callback_and_logout_are_anonymous()
    {
        AssertMethodAnonymous(typeof(AuthController), nameof(AuthController.GoogleStart));
        AssertMethodAnonymous(typeof(AuthController), nameof(AuthController.GoogleCallback));
        AssertMethodAnonymous(typeof(AuthController), nameof(AuthController.Logout));
    }

    [Fact]
    public void Auth_me_stays_private()
    {
        // /api/auth/me must NOT be anonymous — it reports the logged-in user.
        var method = typeof(AuthController).GetMethod(nameof(AuthController.Me))!;
        Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(method.GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public void Private_access_gate_controller_is_anonymous()
    {
        // The whole controller (login/me/logout) must be reachable before any session exists.
        Assert.NotNull(typeof(PrivateAccessController).GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public void Meta_data_deletion_controller_is_anonymous()
    {
        // Meta's servers POST the callback with no cookie; the status page is public.
        Assert.NotNull(typeof(DataDeletionController).GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public void Meta_controller_has_no_anonymous_actions()
    {
        // /api/meta/limits (the only [AllowAnonymous] action this controller ever had) was
        // removed as unused — every remaining Meta endpoint must require authentication.
        var anonymousActions = typeof(MetaController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() != null)
            .Select(m => m.Name)
            .ToList();

        Assert.Empty(anonymousActions);
    }

    [Fact]
    public void Media_frames_route_is_anonymous()
    {
        AssertMethodAnonymous(typeof(MediaController), nameof(MediaController.GetFrame));
    }

    // ── Private controllers stay private (no anonymous opt-out) ───────────────────

    public static IEnumerable<object[]> PrivateControllers() => new[]
    {
        new object[] { typeof(PostsController) },
        new object[] { typeof(WorkspacesController) },
        new object[] { typeof(MediaController) },
        new object[] { typeof(MetaController) },
        new object[] { typeof(AiTextController) },
        new object[] { typeof(AiMediaController) },
        new object[] { typeof(AiVoiceProfileController) },
        new object[] { typeof(AccountController) },
        new object[] { typeof(SupportController) },
    };

    [Theory]
    [MemberData(nameof(PrivateControllers))]
    public void Data_controllers_require_authorization(Type controller)
    {
        // Controller-level [Authorize] and no controller-level [AllowAnonymous]. Combined with the
        // fallback policy, this keeps posts / workspaces / media / AI / account / support / Meta
        // account+assets+pages private.
        Assert.NotNull(controller.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Null(controller.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static void AssertMethodAnonymous(Type controller, string methodName)
    {
        var method = controller.GetMethod(methodName);
        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    private static IConfiguration BuildConfig()
    {
        // Mirrors AuthenticationConfigurationTests: the minimal config Startup needs to build the
        // service graph (no DB connection is opened here — we only resolve the policy provider).
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=postpilot_tests;Username=test;Password=test",
            ["App:RunMode"] = "local",
            ["Meta:AppId"] = "test-meta-app",
            ["Meta:AppSecret"] = "test-meta-secret",
            ["Meta:RedirectUri"] = "http://localhost:5173/oauth/meta/callback",
            ["Gemini:ApiKey"] = "test-gemini-key",
            ["Gemini:Model"] = "gemini-test",
            ["Gemini:VisionModel"] = "gemini-vision-test",
            ["Gemini:BaseUrl"] = "https://generativelanguage.googleapis.com/v1beta",
            ["Gemini:TimeoutSeconds"] = "30",
            ["MediaStorage:Provider"] = "local-disk",
            ["MediaStorage:PresignedUploadExpirationMinutes"] = "15",
            ["GoogleAuth:ClientId"] = "test-google-client",
            ["GoogleAuth:ClientSecret"] = "test-google-secret",
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
