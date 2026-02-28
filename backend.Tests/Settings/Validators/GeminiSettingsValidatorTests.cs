using Microsoft.Extensions.Options;
using PostPilot.Api.Services.Ai;
using PostPilot.Api.Settings.Validators;
using Xunit;

namespace PostPilot.Api.Tests.Settings.Validators;

public class GeminiSettingsValidatorTests
{
    private readonly GeminiSettingsValidator _validator = new();

    private static GeminiSettings ValidSettings() => new()
    {
        ApiKey = "test-api-key",
        Model = "gemini-2.0-flash",
        BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
        TimeoutSeconds = 30
    };

    [Fact]
    public void Validate_AllValid_ReturnsSuccess()
    {
        var result = _validator.Validate(null, ValidSettings());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_MissingApiKey_Fails(string? apiKey)
    {
        var settings = ValidSettings();
        settings.ApiKey = apiKey!;

        var result = _validator.Validate(null, settings);

        Assert.True(result.Failed);
        Assert.Contains("GEMINI_API_KEY", result.FailureMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_MissingModel_Fails(string? model)
    {
        var settings = ValidSettings();
        settings.Model = model!;

        var result = _validator.Validate(null, settings);

        Assert.True(result.Failed);
        Assert.Contains("GEMINI_MODEL", result.FailureMessage);
    }

    [Fact]
    public void Validate_MissingApiKeyAndModel_ReportsBothFailures()
    {
        var settings = ValidSettings();
        settings.ApiKey = "";
        settings.Model = "";

        var result = _validator.Validate(null, settings);

        Assert.True(result.Failed);
        Assert.Contains("GEMINI_API_KEY", result.FailureMessage);
        Assert.Contains("GEMINI_MODEL", result.FailureMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_MissingBaseUrl_Fails(string? baseUrl)
    {
        var settings = ValidSettings();
        settings.BaseUrl = baseUrl!;

        var result = _validator.Validate(null, settings);

        Assert.True(result.Failed);
        Assert.Contains("BaseUrl", result.FailureMessage);
    }

    [Fact]
    public void Validate_InvalidBaseUrl_Fails()
    {
        var settings = ValidSettings();
        settings.BaseUrl = "not-a-url";

        var result = _validator.Validate(null, settings);

        Assert.True(result.Failed);
        Assert.Contains("absolute URI", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_InvalidTimeoutSeconds_Fails(int timeout)
    {
        var settings = ValidSettings();
        settings.TimeoutSeconds = timeout;

        var result = _validator.Validate(null, settings);

        Assert.True(result.Failed);
        Assert.Contains("TimeoutSeconds", result.FailureMessage);
    }

    [Fact]
    public void Validate_VisionModelOptional_SucceedsWhenNull()
    {
        var settings = ValidSettings();
        settings.VisionModel = null;

        var result = _validator.Validate(null, settings);

        Assert.True(result.Succeeded);
    }
}
