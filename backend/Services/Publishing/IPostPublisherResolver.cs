using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Publishing;

/// <summary>
/// Resolves the appropriate publisher for a given platform.
/// </summary>
public interface IPostPublisherResolver
{
    /// <summary>
    /// Get the publisher for a specific platform.
    /// </summary>
    /// <param name="platform">The target platform</param>
    /// <returns>Publisher instance or null if not supported</returns>
    IPostPublisher? GetPublisher(Platform platform);
}

/// <summary>
/// Default implementation that resolves publishers from DI container.
/// </summary>
public class PostPublisherResolver : IPostPublisherResolver
{
    private readonly IEnumerable<IPostPublisher> _publishers;

    public PostPublisherResolver(IEnumerable<IPostPublisher> publishers)
    {
        _publishers = publishers;
    }

    public IPostPublisher? GetPublisher(Platform platform)
    {
        return _publishers.FirstOrDefault(p => p.SupportedPlatform == platform);
    }
}
