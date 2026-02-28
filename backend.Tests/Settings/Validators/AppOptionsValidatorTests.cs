using Microsoft.Extensions.Options;
using PostPilot.Api.Settings;
using PostPilot.Api.Settings.Validators;
using Xunit;

namespace PostPilot.Api.Tests.Settings.Validators;

public class AppOptionsValidatorTests
{
    private readonly AppOptionsValidator _validator = new();

    private static AppOptions ValidOptions() => new()
    {
        RunMode = "local"
    };

    [Fact]
    public void Validate_LocalMode_ReturnsSuccess()
    {
        var result = _validator.Validate(null, ValidOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ServerMode_ReturnsSuccess()
    {
        var opts = ValidOptions();
        opts.RunMode = "server";

        var result = _validator.Validate(null, opts);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ServerModeCaseInsensitive_ReturnsSuccess()
    {
        var opts = ValidOptions();
        opts.RunMode = "Server";

        var result = _validator.Validate(null, opts);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_MissingRunMode_Fails(string? runMode)
    {
        var opts = ValidOptions();
        opts.RunMode = runMode!;

        var result = _validator.Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains("RunMode", result.FailureMessage);
    }

    [Fact]
    public void Validate_InvalidRunMode_Fails()
    {
        var opts = ValidOptions();
        opts.RunMode = "cloud";

        var result = _validator.Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains("local", result.FailureMessage);
        Assert.Contains("server", result.FailureMessage);
    }

    [Fact]
    public void Validate_ValidPublicUrl_Succeeds()
    {
        var opts = ValidOptions();
        opts.PublicUrl = "https://example.ngrok-free.app";

        var result = _validator.Validate(null, opts);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_InvalidPublicUrl_Fails()
    {
        var opts = ValidOptions();
        opts.PublicUrl = "not-a-url";

        var result = _validator.Validate(null, opts);

        Assert.True(result.Failed);
        Assert.Contains("PublicUrl", result.FailureMessage);
    }

    [Fact]
    public void Validate_NullPublicUrl_Succeeds()
    {
        var opts = ValidOptions();
        opts.PublicUrl = null;

        var result = _validator.Validate(null, opts);
        Assert.True(result.Succeeded);
    }
}
