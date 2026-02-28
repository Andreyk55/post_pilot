namespace PostPilot.Api.Settings;

/// <summary>
/// Maps legacy flat environment variable names to canonical __ (section-separated) config keys.
///
/// This allows users who currently set e.g. META_APP_ID to keep working while
/// the codebase transitions to standard .NET configuration keys like Meta__AppId.
///
/// DEPRECATED: Legacy env var names will be removed in a future release.
/// Prefer the canonical names (App__RunMode, Meta__AppId, Gemini__ApiKey, etc.).
/// </summary>
public static class LegacyEnvVarMapper
{
    /// <summary>
    /// Legacy flat env var → canonical config key.
    /// Canonical keys use ":" as section separator (internally mapped from "__" by .NET).
    /// </summary>
    private static readonly (string LegacyEnv, string ConfigKey)[] Mappings =
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
    /// Reads legacy flat env vars and injects them into the <see cref="IConfigurationBuilder"/>
    /// as in-memory overrides — but only when the canonical key is NOT already set.
    /// This ensures that if a user sets both META_APP_ID and Meta__AppId, the canonical one wins.
    /// </summary>
    public static IConfigurationBuilder AddLegacyEnvironmentVariables(this IConfigurationBuilder builder)
    {
        // Build a temporary config to check what's already set
        var tempConfig = builder.Build();

        var overrides = new Dictionary<string, string?>();

        foreach (var (legacyEnv, configKey) in Mappings)
        {
            var legacyValue = Environment.GetEnvironmentVariable(legacyEnv);
            if (string.IsNullOrEmpty(legacyValue))
                continue;

            // Only inject if the canonical key isn't already set (canonical wins)
            var existingValue = tempConfig[configKey];
            if (string.IsNullOrEmpty(existingValue))
            {
                overrides[configKey] = legacyValue;
            }
        }

        if (overrides.Count > 0)
        {
            builder.AddInMemoryCollection(overrides);
        }

        return builder;
    }
}
