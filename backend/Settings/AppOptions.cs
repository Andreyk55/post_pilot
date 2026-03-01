using PostPilot.Api.Enums;

namespace PostPilot.Api.Settings;

/// <summary>
/// Application-level configuration. Bound from "App" config section.
/// RunMode: required env var (App__RunMode).
/// PublicUrl: optional, defaults from appsettings.
/// </summary>
public class AppOptions
{
    public const string SectionName = "App";

    /// <summary>
    /// Application run mode: "local" (filesystem storage) or "server" (storage provider).
    /// Required — set via App__RunMode env var.
    /// </summary>
    public string RunMode { get; set; } = string.Empty;

    public AppRunMode RunModeEnum =>
        RunMode.Equals("server", StringComparison.OrdinalIgnoreCase)
            ? AppRunMode.Server
            : AppRunMode.Local;

    /// <summary>
    /// Public-facing URL override (e.g., ngrok tunnel URL).
    /// Optional — defaults from appsettings, overridable via App__PublicUrl env var.
    /// </summary>
    public string? PublicUrl { get; set; }
}
