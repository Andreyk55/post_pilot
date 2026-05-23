using Microsoft.Extensions.Configuration;
using PostPilot.Api.Settings;
using PostPilot.Api.Settings.Validators;
using Xunit;

namespace PostPilot.Api.Tests.Settings.Validators;

public class MediaStorageRunModeValidatorTests
{
    private static MediaStorageRunModeValidator Build(string runMode)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:RunMode"] = runMode })
            .Build();
        return new MediaStorageRunModeValidator(config);
    }

    private static MediaStorageOptions LocalDisk() => new() { Provider = "local-disk" };

    private static MediaStorageOptions S3() => new()
    {
        Provider = "s3-compatible",
        Bucket = "postpilot-media",
        InternalEndpoint = "https://s3.example.com",
        PublicUploadEndpoint = "https://s3.example.com",
        AccessKey = "ak",
        SecretKey = "sk",
    };

    [Fact]
    public void LocalMode_LocalDisk_Succeeds()
    {
        var result = Build("local").Validate(null, LocalDisk());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void LocalMode_S3_Succeeds()
    {
        var result = Build("local").Validate(null, S3());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ServerMode_S3_Succeeds()
    {
        var result = Build("server").Validate(null, S3());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ServerMode_LocalDisk_Fails()
    {
        var result = Build("server").Validate(null, LocalDisk());

        Assert.True(result.Failed);
        Assert.Contains("local-disk", result.FailureMessage);
        Assert.Contains("server", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServerMode_LocalDisk_CaseInsensitive_Fails()
    {
        var result = Build("Server").Validate(null, new MediaStorageOptions { Provider = "Local-Disk" });
        Assert.True(result.Failed);
    }

    [Fact]
    public void MissingRunMode_Treated_AsLocal_Succeeds()
    {
        // App:RunMode missing/empty -> not Server -> validator stays out of the way
        // (AppOptionsValidator handles the "RunMode required" failure separately).
        var result = Build("").Validate(null, LocalDisk());
        Assert.True(result.Succeeded);
    }
}
