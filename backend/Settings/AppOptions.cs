using PostPilot.Api.Enums;

namespace PostPilot.Api.Settings;

/// <summary>
/// Application-level configuration.
/// Bound from "App" config section.
/// 
/// Canonical env vars (preferred):
///   App__RunMode, App__PublicUrl
/// Legacy env vars (deprecated, compat only):
///   APP_RUN_MODE, PUBLIC_URL
/// </summary>
public class AppOptions
{
    public const string SectionName = "App";

    /// <summary>
    /// Application run mode: "local" (filesystem storage) or "server" (storage provider).
    /// </summary>
    public string RunMode { get; set; } = "local";

    /// <summary>
    /// Parsed <see cref="RunMode"/> as enum. Defaults to <see cref="AppRunMode.Local"/>.
    /// </summary>
    public AppRunMode RunModeEnum =>
        RunMode.Equals("server", StringComparison.OrdinalIgnoreCase)
            ? AppRunMode.Server
            : AppRunMode.Local;

    /// <summary>
    /// Public-facing URL override (e.g., ngrok tunnel URL).
    /// When set, used instead of Media:LocalServerBaseUrl for generating
    /// download/upload URLs that external services (Meta, AI) must reach.
    /// </summary>
    public string? PublicUrl { get; set; }
}
