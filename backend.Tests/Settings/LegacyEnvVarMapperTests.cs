using Microsoft.Extensions.Configuration;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Settings;

public class LegacyEnvVarMapperTests
{
    [Fact]
    public void AddLegacyEnvironmentVariables_MapsLegacyVarsWhenCanonicalNotSet()
    {
        // Set a legacy env var
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "test-key-from-legacy");
        Environment.SetEnvironmentVariable("Gemini__ApiKey", null); // Ensure canonical is not set

        try
        {
            var builder = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddLegacyEnvironmentVariables();

            var config = builder.Build();

            Assert.Equal("test-key-from-legacy", config["Gemini:ApiKey"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
        }
    }

    [Fact]
    public void AddLegacyEnvironmentVariables_CanonicalWinsOverLegacy()
    {
        // Set both legacy and canonical
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "legacy-key");
        Environment.SetEnvironmentVariable("Gemini__ApiKey", "canonical-key");

        try
        {
            var builder = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddLegacyEnvironmentVariables();

            var config = builder.Build();

            // Canonical should win (it's set via AddEnvironmentVariables before legacy mapping)
            Assert.Equal("canonical-key", config["Gemini:ApiKey"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
            Environment.SetEnvironmentVariable("Gemini__ApiKey", null);
        }
    }

    [Fact]
    public void AddLegacyEnvironmentVariables_IgnoresUnsetLegacyVars()
    {
        // Ensure legacy vars are not set
        Environment.SetEnvironmentVariable("META_APP_ID", null);

        var builder = new ConfigurationBuilder()
            .AddLegacyEnvironmentVariables();

        var config = builder.Build();

        Assert.Null(config["Meta:AppId"]);
    }

    [Fact]
    public void AddLegacyEnvironmentVariables_MapsAllKnownVars()
    {
        var legacyVars = new Dictionary<string, (string envName, string configKey)>
        {
            ["APP_RUN_MODE"] = ("APP_RUN_MODE", "App:RunMode"),
            ["PUBLIC_URL"] = ("PUBLIC_URL", "App:PublicUrl"),
            ["META_APP_ID"] = ("META_APP_ID", "Meta:AppId"),
            ["META_APP_SECRET"] = ("META_APP_SECRET", "Meta:AppSecret"),
            ["GEMINI_API_KEY"] = ("GEMINI_API_KEY", "Gemini:ApiKey"),
            ["GEMINI_MODEL"] = ("GEMINI_MODEL", "Gemini:Model"),
            ["GEMINI_VISION_MODEL"] = ("GEMINI_VISION_MODEL", "Gemini:VisionModel"),
        };

        foreach (var (_, (envName, _)) in legacyVars)
        {
            Environment.SetEnvironmentVariable(envName, $"test-{envName}");
        }

        try
        {
            var builder = new ConfigurationBuilder()
                .AddLegacyEnvironmentVariables();

            var config = builder.Build();

            foreach (var (_, (envName, configKey)) in legacyVars)
            {
                Assert.Equal($"test-{envName}", config[configKey]);
            }
        }
        finally
        {
            foreach (var (_, (envName, _)) in legacyVars)
            {
                Environment.SetEnvironmentVariable(envName, null);
            }
        }
    }
}
