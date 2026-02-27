namespace PostPilot.Api.Enums;

/// <summary>
/// Determines how the application runs and which storage backend is used.
/// </summary>
public enum AppRunMode
{
    /// <summary>
    /// Local development on developer's machine.
    /// Backend receives uploads and stores files to disk.
    /// </summary>
    Local,

    /// <summary>
    /// Server mode (dev server or production).
    /// Frontend uploads directly to object storage via pre-signed PUT URLs.
    /// Media is fetched by Meta via pre-signed GET URLs.
    /// </summary>
    Server
}
