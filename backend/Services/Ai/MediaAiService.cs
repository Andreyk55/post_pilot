using PostPilot.Api.DTOs;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Service for orchestrating AI operations on media assets.
/// Handles asset resolution, frame extraction, and AI calls.
/// </summary>
public class MediaAiService : IMediaAiService
{
    private readonly IGeminiClient _geminiClient;
    private readonly IAssetResolver _assetResolver;
    private readonly IVideoFrameExtractor _videoFrameExtractor;
    private readonly IMediaService _mediaService;
    private readonly ILogger<MediaAiService> _logger;
    private readonly string _localServerBaseUrl;

    // Temporary storage for extracted frames
    private readonly string _framesDirectory;

    public MediaAiService(
        IGeminiClient geminiClient,
        IAssetResolver assetResolver,
        IVideoFrameExtractor videoFrameExtractor,
        IMediaService mediaService,
        ILogger<MediaAiService> logger,
        MediaOptions mediaOptions)
    {
        _geminiClient = geminiClient;
        _assetResolver = assetResolver;
        _videoFrameExtractor = videoFrameExtractor;
        _mediaService = mediaService;
        _logger = logger;
        _localServerBaseUrl = mediaOptions.LocalServerBaseUrl;

        _framesDirectory = Path.Combine(Directory.GetCurrentDirectory(), "uploads", "frames");
        Directory.CreateDirectory(_framesDirectory);
    }

    public async Task<AiMediaCaptionIdeasResponse> GenerateImageCaptionIdeasAsync(
        string assetUrl,
        AiPlatform platform,
        string? existingText,
        string language,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating image caption ideas for {AssetUrl}, Platform: {Platform}", assetUrl, platform);

        var asset = await _assetResolver.ResolveAsync(assetUrl, cancellationToken);

        if (!asset.MimeType.StartsWith("image/"))
        {
            throw new InvalidOperationException($"Asset is not an image: {asset.MimeType}");
        }

        return await _geminiClient.GenerateImageCaptionIdeasAsync(
            asset.Bytes,
            asset.MimeType,
            platform,
            existingText,
            language,
            cancellationToken);
    }

    public async Task<AiImageQualityCheckResponse> CheckImageQualityAsync(
        string assetUrl,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Checking image quality for {AssetUrl}", assetUrl);

        var asset = await _assetResolver.ResolveAsync(assetUrl, cancellationToken);

        if (!asset.MimeType.StartsWith("image/"))
        {
            throw new InvalidOperationException($"Asset is not an image: {asset.MimeType}");
        }

        return await _geminiClient.CheckImageQualityAsync(
            asset.Bytes,
            asset.MimeType,
            cancellationToken);
    }

    public async Task<AiAltTextResponse> GenerateAltTextAsync(
        string assetUrl,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating alt text for {AssetUrl}", assetUrl);

        var asset = await _assetResolver.ResolveAsync(assetUrl, cancellationToken);

        if (!asset.MimeType.StartsWith("image/"))
        {
            throw new InvalidOperationException($"Asset is not an image: {asset.MimeType}");
        }

        return await _geminiClient.GenerateAltTextAsync(
            asset.Bytes,
            asset.MimeType,
            cancellationToken);
    }

    public async Task<AiMediaCaptionIdeasResponse> GenerateVideoCaptionIdeasAsync(
        string assetUrl,
        AiPlatform platform,
        string? existingText,
        string language,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating video caption ideas for {AssetUrl}, Platform: {Platform}", assetUrl, platform);

        if (!_videoFrameExtractor.IsAvailable())
        {
            throw new InvalidOperationException("FFmpeg is not available. Video caption ideas require FFmpeg to extract frames.");
        }

        // Get the video file path
        var videoPath = await GetLocalVideoPathAsync(assetUrl, cancellationToken);

        try
        {
            // Extract first frame (at 0.5s to avoid black frames)
            var frame = await _videoFrameExtractor.ExtractFrameAsync(videoPath, 0.5, cancellationToken);

            // Use the first frame for caption generation
            var response = await _geminiClient.GenerateImageCaptionIdeasAsync(
                frame.ImageBytes,
                frame.MimeType,
                platform,
                existingText,
                language,
                cancellationToken);

            // Return with correct action type
            return new AiMediaCaptionIdeasResponse(
                AiMediaAction.VideoCaptionIdeas,
                response.Variants);
        }
        finally
        {
            // Clean up temporary video file if it was downloaded
            CleanupTempFile(videoPath, assetUrl);
        }
    }

    public async Task<AiThumbnailSuggestResponse> SuggestThumbnailsAsync(
        string assetUrl,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Suggesting thumbnails for {AssetUrl}", assetUrl);

        if (!_videoFrameExtractor.IsAvailable())
        {
            throw new InvalidOperationException("FFmpeg is not available. Thumbnail suggestions require FFmpeg to extract frames.");
        }

        // Get the video file path
        var videoPath = await GetLocalVideoPathAsync(assetUrl, cancellationToken);

        try
        {
            // Extract 6 thumbnail candidates
            var frames = await _videoFrameExtractor.ExtractThumbnailCandidatesAsync(videoPath, 6, cancellationToken);

            // Save frames and generate URLs
            var frameResults = new List<AiVideoFrame>();

            foreach (var frame in frames)
            {
                var frameUrl = await SaveFrameAndGetUrlAsync(frame, cancellationToken);
                frameResults.Add(new AiVideoFrame(frame.TimestampSeconds, frameUrl));
            }

            return new AiThumbnailSuggestResponse(AiMediaAction.ThumbnailSuggest, frameResults);
        }
        finally
        {
            // Clean up temporary video file if it was downloaded
            CleanupTempFile(videoPath, assetUrl);
        }
    }

    /// <summary>
    /// Gets a local file path for the video. Downloads if necessary.
    /// </summary>
    private async Task<string> GetLocalVideoPathAsync(string assetUrl, CancellationToken cancellationToken)
    {
        // Try to get local file path directly from storage
        var localFilePath = await _mediaService.GetLocalFilePathAsync(assetUrl);
        if (localFilePath != null)
        {
            return localFilePath;
        }

        // Otherwise, download to temp file
        var asset = await _assetResolver.ResolveAsync(assetUrl, cancellationToken);

        if (!asset.MimeType.StartsWith("video/"))
        {
            throw new InvalidOperationException($"Asset is not a video: {asset.MimeType}");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"postpilot-video-{Guid.NewGuid()}.mp4");
        await File.WriteAllBytesAsync(tempPath, asset.Bytes, cancellationToken);

        _logger.LogDebug("Downloaded video to temp path: {TempPath}", tempPath);
        return tempPath;
    }

    /// <summary>
    /// Saves an extracted frame and returns a URL to access it.
    /// </summary>
    private async Task<string> SaveFrameAndGetUrlAsync(ExtractedFrame frame, CancellationToken cancellationToken)
    {
        var frameId = $"{Guid.NewGuid()}.jpg";
        var framePath = Path.Combine(_framesDirectory, frameId);

        await File.WriteAllBytesAsync(framePath, frame.ImageBytes, cancellationToken);

        // Generate URL using media service
        var storageKey = $"media/frames/{frameId}";

        // Frames are always saved locally (both modes) for AI processing
        var baseUrl = Environment.GetEnvironmentVariable("PUBLIC_URL") ?? _localServerBaseUrl;
        return $"{baseUrl}/api/media/frames/{frameId}";
    }

    /// <summary>
    /// Cleans up temporary files if they were created.
    /// </summary>
    private void CleanupTempFile(string filePath, string originalAssetUrl)
    {
        // Only clean up if it's a temp file (not the original local file)
        if (filePath.Contains("postpilot-video-") && File.Exists(filePath))
        {
            try
            {
                File.Delete(filePath);
                _logger.LogDebug("Cleaned up temp video file: {FilePath}", filePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temp file: {FilePath}", filePath);
            }
        }
    }

    /// <summary>
    /// Processes frames that were extracted client-side.
    /// Saves the frames and returns URLs for thumbnail selection.
    /// This approach works in Lambda without FFmpeg dependency.
    /// </summary>
    public async Task<AiThumbnailSuggestResponse> ProcessClientExtractedFramesAsync(
        List<ClientExtractedFrame> frames,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing {Count} client-extracted frames", frames.Count);

        var frameResults = new List<AiVideoFrame>();

        foreach (var frame in frames)
        {
            var frameUrl = await SaveBase64FrameAndGetUrlAsync(frame, cancellationToken);
            frameResults.Add(new AiVideoFrame(frame.TimestampSeconds, frameUrl));
        }

        return new AiThumbnailSuggestResponse(AiMediaAction.ThumbnailSuggest, frameResults);
    }

    /// <summary>
    /// Saves a base64-encoded frame from the client and returns a URL.
    /// </summary>
    private async Task<string> SaveBase64FrameAndGetUrlAsync(
        ClientExtractedFrame frame,
        CancellationToken cancellationToken)
    {
        // Parse the data URL to get the bytes
        // Format: data:image/jpeg;base64,/9j/4AAQSkZJRg...
        var dataUrlParts = frame.ImageData.Split(',');
        if (dataUrlParts.Length != 2)
        {
            throw new ArgumentException("Invalid data URL format");
        }

        var base64Data = dataUrlParts[1];
        var imageBytes = Convert.FromBase64String(base64Data);

        // Determine file extension from MIME type
        var mimeMatch = System.Text.RegularExpressions.Regex.Match(dataUrlParts[0], @"data:image/(\w+);");
        var extension = mimeMatch.Success ? mimeMatch.Groups[1].Value : "jpg";
        if (extension == "jpeg") extension = "jpg";

        var frameId = $"{Guid.NewGuid()}.{extension}";
        var framePath = Path.Combine(_framesDirectory, frameId);

        await File.WriteAllBytesAsync(framePath, imageBytes, cancellationToken);

        _logger.LogDebug("Saved client-extracted frame: {FramePath}", framePath);

        // Generate URL
        var publicUrl = Environment.GetEnvironmentVariable("PUBLIC_URL") ?? _localServerBaseUrl;
        return $"{publicUrl}/api/media/frames/{frameId}";
    }

    /// <summary>
    /// Generates caption ideas for a video using a pre-extracted frame.
    /// Frame is extracted client-side and sent as base64 data URL.
    /// This approach works in Lambda without FFmpeg dependency.
    /// </summary>
    public async Task<AiMediaCaptionIdeasResponse> GenerateVideoCaptionIdeasFromFrameAsync(
        string frameData,
        AiPlatform platform,
        string? existingText,
        string language,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating video caption ideas from client-extracted frame, Platform: {Platform}", platform);

        // Parse the data URL to get the bytes and mime type
        var dataUrlParts = frameData.Split(',');
        if (dataUrlParts.Length != 2)
        {
            throw new ArgumentException("Invalid data URL format");
        }

        var base64Data = dataUrlParts[1];
        var imageBytes = Convert.FromBase64String(base64Data);

        // Determine MIME type from data URL
        var mimeMatch = System.Text.RegularExpressions.Regex.Match(dataUrlParts[0], @"data:(image/\w+);");
        var mimeType = mimeMatch.Success ? mimeMatch.Groups[1].Value : "image/jpeg";

        // Use the frame for caption generation
        var response = await _geminiClient.GenerateImageCaptionIdeasAsync(
            imageBytes,
            mimeType,
            platform,
            existingText,
            language,
            cancellationToken);

        // Return with correct action type
        return new AiMediaCaptionIdeasResponse(
            AiMediaAction.VideoCaptionIdeas,
            response.Variants);
    }
}
