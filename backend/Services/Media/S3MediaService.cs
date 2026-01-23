using Amazon.S3;
using Amazon.S3.Model;

namespace PostPilot.Api.Services.Media;

/// <summary>
/// S3-based implementation of media service for production use.
/// </summary>
public class S3MediaService : IMediaService
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly ILogger<S3MediaService> _logger;

    private static readonly HashSet<string> _allowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/gif"
    };

    private const long _maxFileSizeBytes = 10 * 1024 * 1024; // 10MB
    private static readonly TimeSpan UploadUrlExpiration = TimeSpan.FromMinutes(15);

    public IReadOnlyCollection<string> AllowedContentTypes => _allowedContentTypes;
    public long MaxFileSizeBytes => _maxFileSizeBytes;

    public S3MediaService(
        IAmazonS3 s3Client,
        string bucketName,
        ILogger<S3MediaService> logger)
    {
        _s3Client = s3Client;
        _bucketName = bucketName;
        _logger = logger;
    }

    public Task<UploadUrlResult> GenerateUploadUrlAsync(string fileName, string contentType)
    {
        if (!IsValidImageType(contentType))
        {
            throw new ArgumentException($"Invalid content type: {contentType}. Allowed types: {string.Join(", ", _allowedContentTypes)}");
        }

        var extension = Path.GetExtension(fileName)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(extension))
        {
            extension = contentType switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                _ => ".jpg"
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

        _logger.LogInformation("Generated upload URL for key {S3Key}, expires in {Minutes} minutes",
            s3Key, UploadUrlExpiration.TotalMinutes);

        return Task.FromResult(new UploadUrlResult(uploadUrl, s3Key));
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
        return _allowedContentTypes.Contains(contentType);
    }
}
