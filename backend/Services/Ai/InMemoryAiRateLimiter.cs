using Microsoft.Extensions.Caching.Memory;

namespace PostPilot.Api.Services.Ai;

public class InMemoryAiRateLimiter : IAiRateLimiter
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<InMemoryAiRateLimiter> _logger;

    private const int MaxCallsPerDay = 20;
    private static readonly TimeSpan WindowDuration = TimeSpan.FromDays(1);

    public InMemoryAiRateLimiter(IMemoryCache cache, ILogger<InMemoryAiRateLimiter> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public Task<bool> TryAcquireAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(userId);
        var now = DateTime.UtcNow;

        var entry = _cache.GetOrCreate(key, cacheEntry =>
        {
            cacheEntry.AbsoluteExpirationRelativeToNow = WindowDuration;
            return new RateLimitEntry { Count = 0, WindowStart = now };
        })!;

        // Reset window if expired
        if (now - entry.WindowStart >= WindowDuration)
        {
            entry.Count = 0;
            entry.WindowStart = now;
        }

        if (entry.Count >= MaxCallsPerDay)
        {
            _logger.LogWarning("Rate limit exceeded for user {UserId}. Count: {Count}", userId, entry.Count);
            return Task.FromResult(false);
        }

        entry.Count++;
        _cache.Set(key, entry, WindowDuration);

        _logger.LogDebug("Rate limit acquired for user {UserId}. Count: {Count}/{Max}", userId, entry.Count, MaxCallsPerDay);
        return Task.FromResult(true);
    }

    public Task<int> GetRemainingCallsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(userId);
        var now = DateTime.UtcNow;

        if (!_cache.TryGetValue(key, out RateLimitEntry? entry) || entry == null)
        {
            return Task.FromResult(MaxCallsPerDay);
        }

        // Check if window expired
        if (now - entry.WindowStart >= WindowDuration)
        {
            return Task.FromResult(MaxCallsPerDay);
        }

        return Task.FromResult(Math.Max(0, MaxCallsPerDay - entry.Count));
    }

    private static string BuildKey(Guid userId) => $"ratelimit:ai:{userId}";

    private class RateLimitEntry
    {
        public int Count { get; set; }
        public DateTime WindowStart { get; set; }
    }
}
