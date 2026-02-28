namespace PostPilot.Api.Settings;

/// <summary>
/// Maps flat environment variable names to canonical __ (section-separated) config keys.
///
/// This allows users who set e.g. META_APP_ID or GEMINI_API_KEY to have those
/// values flow into the standard .NET configuration hierarchy (Meta:AppId, Gemini:ApiKey, etc.).
///
/// Both flat names and canonical __ names are supported. When both are set,
/// the canonical name wins.
/// </summary>
public static class EnvVarMapper
{
    /// <summary>
    /// Flat env var → canonical config key.
    /// Canonical keys use ":" as section separator (internally mapped from "__" by .NET).
    /// </summary>
    private static readonly (string FlatEnvVar, string ConfigKey)[] Mappings =
    [
        ("APP_RUN_MODE",        "App:RunMode"),
        ("PUBLIC_URL",          "App:PublicUrl"),
        ("META_APP_ID",         "Meta:AppId"),
        ("META_APP_SECRET",     "Meta:AppSecret"),
        ("GEMINI_API_KEY",      "Gemini:ApiKey"),
        ("GEMINI_MODEL",        "Gemini:Model"),
        ("GEMINI_VISION_MODEL", "Gemini:VisionModel"),
    ];

    /// <summary>
    /// Reads flat env vars and injects them into the <see cref="IConfigurationBuilder"/>
    /// as in-memory overrides — but only when the canonical key is NOT already set.
    /// This ensures that if a user sets both META_APP_ID and Meta__AppId, the canonical one wins.
    /// </summary>
    public static IConfigurationBuilder AddFlatEnvironmentVariables(this IConfigurationBuilder builder)
    {
        // Build a temporary config to check what's already set
        var tempConfig = builder.Build();

        var overrides = new Dictionary<string, string?>();

        foreach (var (flatEnvVar, configKey) in Mappings)
        {
            var value = Environment.GetEnvironmentVariable(flatEnvVar);
            if (string.IsNullOrEmpty(value))
                continue;

            // Only inject if the canonical key isn't already set (canonical wins)
            var existingValue = tempConfig[configKey];
            if (string.IsNullOrEmpty(existingValue))
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
