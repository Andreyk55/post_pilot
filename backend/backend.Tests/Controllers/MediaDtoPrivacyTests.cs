using PostPilot.Api.Controllers;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

/// <summary>
/// Pins the media-privacy redesign's DTO contract: StorageKey must never appear on any
/// frontend-facing response shape. These are compile/reflection-level guards so a future
/// change that re-adds a StorageKey property to one of these records fails a test immediately
/// rather than being caught only by manual review.
/// </summary>
public class MediaDtoPrivacyTests
{
    [Theory]
    [InlineData(typeof(InitUploadResponse))]
    [InlineData(typeof(CompleteUploadResponse))]
    [InlineData(typeof(PostDto))]
    [InlineData(typeof(PostMediaItemDto))]
    [InlineData(typeof(PostPilot.Api.DTOs.PostDetailsDto))]
    [InlineData(typeof(PostPilot.Api.DTOs.PostDetailsMediaItemDto))]
    [InlineData(typeof(PostPilot.Api.DTOs.MediaThumbnailDto))]
    public void Dto_has_no_StorageKey_property(Type dtoType)
    {
        var property = dtoType.GetProperty("StorageKey");
        Assert.Null(property);
    }

    [Fact]
    public void InitUploadResponse_carries_MediaId_and_PreviewUrl()
    {
        Assert.NotNull(typeof(InitUploadResponse).GetProperty("MediaId"));
        Assert.NotNull(typeof(InitUploadResponse).GetProperty("PreviewUrl"));
    }

    [Fact]
    public void CompleteUploadResponse_carries_MediaId_and_PreviewUrl()
    {
        Assert.NotNull(typeof(CompleteUploadResponse).GetProperty("MediaId"));
        Assert.NotNull(typeof(CompleteUploadResponse).GetProperty("PreviewUrl"));
    }

    [Fact]
    public void PostDto_and_PostMediaItemDto_carry_MediaId()
    {
        Assert.NotNull(typeof(PostDto).GetProperty("MediaId"));
        Assert.NotNull(typeof(PostMediaItemDto).GetProperty("MediaId"));
    }

    [Fact]
    public void MediaThumbnailDto_carries_MediaId_instead_of_StorageKey()
    {
        Assert.NotNull(typeof(PostPilot.Api.DTOs.MediaThumbnailDto).GetProperty("MediaId"));
    }
}
