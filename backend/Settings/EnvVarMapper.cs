namespace PostPilot.Api.Settings;

/// <summary>
/// Maps flat environment variable names to canonical config keys.
/// Only the 4 required env vars have legacy flat-name support.
/// </summary>
public static class EnvVarMapper
{
    private static readonly (string FlatEnvVar, string ConfigKey)[] Mappings =
    [
        ("APP_RUN_MODE",    "App:RunMode"),
        ("META_APP_ID",     "Meta:AppId"),
        ("META_APP_SECRET", "Meta:AppSecret"),
        ("GEMINI_API_KEY",  "Gemini:ApiKey"),
    ];

    /// <summary>
    /// Reads flat env vars and injects them as in-memory overrides
    /// when the canonical key is not already set.
    /// </summary>
    public static IConfigurationBuilder AddFlatEnvironmentVariables(this IConfigurationBuilder builder)
    {
        var tempConfig = builder.Build();
        var overrides = new Dictionary<string, string?>();

        foreach (var (flatEnvVar, configKey) in Mappings)
        {
            var value = Environment.GetEnvironmentVariable(flatEnvVar);
            if (string.IsNullOrEmpty(value))
                continue;

            if (string.IsNullOrEmpty(tempConfig[configKey]))
            {
                overrides[configKey] = value;
            }
        }

        if (overrides.Count > 0)
        {
            builder.AddInMemoryCollection(overrides);
        }

        return builder;
    }
}
