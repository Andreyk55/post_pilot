using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Ai;

public class InMemoryAiRateLimiter : IAiRateLimiter
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<InMemoryAiRateLimiter> _logger;
    private readonly AiRateLimiterOptions _options;
    private readonly TimeSpan _windowDuration;

    public InMemoryAiRateLimiter(
        IMemoryCache cache,
        ILogger<InMemoryAiRateLimiter> logger,
        IOptions<AiRateLimiterOptions> options)
    {
        _cache = cache;
        _logger = logger;
        _options = options.Value;
        _windowDuration = TimeSpan.FromHours(_options.WindowHours);
    }

    public Task<bool> TryAcquireAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // The quota is resolved per user: override users get OverrideMaxCallsPerDay,
        // everyone else gets MaxCallsPerDay. The time window is the same for all users.
        var maxCalls = _options.GetMaxCallsForUser(userId);
        var key = BuildKey(userId);
        var now = DateTime.UtcNow;

        // Fixed-window behavior: the window starts when the user's FIRST request
        // creates the cache entry (WindowStart = now), and the cache entry expires
        // after WindowHours. Example: WindowHours=24 and first call at 14:30 → the
        // counter resets around 14:30 the next day.
        var entry = _cache.GetOrCreate(key, cacheEntry =>
        {
            cacheEntry.AbsoluteExpirationRelativeToNow = _windowDuration;
            return new RateLimitEntry { Count = 0, WindowStart = now };
        })!;

        // Reset window if expired (defensive: normally the cache entry has already
        // been evicted by AbsoluteExpirationRelativeToNow before we reach here).
        if (now - entry.WindowStart >= _windowDuration)
        {
            entry.Count = 0;
            entry.WindowStart = now;
        }

        if (entry.Count >= maxCalls)
        {
            _logger.LogWarning("Rate limit exceeded for user {UserId}. Count: {Count}", userId, entry.Count);
            return Task.FromResult(false);
        }

        entry.Count++;
        // Re-set with the window duration so the entry keeps its original expiration
        // anchored to WindowStart (resetting the absolute expiration would slide the
        // window; instead we rely on the entry having been created with
        // AbsoluteExpirationRelativeToNow on first acquire).
        _cache.Set(key, entry, new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = new DateTimeOffset(entry.WindowStart + _windowDuration)
        });

        _logger.LogDebug("Rate limit acquired for user {UserId}. Count: {Count}/{Max}", userId, entry.Count, maxCalls);
        return Task.FromResult(true);
    }

    public Task<int> GetRemainingCallsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var maxCalls = _options.GetMaxCallsForUser(userId);
        var key = BuildKey(userId);
        var now = DateTime.UtcNow;

        if (!_cache.TryGetValue(key, out RateLimitEntry? entry) || entry == null)
        {
            return Task.FromResult(maxCalls);
        }

        // Check if window expired
        if (now - entry.WindowStart >= _windowDuration)
        {
            return Task.FromResult(maxCalls);
        }

        return Task.FromResult(Math.Max(0, maxCalls - entry.Count));
    }

    private static string BuildKey(Guid userId) => $"ratelimit:ai:{userId}";

    private class RateLimitEntry
    {
        public int Count { get; set; }
        public DateTime WindowStart { get; set; }
    }
}
