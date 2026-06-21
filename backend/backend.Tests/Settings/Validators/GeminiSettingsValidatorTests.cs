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
        Model = "gemma-4-26b",
        VisionModel = "gemini-2.5-flash",
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
        Assert.Contains("Gemini:ApiKey", result.FailureMessage);
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
        Assert.Contains("Gemini:Model", result.FailureMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_MissingVisionModel_Fails(string? visionModel)
    {
        var settings = ValidSettings();
        settings.VisionModel = visionModel!;

        var result = _validator.Validate(null, settings);

        Assert.True(result.Failed);
        Assert.Contains("Gemini:VisionModel", result.FailureMessage);
    }

    [Fact]
    public void Validate_GemmaVisionModel_Fails()
    {
        var settings = ValidSettings();
        settings.VisionModel = "gemma-4-26b";

        var result = _validator.Validate(null, settings);

        Assert.True(result.Failed);
        Assert.Contains("vision-capable Gemini model", result.FailureMessage);
    }

    [Fact]
    public void Validate_MissingApiKeyModelAndVisionModel_ReportsAllFailures()
    {
        var settings = ValidSettings();
        settings.ApiKey = "";
        settings.Model = "";
        settings.VisionModel = "";

        var result = _validator.Validate(null, settings);

        Assert.True(result.Failed);
        Assert.Contains("Gemini:ApiKey", result.FailureMessage);
        Assert.Contains("Gemini:Model", result.FailureMessage);
        Assert.Contains("Gemini:VisionModel", result.FailureMessage);
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

}
