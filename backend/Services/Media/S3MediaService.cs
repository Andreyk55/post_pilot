using Amazon.S3;
using Amazon.S3.Model;
using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Media;

/// <summary>
/// S3-based implementation of media service for production use.
/// </summary>
public class S3MediaService : IMediaService
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly ILogger<S3MediaService> _logger;
    private readonly long _maxVideoFileSizeBytes;

    private static readonly HashSet<string> _allowedImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/gif"
    };

    private static readonly HashSet<string> _allowedVideoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "video/mp4"
    };

    private const long DefaultMaxImageFileSizeBytes = 10 * 1024 * 1024; // 10MB
    private const long DefaultMaxVideoFileSizeBytes = 200 * 1024 * 1024; // 200MB
    private static readonly TimeSpan UploadUrlExpiration = TimeSpan.FromMinutes(60); // Longer for video uploads

    public IReadOnlyCollection<string> AllowedImageTypes => _allowedImageTypes;
    public IReadOnlyCollection<string> AllowedVideoTypes => _allowedVideoTypes;
    public IReadOnlyCollection<string> AllowedContentTypes => _allowedImageTypes.Concat(_allowedVideoTypes).ToArray();
    public long MaxImageFileSizeBytes => DefaultMaxImageFileSizeBytes;
    public long MaxVideoFileSizeBytes => _maxVideoFileSizeBytes;

    public S3MediaService(
        IAmazonS3 s3Client,
        string bucketName,
        ILogger<S3MediaService> logger,
        long? maxVideoFileSizeBytes = null)
    {
        _s3Client = s3Client;
        _bucketName = bucketName;
        _logger = logger;
        _maxVideoFileSizeBytes = maxVideoFileSizeBytes ?? DefaultMaxVideoFileSizeBytes;
    }

    public Task<UploadUrlResult> GenerateUploadUrlAsync(string fileName, string contentType)
    {
        if (!IsValidMediaType(contentType))
        {
            throw new ArgumentException($"Invalid content type: {contentType}. Allowed types: {string.Join(", ", AllowedContentTypes)}");
        }

        var mediaType = GetMediaType(contentType);
        var extension = Path.GetExtension(fileName)?.ToLowerInvariant();

        if (string.IsNullOrEmpty(extension))
        {
            extension = contentType switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                "video/mp4" => ".mp4",
                _ => mediaType == MediaType.Video ? ".mp4" : ".jpg"
            };
        }

        var s3Key = $"media/{Guid.NewGuid()}{extension}";

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = s3Key,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(UploadUrlExpiration),
            ContentType = contentType
        };

        var uploadUrl = _s3Client.GetPreSignedURL(request);

        _logger.LogInformation("Generated upload URL for {MediaType} key {S3Key}, expires in {Minutes} minutes",
            mediaType, s3Key, UploadUrlExpiration.TotalMinutes);

        return Task.FromResult(new UploadUrlResult(uploadUrl, s3Key, mediaType));
    }

    public string GenerateDownloadUrl(string s3Key, TimeSpan expiration)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = s3Key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiration)
        };

        var downloadUrl = _s3Client.GetPreSignedURL(request);

        _logger.LogDebug("Generated download URL for key {S3Key}, expires in {Minutes} minutes",
            s3Key, expiration.TotalMinutes);

        return downloadUrl;
    }

    public bool IsS3Key(string? mediaUrl)
    {
        return !string.IsNullOrEmpty(mediaUrl) &&
               mediaUrl.StartsWith("media/", StringComparison.OrdinalIgnoreCase) &&
               !mediaUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsValidImageType(string contentType)
    {
        return _allowedImageTypes.Contains(contentType);
    }

    public bool IsValidVideoType(string contentType)
    {
        return _allowedVideoTypes.Contains(contentType);
    }

    public bool IsValidMediaType(string contentType)
    {
        return IsValidImageType(contentType) || IsValidVideoType(contentType);
    }

    public MediaType GetMediaType(string contentType)
    {
        if (IsValidImageType(contentType))
            return MediaType.Image;
        if (IsValidVideoType(contentType))
            return MediaType.Video;
        return MediaType.None;
    }

    public long GetMaxFileSizeBytes(string contentType)
    {
        if (IsValidVideoType(contentType))
            return MaxVideoFileSizeBytes;
        return MaxImageFileSizeBytes;
    }
}
