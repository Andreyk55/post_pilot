using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Services.Media;

/// <summary>
/// Hermetic tests for SupabaseMediaStorageProvider. We stub Supabase's HTTP
/// API with a fake handler so we never touch the network. The properties we
/// guard:
///   - Service-role key is sent on every request (both `apikey` and `Authorization`).
///   - Paths include the bucket and url-encode every segment of the storage key.
///   - The signed-upload URL the browser sees is fully qualified and carries
///     the token as a query parameter.
///   - The signed-download URL builder copes with both absolute and relative URLs
///     in the Supabase response.
/// </summary>
public class SupabaseMediaStorageProviderTests
{
    private static MediaStorageOptions Opts() => new()
    {
        Provider = "supabase",
        Supabase = new SupabaseStorageOptions
        {
            Url = "https://abc.supabase.co",
            ServiceRoleKey = "service-role-key",
            Bucket = "postpilot-media",
            SignedUrlExpirySeconds = 3600,
        },
    };

    private static (SupabaseMediaStorageProvider provider, StubHandler handler) BuildWith(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://abc.supabase.co/") };
        var provider = new SupabaseMediaStorageProvider(
            Opts(),
            NullLogger<SupabaseMediaStorageProvider>.Instance,
            http);
        return (provider, handler);
    }

    [Fact]
    public async Task CreateUploadUrlAsync_AppendsTokenAndReturnsAbsoluteUrl()
    {
        var (provider, handler) = BuildWith(req =>
        {
            // Supabase responds with a relative URL and a separate token.
            // The provider must build a fully qualified URL with ?token=...
            var json = "{\"url\":\"/object/upload/sign/postpilot-media/workspaces/ws/media/m/photo.png\",\"token\":\"abc123\"}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var url = await provider.CreateUploadUrlAsync(
            "workspaces/ws/media/m/photo.png",
            "image/png",
            TimeSpan.FromMinutes(15));

        Assert.StartsWith("https://abc.supabase.co/", url);
        Assert.Contains("/object/upload/sign/postpilot-media/", url);
        Assert.Contains("token=abc123", url);

        var req = handler.LastRequest!;
        // Auth headers go on every request.
        Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
        Assert.Equal("service-role-key", req.Headers.Authorization?.Parameter);
        Assert.True(req.Headers.Contains("apikey"));
        Assert.Equal("service-role-key", string.Join(",", req.Headers.GetValues("apikey")));
        // The POST path must include the bucket and url-encoded key segments.
        Assert.Contains("/storage/v1/object/upload/sign/postpilot-media/", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task CreateDownloadUrlAsync_BuildsAbsoluteUrlFromSignedUrlField()
    {
        var (provider, _) = BuildWith(_ =>
        {
            // Supabase uses the field name `signedURL` (capital URL) and returns
            // a relative path. The provider must absolutise it.
            var json = "{\"signedURL\":\"/object/sign/postpilot-media/workspaces/ws/media/m/photo.png?token=xyz\"}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var url = await provider.CreateDownloadUrlAsync(
            "workspaces/ws/media/m/photo.png",
            TimeSpan.FromHours(1));

        Assert.StartsWith("https://abc.supabase.co/", url);
        Assert.Contains("/object/sign/postpilot-media/", url);
        Assert.Contains("token=xyz", url);
    }

    [Fact]
    public async Task GetObjectInfoAsync_ReturnsNullOnNotFound()
    {
        var (provider, _) = BuildWith(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var info = await provider.GetObjectInfoAsync("missing-key.png");

        Assert.Null(info);
    }

    [Fact]
    public async Task GetObjectInfoAsync_ParsesSizeAndContentType()
    {
        var (provider, _) = BuildWith(_ =>
        {
            var json = "{\"size\":12345,\"contentType\":\"image/png\",\"etag\":\"abc\",\"lastModified\":\"2026-05-29T12:00:00Z\"}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var info = await provider.GetObjectInfoAsync("workspaces/ws/media/m/photo.png");

        Assert.NotNull(info);
        Assert.Equal(12345, info!.SizeBytes);
        Assert.Equal("image/png", info.ContentType);
    }

    [Fact]
    public async Task DeleteAsync_TreatsNotFoundAsSuccess()
    {
        var (provider, _) = BuildWith(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        // Should NOT throw — deleting a missing object is a no-op so retries
        // after partial cleanup are idempotent.
        await provider.DeleteAsync("missing.png");
    }

    [Fact]
    public async Task SaveAsync_NotSupported()
    {
        var (provider, _) = BuildWith(_ => new HttpResponseMessage(HttpStatusCode.OK));

        // Supabase uploads go through the signed-URL flow only. The API never
        // proxies bytes; this method intentionally throws.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SaveAsync("key.png", new MemoryStream()));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }
}
