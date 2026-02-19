using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Publishing;

/// <summary>
/// Abstraction for publishing stories to social media platforms.
/// Same contract as IPostPublisher but separate interface to avoid
/// conflicting platform registrations (e.g., both InstagramPublisher
/// and InstagramStoryPublisher handle Platform.Instagram).
/// </summary>
public interface IStoryPublisher
{
    /// <summary>
    /// The platform this story publisher handles.
    /// </summary>
    Platform SupportedPlatform { get; }

    /// <summary>
    /// Publish a story to the target platform.
    /// Must be idempotent - safe to call multiple times.
    /// </summary>
    Task<PublishResult> PublishAsync(Guid postId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the appropriate story publisher for a given platform.
/// </summary>
public interface IStoryPublisherResolver
{
    IStoryPublisher? GetPublisher(Platform platform);
}

/// <summary>
/// Default implementation that resolves story publishers from DI container.
/// </summary>
public class StoryPublisherResolver : IStoryPublisherResolver
{
    private readonly IEnumerable<IStoryPublisher> _publishers;

    public StoryPublisherResolver(IEnumerable<IStoryPublisher> publishers)
    {
        _publishers = publishers;
    }

    public IStoryPublisher? GetPublisher(Platform platform)
    {
        return _publishers.FirstOrDefault(p => p.SupportedPlatform == platform);
    }
}
