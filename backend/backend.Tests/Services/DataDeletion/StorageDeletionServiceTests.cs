using Microsoft.Extensions.Logging.Abstractions;
using PostPilot.Api.Services.DataDeletion;
using PostPilot.Api.Tests.TestHelpers;
using Xunit;

namespace PostPilot.Api.Tests.Services.DataDeletion;

public class StorageDeletionServiceTests
{
    private const string FbPrefix = "users/u1/workspaces/w1/providers/meta-facebook/";
    private const string IgPrefix = "users/u1/workspaces/w1/providers/meta-instagram/";

    private static StorageDeletionService NewService(RecordingStorageProvider storage) =>
        new(storage, NullLogger<StorageDeletionService>.Instance);

    [Fact]
    public async Task Deletes_only_keys_under_allowed_prefixes()
    {
        var storage = new RecordingStorageProvider();
        var keys = new string?[]
        {
            FbPrefix + "media/a.jpg",
            IgPrefix + "media/b.jpg",
            "users/OTHER/workspaces/w1/providers/meta-facebook/c.jpg", // wrong user
            "users/u1/workspaces/OTHER/providers/meta-facebook/d.jpg", // wrong workspace
            "users/u1/workspaces/w1/providers/linkedin/e.jpg",          // wrong provider
            "media/legacy.jpg",                                          // legacy/unsafe
        };

        var result = await NewService(storage).DeleteObjectsBestEffortAsync(
            keys, new[] { FbPrefix, IgPrefix }, CancellationToken.None);

        Assert.Equal(2, result.Deleted);
        Assert.Equal(4, result.SkippedUnsafe);
        Assert.Contains(FbPrefix + "media/a.jpg", storage.DeletedKeys);
        Assert.Contains(IgPrefix + "media/b.jpg", storage.DeletedKeys);
        Assert.DoesNotContain("media/legacy.jpg", storage.DeletedKeys);
    }

    [Fact]
    public async Task Null_empty_and_duplicate_keys_are_handled()
    {
        var storage = new RecordingStorageProvider();
        var keys = new string?[] { null, "", "   ", FbPrefix + "x.jpg", FbPrefix + "x.jpg" };

        var result = await NewService(storage).DeleteObjectsBestEffortAsync(
            keys, new[] { FbPrefix }, CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        Assert.Single(storage.DeletedKeys);
    }

    [Fact]
    public async Task Storage_failure_is_recorded_as_warning_not_thrown()
    {
        var storage = new RecordingStorageProvider
        {
            ThrowOnDelete = key => key.EndsWith("boom.jpg"),
        };
        var keys = new string?[] { FbPrefix + "ok.jpg", FbPrefix + "boom.jpg" };

        var result = await NewService(storage).DeleteObjectsBestEffortAsync(
            keys, new[] { FbPrefix }, CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        Assert.Single(result.Warnings);
        Assert.Contains(FbPrefix + "ok.jpg", storage.DeletedKeys);
    }

    [Fact]
    public async Task Empty_allowed_prefixes_deletes_nothing()
    {
        var storage = new RecordingStorageProvider();

        var result = await NewService(storage).DeleteObjectsBestEffortAsync(
            new string?[] { FbPrefix + "a.jpg" }, Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(0, result.Deleted);
        Assert.Empty(storage.DeletedKeys);
    }
}
