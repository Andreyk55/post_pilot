namespace PostPilot.Api.Services.Validation;

/// <summary>
/// Interface for extracting metadata from video files.
/// Implementations may use ffprobe, MediaInfo, or other tools.
/// </summary>
public interface IVideoMetadataExtractor
{
    /// <summary>
    /// Extracts metadata from a video file.
    /// </summary>
    /// <param name="filePath">Path to the video file.</param>
    /// <returns>Extracted metadata or null if extraction fails.</returns>
    Task<VideoMetadata?> ExtractAsync(string filePath);

    /// <summary>
    /// Checks if the video metadata extraction tool is available.
    /// </summary>
    /// <returns>True if the tool is available and working.</returns>
    Task<bool> IsAvailableAsync();
}

/// <summary>
/// Metadata extracted from a video file.
/// </summary>
public record VideoMetadata(
    int Width,
    int Height,
    double DurationSeconds,
    string? Container, // mp4, mov, avi, etc.
    string? VideoCodec, // h264, hevc, vp9, etc.
    string? AudioCodec, // aac, mp3, etc.
    double? Fps,
    long? Bitrate,
    string MimeType
);
