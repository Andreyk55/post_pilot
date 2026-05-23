using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Media;

/// <summary>
/// Storage provider for any S3-compatible backend (MinIO, AWS S3, Cloudflare R2,
/// DigitalOcean Spaces, Backblaze B2, Wasabi, Hetzner Object Storage).
///
/// The AWS SDK is an implementation detail. Nothing else in the app references
/// <c>Amazon.S3</c> — controllers, publishers, DB models, and the frontend stay
/// provider-neutral via <see cref="IMediaStorageProvider"/>.
///
/// Why two clients:
///   - Internal ops (HeadObject, GetObject, DeleteObject, PutObject) must hit the
///     bucket over the Docker network — endpoint = http://minio:9000.
///   - Presigned upload URLs must be signed against the host the browser will
///     hit — http://localhost:9000. S3 signature v4 binds the signature to the
///     endpoint, so we can't sign-internal and serve-public from one client.
/// </summary>
public class S3CompatibleMediaStorageProvider : IMediaStorageProvider, IDisposable
{
    private readonly AmazonS3Client _internalClient;
    private readonly AmazonS3Client _publicClient;
    private readonly string _bucket;
    private readonly Uri _publicEndpoint;
    private readonly Protocol _publicSignedUrlProtocol;
    private readonly ILogger<S3CompatibleMediaStorageProvider> _logger;
    private bool _disposed;

    public S3CompatibleMediaStorageProvider(
        MediaStorageOptions options,
        ILogger<S3CompatibleMediaStorageProvider> logger)
    {
        _logger = logger;
        _bucket = options.Bucket;

        // The two endpoints have INDEPENDENT schemes: InternalEndpoint is typically
        // http://postpilot-minio:9000 (container-to-container), while PublicUploadEndpoint
        // is https://media.<host> (browser-facing, behind TLS-terminating nginx).
        // A single UseSSL flag cannot drive both clients — derive each scheme from
        // its own endpoint URI.
        var internalUri = new Uri(options.InternalEndpoint);
        _publicEndpoint = new Uri(options.PublicUploadEndpoint);

        var internalUseHttp = internalUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        var publicUseHttp = _publicEndpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        // GetPreSignedURL ignores ServiceURL's scheme and defaults to HTTPS unless
        // Protocol is set explicitly. Match the public endpoint's scheme so the URL
        // we hand the browser starts with the right scheme on the first try; we also
        // post-process below to guarantee scheme/host/port regardless of SDK quirks.
        _publicSignedUrlProtocol = publicUseHttp ? Protocol.HTTP : Protocol.HTTPS;

        var credentials = new Amazon.Runtime.BasicAWSCredentials(options.AccessKey, options.SecretKey);

        _internalClient = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = options.InternalEndpoint,
            ForcePathStyle = true,
            UseHttp = internalUseHttp,
        });

        _publicClient = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = options.PublicUploadEndpoint,
            ForcePathStyle = true,
            UseHttp = publicUseHttp,
        });

        _logger.LogInformation(
            "S3CompatibleMediaStorageProvider initialized. Provider={Provider} Bucket={Bucket} Internal={Internal} Public={Public} UseSSL={UseSSL}",
            options.Provider, _bucket, options.InternalEndpoint, options.PublicUploadEndpoint, options.UseSSL);
    }

    /// <summary>
    /// Forces the scheme, host, and port of <paramref name="signedUrl"/> to match
    /// <see cref="_publicEndpoint"/>. S3 SigV4 signs the Host header (and the path/query),
    /// but NOT the URL scheme — so swapping http↔https here does not invalidate the
    /// signature. We do this because:
    ///   1. The AWS SDK has historically defaulted the scheme inconsistently across
    ///      versions when ServiceURL+Protocol+UseHttp interact.
    ///   2. The browser-facing URL must always start with PublicUploadEndpoint exactly
    ///      (scheme/host/port), regardless of how the SDK chose to render it.
    /// </summary>
    private string RewriteToPublicEndpoint(string signedUrl)
    {
        var signed = new UriBuilder(signedUrl)
        {
            Scheme = _publicEndpoint.Scheme,
            Host = _publicEndpoint.Host,
            Port = _publicEndpoint.IsDefaultPort ? -1 : _publicEndpoint.Port,
        };
        return signed.Uri.ToString();
    }

    public Task<string> CreateUploadUrlAsync(string storageKey, string contentType, TimeSpan expires, CancellationToken cancellationToken = default)
    {
        var signed = _publicClient.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = storageKey,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(expires),
            ContentType = contentType,
            Protocol = _publicSignedUrlProtocol,
        });
        var url = RewriteToPublicEndpoint(signed);
        _logger.LogInformation(
            "Generated presigned PUT URL for key {Key} (expires in {Expires}, scheme={Scheme})",
            storageKey, expires, _publicEndpoint.Scheme);
        return Task.FromResult(url);
    }

    public Task<string> CreateDownloadUrlAsync(string storageKey, TimeSpan expires, CancellationToken cancellationToken = default)
    {
        var signed = _publicClient.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = storageKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expires),
            Protocol = _publicSignedUrlProtocol,
        });
        return Task.FromResult(RewriteToPublicEndpoint(signed));
    }

    public async Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _internalClient.GetObjectAsync(_bucket, storageKey, cancellationToken);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        await _internalClient.DeleteObjectAsync(_bucket, storageKey, cancellationToken);
        _logger.LogInformation("Deleted object {Key} from bucket {Bucket}", storageKey, _bucket);
    }

    public async Task<string?> GetLocalFilePathAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        // Object storage has no real local file. Materialize one in the system temp dir
        // so callers that require a path (ffprobe, ImageSharp) can work. The filename
        // prefix lets MediaService.TryCleanupTempLocalPath recognize and delete it
        // after use without ever touching real LocalDisk storage paths.
        await using var source = await OpenReadAsync(storageKey, cancellationToken);
        if (source == null) return null;

        var extension = Path.GetExtension(storageKey);
        var tempPath = Path.Combine(Path.GetTempPath(), $"postpilot-media-{Guid.NewGuid()}{extension}");
        await using (var destination = File.Create(tempPath))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }
        return tempPath;
    }

    public Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
    {
        // The legacy "PUT through the API" upload path is local-disk only. When
        // s3-compatible storage is active, clients upload directly to the bucket
        // via a presigned URL — there's no reason for the API to receive the bytes.
        throw new InvalidOperationException(
            "Direct save is not supported by S3CompatibleMediaStorageProvider. " +
            "Use the presigned upload URL from /api/media/uploads/init instead.");
    }

    [Obsolete("Use ObjectExistsAsync.")]
    public bool Exists(string storageKey)
    {
        return ObjectExistsAsync(storageKey).GetAwaiter().GetResult();
    }

    public async Task<bool> ObjectExistsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        try
        {
            await _internalClient.GetObjectMetadataAsync(_bucket, storageKey, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<StoredObjectInfo?> GetObjectInfoAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = await _internalClient.GetObjectMetadataAsync(_bucket, storageKey, cancellationToken);
            return new StoredObjectInfo(
                SizeBytes: metadata.ContentLength,
                ContentType: metadata.Headers.ContentType,
                ETag: metadata.ETag,
                LastModified: metadata.LastModified);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _internalClient.Dispose();
        _publicClient.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
