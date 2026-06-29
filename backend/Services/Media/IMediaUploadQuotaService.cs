namespace PostPilot.Api.Services.Media;

public interface IMediaUploadQuotaService
{
    Task<MediaUploadQuotaResult> TryConsumeUploadAsync(Guid userId, CancellationToken ct = default);
}

public sealed record MediaUploadQuotaResult(
    bool Allowed,
    int Limit,
    int Used,
    int Remaining,
    DateTime PeriodEndUtc,
    string? ErrorCode = null
);

public sealed class MediaUploadQuotaExceededException : Exception
{
    public const string DefaultErrorCode = "MEDIA_UPLOAD_QUOTA_EXCEEDED";

    public MediaUploadQuotaExceededException(MediaUploadQuotaResult result)
        : base("Daily media upload limit reached. You can upload more media when your quota resets.")
    {
        Result = result;
    }

    public MediaUploadQuotaResult Result { get; }
}
