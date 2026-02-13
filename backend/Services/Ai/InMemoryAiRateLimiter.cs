using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Ai;

public class InMemoryAiRateLimiter : IAiRateLimiter
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<InMemoryAiRateLimiter> _logger;
    private readonly int _maxCallsPerDay;
    private readonly TimeSpan _windowDuration;

    public InMemoryAiRateLimiter(
        IMemoryCache cache,
        ILogger<InMemoryAiRateLimiter> logger,
        IOptions<AiRateLimiterOptions> options)
    {
        _cache = cache;
        _logger = logger;
        var resolvedOptions = options.Value ?? new AiRateLimiterOptions();
        _maxCallsPerDay = resolvedOptions.MaxCallsPerDay > 0 ? resolvedOptions.MaxCallsPerDay : 20;
        _windowDuration = resolvedOptions.WindowHours > 0
            ? TimeSpan.FromHours(resolvedOptions.WindowHours)
            : TimeSpan.FromHours(24);
    }

    public Task<bool> TryAcquireAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(userId);
        var now = DateTime.UtcNow;

        var entry = _cache.GetOrCreate(key, cacheEntry =>
        {
            cacheEntry.AbsoluteExpirationRelativeToNow = _windowDuration;
            return new RateLimitEntry { Count = 0, WindowStart = now };
        })!;

        // Reset window if expired
        if (now - entry.WindowStart >= _windowDuration)
        {
            entry.Count = 0;
            entry.WindowStart = now;
        }

        if (entry.Count >= _maxCallsPerDay)
        {
            _logger.LogWarning("Rate limit exceeded for user {UserId}. Count: {Count}", userId, entry.Count);
            return Task.FromResult(false);
        }

        entry.Count++;
        _cache.Set(key, entry, _windowDuration);

        _logger.LogDebug("Rate limit acquired for user {UserId}. Count: {Count}/{Max}", userId, entry.Count, _maxCallsPerDay);
        return Task.FromResult(true);
    }

    public Task<int> GetRemainingCallsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(userId);
        var now = DateTime.UtcNow;

        if (!_cache.TryGetValue(key, out RateLimitEntry? entry) || entry == null)
        {
            return Task.FromResult(_maxCallsPerDay);
        }

        // Check if window expired
        if (now - entry.WindowStart >= _windowDuration)
        {
            return Task.FromResult(_maxCallsPerDay);
        }

        return Task.FromResult(Math.Max(0, _maxCallsPerDay - entry.Count));
    }

    private static string BuildKey(Guid userId) => $"ratelimit:ai:{userId}";

    private class RateLimitEntry
    {
        public int Count { get; set; }
        public DateTime WindowStart { get; set; }
    }
}
