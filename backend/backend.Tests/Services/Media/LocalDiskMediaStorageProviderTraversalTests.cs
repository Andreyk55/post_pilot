using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PostPilot.Api.Services.Media;
using Xunit;

namespace PostPilot.Api.Tests.Services.Media;

/// <summary>
/// L1: the local-disk media provider must never read/write/delete outside its upload root.
/// Storage keys are canonicalized and rejected if they are rooted/absolute or escape the root
/// via ".." traversal, while legitimate server-generated keys keep working.
/// </summary>
public class LocalDiskMediaStorageProviderTraversalTests
{
    private static LocalDiskMediaStorageProvider NewProvider()
        => new(NullLogger<LocalDiskMediaStorageProvider>.Instance, "http://localhost:5122");

    // ── path resolution (authoritative check) ─────────────────────────────────────

    [Fact]
    public void ValidGeneratedKey_resolves_inside_root()
    {
        var provider = NewProvider();
        var key = $"users/{Guid.NewGuid():D}/workspaces/{Guid.NewGuid():D}/providers/meta-facebook/media/{Guid.NewGuid():D}/photo.jpg";

        Assert.True(provider.TryResolveLocalPath(key, out var path));
        Assert.EndsWith("photo.jpg", path);
        Assert.Contains($"uploads{Path.DirectorySeparatorChar}", path);
    }

    [Fact]
    public void LegacyMediaPrefixKey_resolves_inside_root()
    {
        var provider = NewProvider();
        Assert.True(provider.TryResolveLocalPath($"media/{Guid.NewGuid():D}.jpg", out var path));
        Assert.EndsWith(".jpg", path);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("media/../../secret.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32\\x")]
    [InlineData("")]
    public void TraversalKeys_are_rejected(string key)
    {
        Assert.False(NewProvider().TryResolveLocalPath(key, out _));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    public void AbsoluteOrRootedKeys_are_rejected(string key)
    {
        Assert.False(NewProvider().TryResolveLocalPath(key, out _));
    }

    // ── the guard applies to every read/write/delete surface ──────────────────────

    [Fact]
    public async Task OpenRead_returns_null_for_traversal_key()
    {
        Assert.Null(await NewProvider().OpenReadAsync("../secret.txt"));
    }

    [Fact]
    public async Task GetLocalFilePath_returns_null_for_traversal_key()
    {
        Assert.Null(await NewProvider().GetLocalFilePathAsync("../../secret.txt"));
    }

    [Fact]
    public async Task ObjectExists_is_false_for_traversal_key()
    {
        Assert.False(await NewProvider().ObjectExistsAsync("../secret.txt"));
    }

    [Fact]
    public async Task Save_throws_for_traversal_key()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("nope"));
        await Assert.ThrowsAsync<ArgumentException>(() => NewProvider().SaveAsync("../escape.txt", content));
    }

    [Fact]
    public async Task Save_throws_for_absolute_key()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("nope"));
        await Assert.ThrowsAsync<ArgumentException>(() => NewProvider().SaveAsync("/tmp/escape.txt", content));
    }

    [Fact]
    public async Task Delete_is_noop_for_traversal_key()
    {
        // Must not throw and must not touch anything outside the root.
        await NewProvider().DeleteAsync("../secret.txt");
    }

    // ── round-trip for a legitimate key still works ───────────────────────────────

    [Fact]
    public async Task ValidKey_saves_reads_and_deletes()
    {
        var provider = NewProvider();
        var key = $"media/{Guid.NewGuid():N}.jpg";
        var bytes = new byte[] { 9, 8, 7, 6 };

        await provider.SaveAsync(key, new MemoryStream(bytes));
        try
        {
            await using var stream = await provider.OpenReadAsync(key);
            Assert.NotNull(stream);
            using var ms = new MemoryStream();
            await stream!.CopyToAsync(ms);
            Assert.Equal(bytes, ms.ToArray());
        }
        finally
        {
            await provider.DeleteAsync(key);
        }
    }
}
