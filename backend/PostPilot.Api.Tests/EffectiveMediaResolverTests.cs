using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Validation;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// Pure-function tests for <see cref="EffectiveMediaResolver"/> — the single source of truth for
/// "which bytes do we validate/publish for this target?". This is the helper that makes Instagram
/// PNG support and the advisory/gate consistency work, so its decisions are pinned here.
/// </summary>
public class EffectiveMediaResolverTests
{
    private static Media Png(string? derivativeKey = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        StorageProvider = "local-disk",
        Bucket = "",
        StorageKey = "original.png",
        OriginalFileName = "original.png",
        ContentType = "image/png",
        SizeBytes = 1234,
        Status = MediaUploadStatus.Uploaded,
        CreatedAt = DateTime.UtcNow,
        InstagramImageStorageKey = derivativeKey,
        InstagramImageMimeType = derivativeKey == null ? null : "image/jpeg",
        InstagramImageSizeBytes = derivativeKey == null ? null : 999,
    };

    private static Media Jpeg() => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        StorageProvider = "local-disk",
        Bucket = "",
        StorageKey = "original.jpg",
        OriginalFileName = "original.jpg",
        ContentType = "image/jpeg",
        SizeBytes = 4321,
        Status = MediaUploadStatus.Uploaded,
        CreatedAt = DateTime.UtcNow,
    };

    private static Media Video() => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        StorageProvider = "local-disk",
        Bucket = "",
        StorageKey = "clip.mp4",
        OriginalFileName = "clip.mp4",
        ContentType = "video/mp4",
        SizeBytes = 5_000_000,
        Status = MediaUploadStatus.Uploaded,
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public void InstagramPng_WithDerivative_ResolvesToDerivative()
    {
        var eff = EffectiveMediaResolver.Resolve(Png("original.ig.jpg"), MediaType.Image, Platform.Instagram);

        Assert.True(eff.IsDerivative);
        Assert.False(eff.DerivativeMissing);
        Assert.Equal("original.ig.jpg", eff.StorageKey);
        Assert.Equal("image/jpeg", eff.MimeType);
        Assert.Equal(999, eff.SizeBytes);
    }

    [Fact]
    public void InstagramPng_WithoutDerivative_SignalsMissing()
    {
        var eff = EffectiveMediaResolver.Resolve(Png(derivativeKey: null), MediaType.Image, Platform.Instagram);

        Assert.True(eff.DerivativeMissing);
        Assert.False(eff.IsDerivative);
    }

    [Fact]
    public void FacebookPng_AlwaysResolvesToOriginal()
    {
        var eff = EffectiveMediaResolver.Resolve(Png("original.ig.jpg"), MediaType.Image, Platform.Facebook);

        Assert.False(eff.IsDerivative);
        Assert.False(eff.DerivativeMissing);
        Assert.Equal("original.png", eff.StorageKey);
        Assert.Equal("image/png", eff.MimeType);
    }

    [Fact]
    public void InstagramJpeg_ResolvesToOriginal()
    {
        var eff = EffectiveMediaResolver.Resolve(Jpeg(), MediaType.Image, Platform.Instagram);

        Assert.False(eff.IsDerivative);
        Assert.False(eff.DerivativeMissing);
        Assert.Equal("original.jpg", eff.StorageKey);
    }

    [Fact]
    public void Video_NeverUsesDerivative_EvenForInstagram()
    {
        var eff = EffectiveMediaResolver.Resolve(Video(), MediaType.Video, Platform.Instagram);

        Assert.False(eff.IsDerivative);
        Assert.False(eff.DerivativeMissing);
        Assert.Equal("clip.mp4", eff.StorageKey);
        Assert.Equal("video/mp4", eff.MimeType);
    }
}
