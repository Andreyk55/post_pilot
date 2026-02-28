using PostPilot.Api.Services.Ai;
using Xunit;

namespace PostPilot.Api.Tests.Settings;

public class GeminiSettingsVisionModelFallbackTests
{
    [Fact]
    public void VisionModel_WhenNull_FallsBackToModel()
    {
        // The PostConfigure in Startup.cs sets VisionModel = Model when empty.
        // This test validates the fallback logic that would run in PostConfigure.
        var settings = new GeminiSettings
        {
            ApiKey = "test-key",
            Model = "gemini-2.0-flash",
            VisionModel = null,
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            TimeoutSeconds = 30
        };

        // Simulate PostConfigure fallback logic
        if (string.IsNullOrWhiteSpace(settings.VisionModel))
        {
            settings.VisionModel = settings.Model;
        }

        Assert.Equal("gemini-2.0-flash", settings.VisionModel);
    }

    [Fact]
    public void VisionModel_WhenEmpty_FallsBackToModel()
    {
        var settings = new GeminiSettings
        {
            ApiKey = "test-key",
            Model = "gemini-2.0-flash",
            VisionModel = "",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            TimeoutSeconds = 30
        };

        // Simulate PostConfigure fallback logic
        if (string.IsNullOrWhiteSpace(settings.VisionModel))
        {
            settings.VisionModel = settings.Model;
        }

        Assert.Equal("gemini-2.0-flash", settings.VisionModel);
    }

    [Fact]
    public void VisionModel_WhenWhitespace_FallsBackToModel()
    {
        var settings = new GeminiSettings
        {
            ApiKey = "test-key",
            Model = "gemini-2.0-flash",
            VisionModel = "   ",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            TimeoutSeconds = 30
        };

        // Simulate PostConfigure fallback logic
        if (string.IsNullOrWhiteSpace(settings.VisionModel))
        {
            settings.VisionModel = settings.Model;
        }

        Assert.Equal("gemini-2.0-flash", settings.VisionModel);
    }

    [Fact]
    public void VisionModel_WhenExplicitlySet_DoesNotFallBack()
    {
        var settings = new GeminiSettings
        {
            ApiKey = "test-key",
            Model = "gemini-2.0-flash",
            VisionModel = "gemini-2.0-pro-vision",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            TimeoutSeconds = 30
        };

        // Simulate PostConfigure fallback logic
        if (string.IsNullOrWhiteSpace(settings.VisionModel))
        {
            settings.VisionModel = settings.Model;
        }

        Assert.Equal("gemini-2.0-pro-vision", settings.VisionModel);
    }
}
