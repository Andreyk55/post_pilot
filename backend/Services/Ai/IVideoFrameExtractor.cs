namespace PostPilot.Api.Services.Ai;

/// <summary>
/// An extracted video frame.
/// </summary>
public record ExtractedFrame(
    /// <summary>
    /// Timestamp in seconds where the frame was extracted.
    /// </summary>
    double TimestampSeconds,

    /// <summary>
    /// The frame image as bytes (JPEG format).
    /// </summary>
    byte[] ImageBytes,

    /// <summary>
    /// The MIME type of the image (always "image/jpeg").
    /// </summary>
    string MimeType = "image/jpeg"
);

/// <summary>
/// Service for extracting frames from video files using FFmpeg.
/// </summary>
public interface IVideoFrameExtractor
{
    /// <summary>
    /// Extracts a single frame from a video at the specified timestamp.
    /// </summary>
    /// <param name="videoPath">Path to the video file</param>
    /// <param name="timestampSeconds">Timestamp in seconds to extract the frame</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The extracted frame</returns>
    Task<ExtractedFrame> ExtractFrameAsync(string videoPath, double timestampSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts multiple frames from a video at evenly distributed timestamps.
    /// Default positions: 0%, 10%, 25%, 50%, 75%, 90% of video duration.
    /// </summary>
    /// <param name="videoPath">Path to the video file</param>
    /// <param name="frameCount">Number of frames to extract (default: 6)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of extracted frames with timestamps</returns>
    Task<List<ExtractedFrame>> ExtractThumbnailCandidatesAsync(string videoPath, int frameCount = 6, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the duration of a video in seconds.
    /// </summary>
    /// <param name="videoPath">Path to the video file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Video duration in seconds</returns>
    Task<double> GetVideoDurationAsync(string videoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if FFmpeg is available on the system.
    /// </summary>
    /// <returns>True if FFmpeg is available</returns>
    bool IsAvailable();
}
