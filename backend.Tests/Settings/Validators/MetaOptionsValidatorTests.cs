using Microsoft.Extensions.Options;
using PostPilot.Api.Settings;
using PostPilot.Api.Settings.Validators;
using Xunit;

namespace PostPilot.Api.Tests.Settings.Validators;

public class MetaOptionsValidatorTests
{
    private readonly MetaOptionsValidator _validator = new();

    private static MetaOptions ValidOptions() => new()
    {
        AppId = "123456789",
        AppSecret = "abc123secret",
        RedirectUri = "https://localhost:5122/api/meta/callback"
    };

    [Fact]
    public void Validate_AllValid_ReturnsSuccess()
    {
        var result = _validator.Validate(null, ValidOptions());
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_MissingAppId_Fails(string? appId)
    {
        var opts = ValidOptions();
        opts.AppId = appId!;

        var result = _validator.Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains("AppId", result.FailureMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_MissingAppSecret_Fails(string? appSecret)
    {
        var opts = ValidOptions();
        opts.AppSecret = appSecret!;

        var result = _validator.Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains("AppSecret", result.FailureMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_MissingRedirectUri_Fails(string? redirectUri)
    {
        var opts = ValidOptions();
        opts.RedirectUri = redirectUri!;

        var result = _validator.Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains("RedirectUri", result.FailureMessage);
    }

    [Fact]
    public void Validate_InvalidRedirectUri_Fails()
    {
        var opts = ValidOptions();
        opts.RedirectUri = "not-a-url";

        var result = _validator.Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains("absolute URI", result.FailureMessage);
    }

    [Fact]
    public void Validate_MissingAppIdAndSecret_ReportsBothFailures()
    {
        var opts = ValidOptions();
        opts.AppId = "";
        opts.AppSecret = "";

        var result = _validator.Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains("AppId", result.FailureMessage);
        Assert.Contains("AppSecret", result.FailureMessage);
    }
}
