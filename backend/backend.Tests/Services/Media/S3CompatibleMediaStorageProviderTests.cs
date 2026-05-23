using Microsoft.Extensions.Logging.Abstractions;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Services.Media;

/// <summary>
/// Regression tests for the presigned-URL scheme bug: prod had
/// InternalEndpoint=http://postpilot-minio:9000 and PublicUploadEndpoint=https://media.<host>,
/// but the provider used a single UseSSL flag for both clients and returned http:// URLs
/// to the browser. The provider must now rewrite the signed URL's scheme/host/port to
/// match PublicUploadEndpoint regardless of how the AWS SDK chose to render it.
///
/// Note: GetPreSignedURL signs locally with no network call, so these tests are hermetic.
/// </summary>
public class S3CompatibleMediaStorageProviderTests
{
    private static MediaStorageOptions ProdLikeOptions() => new()
    {
        Provider = "s3-compatible",
        Bucket = "postpilot-media",
        InternalEndpoint = "http://postpilot-minio:9000",
        PublicUploadEndpoint = "https://media.post-pilot.cloud-ip.cc",
        AccessKey = "test-access-key",
        SecretKey = "test-secret-key",
        UseSSL = true,
        PresignedUploadExpirationMinutes = 15,
    };

    [Fact]
    public async Task CreateUploadUrlAsync_UsesHttpsSchemeFromPublicUploadEndpoint()
    {
        // Production config: internal is HTTP (container network), public is HTTPS (nginx).
        // The returned URL must start with https://media.post-pilot.cloud-ip.cc — never http://.
        using var provider = new S3CompatibleMediaStorageProvider(
            ProdLikeOptions(),
            NullLogger<S3CompatibleMediaStorageProvider>.Instance);

        var url = await provider.CreateUploadUrlAsync("media/test.png", "image/png", TimeSpan.FromMinutes(15));

        Assert.StartsWith("https://media.post-pilot.cloud-ip.cc/", url);
        Assert.DoesNotContain("http://media.", url);
        Assert.DoesNotContain("postpilot-minio", url); // must not leak the internal hostname
    }

    [Fact]
    public async Task CreateDownloadUrlAsync_UsesHttpsSchemeFromPublicUploadEndpoint()
    {
        using var provider = new S3CompatibleMediaStorageProvider(
            ProdLikeOptions(),
            NullLogger<S3CompatibleMediaStorageProvider>.Instance);

        var url = await provider.CreateDownloadUrlAsync("media/test.png", TimeSpan.FromMinutes(15));

        Assert.StartsWith("https://media.post-pilot.cloud-ip.cc/", url);
        Assert.DoesNotContain("postpilot-minio", url);
    }

    [Fact]
    public async Task CreateUploadUrlAsync_LocalDevHttp_KeepsHttpFromPublicEndpoint()
    {
        // Local dev: both endpoints are HTTP. Public is http://localhost:9000.
        var options = new MediaStorageOptions
        {
            Provider = "s3-compatible",
            Bucket = "postpilot-media",
            InternalEndpoint = "http://minio:9000",
            PublicUploadEndpoint = "http://localhost:9000",
            AccessKey = "test-access-key",
            SecretKey = "test-secret-key",
            UseSSL = false,
        };

        using var provider = new S3CompatibleMediaStorageProvider(
            options,
            NullLogger<S3CompatibleMediaStorageProvider>.Instance);

        var url = await provider.CreateUploadUrlAsync("media/test.png", "image/png", TimeSpan.FromMinutes(15));

        Assert.StartsWith("http://localhost:9000/", url);
        Assert.DoesNotContain("minio:9000", url);
    }

    [Fact]
    public async Task CreateUploadUrlAsync_PreservesBucketAndKey()
    {
        using var provider = new S3CompatibleMediaStorageProvider(
            ProdLikeOptions(),
            NullLogger<S3CompatibleMediaStorageProvider>.Instance);

        var url = await provider.CreateUploadUrlAsync("media/abc-123.png", "image/png", TimeSpan.FromMinutes(15));

        // Path-style: https://host/bucket/key
        Assert.Contains("/postpilot-media/media/abc-123.png", url);
        // Presigned URL must include a signature in the query string (SigV4 or SigV2).
        Assert.Contains("Signature=", url, StringComparison.OrdinalIgnoreCase);
    }
}
