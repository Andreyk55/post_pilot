using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace PostPilot.Api.Tests.Auth;

public class AuthenticationConfigurationTests
{
    [Fact]
    public void ApiChallengeDefaultsToCookieScheme()
    {
        var services = new ServiceCollection();
        var startup = new Startup(BuildConfig());

        startup.ConfigureServices(services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;

        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, options.DefaultScheme);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, options.DefaultChallengeScheme);
    }

    private static IConfiguration BuildConfig()
    {
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
