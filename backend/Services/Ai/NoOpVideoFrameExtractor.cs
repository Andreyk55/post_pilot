namespace PostPilot.Api.Services.Ai;

/// <summary>
/// No-op implementation of video frame extractor for serverless environments.
/// Always reports as unavailable since frame extraction should happen client-side.
/// </summary>
public class NoOpVideoFrameExtractor : IVideoFrameExtractor
{
    private const string NotAvailableMessage =
        "Server-side video frame extraction is not available. " +
        "Use client-side extraction and submit frames via POST /api/ai/media/thumbnails instead.";

    public bool IsAvailable() => false;

    public Task<ExtractedFrame> ExtractFrameAsync(
        string videoPath,
        double timestampSeconds,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(NotAvailableMessage);
    }

    public Task<List<ExtractedFrame>> ExtractThumbnailCandidatesAsync(
        string videoPath,
        int frameCount = 6,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(NotAvailableMessage);
    }

    public Task<double> GetVideoDurationAsync(
        string videoPath,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(NotAvailableMessage);
    }
}
