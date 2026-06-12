using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PostPilot.Api.Services.Ai;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Services.Ai;

public class AiRateLimiterTests
{
    private readonly InMemoryAiRateLimiter _rateLimiter;
    private readonly Guid _testUserId = Guid.NewGuid();

    public AiRateLimiterTests()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = NullLogger<InMemoryAiRateLimiter>.Instance;
        _rateLimiter = new InMemoryAiRateLimiter(cache, logger, Options.Create(new AiRateLimiterOptions
        {
            MaxCallsPerDay = 20,
            WindowHours = 24
        }));
    }

    [Fact]
    public async Task TryAcquireAsync_FirstCall_ReturnsTrue()
    {
        var result = await _rateLimiter.TryAcquireAsync(_testUserId);

        Assert.True(result);
    }

    [Fact]
    public async Task TryAcquireAsync_UnderLimit_ReturnsTrue()
    {
        // Make 19 calls (under the 20 limit)
        for (int i = 0; i < 19; i++)
        {
            await _rateLimiter.TryAcquireAsync(_testUserId);
        }

        var result = await _rateLimiter.TryAcquireAsync(_testUserId);

        Assert.True(result);
    }

    [Fact]
    public async Task TryAcquireAsync_AtLimit_ReturnsFalse()
    {
        // Make 20 calls to hit the limit
        for (int i = 0; i < 20; i++)
        {
            await _rateLimiter.TryAcquireAsync(_testUserId);
        }

        // 21st call should fail
        var result = await _rateLimiter.TryAcquireAsync(_testUserId);

        Assert.False(result);
    }

    [Fact]
    public async Task TryAcquireAsync_DifferentUsers_IndependentLimits()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        // Exhaust user1's limit
        for (int i = 0; i < 20; i++)
        {
            await _rateLimiter.TryAcquireAsync(user1);
        }

        // User2 should still be able to make calls
        var result = await _rateLimiter.TryAcquireAsync(user2);

        Assert.True(result);
    }

    [Fact]
    public async Task GetRemainingCallsAsync_NewUser_ReturnsMaxCalls()
    {
        var newUser = Guid.NewGuid();

        var remaining = await _rateLimiter.GetRemainingCallsAsync(newUser);

        Assert.Equal(20, remaining);
    }

    [Fact]
    public async Task GetRemainingCallsAsync_AfterCalls_ReturnsCorrectRemaining()
    {
        // Make 5 calls
        for (int i = 0; i < 5; i++)
        {
            await _rateLimiter.TryAcquireAsync(_testUserId);
        }

        var remaining = await _rateLimiter.GetRemainingCallsAsync(_testUserId);

        Assert.Equal(15, remaining);
    }

    [Fact]
    public async Task GetRemainingCallsAsync_ExhaustedLimit_ReturnsZero()
    {
        // Exhaust limit
        for (int i = 0; i < 20; i++)
        {
            await _rateLimiter.TryAcquireAsync(_testUserId);
        }

        var remaining = await _rateLimiter.GetRemainingCallsAsync(_testUserId);

        Assert.Equal(0, remaining);
    }

    // ── Override quota tests ────────────────────────────────────────────────

    private static InMemoryAiRateLimiter BuildLimiter(AiRateLimiterOptions options)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = NullLogger<InMemoryAiRateLimiter>.Instance;
        return new InMemoryAiRateLimiter(cache, logger, Options.Create(options));
    }

    [Fact]
    public async Task NormalUser_GetsDefaultQuota()
    {
        var overrideUser = Guid.NewGuid();
        var normalUser = Guid.NewGuid();
        var limiter = BuildLimiter(new AiRateLimiterOptions
        {
            MaxCallsPerDay = 20,
            WindowHours = 24,
            OverrideMaxCallsPerDay = 30,
            OverrideUserIds = overrideUser.ToString()
        });

        // Normal user is capped at the default 20.
        for (int i = 0; i < 20; i++)
        {
            Assert.True(await limiter.TryAcquireAsync(normalUser));
        }

        Assert.False(await limiter.TryAcquireAsync(normalUser));
        Assert.Equal(0, await limiter.GetRemainingCallsAsync(normalUser));
    }

    [Fact]
    public async Task OverrideUser_GetsOverrideQuota()
    {
        var overrideUser = Guid.NewGuid();
        var limiter = BuildLimiter(new AiRateLimiterOptions
        {
            MaxCallsPerDay = 20,
            WindowHours = 24,
            OverrideMaxCallsPerDay = 30,
            OverrideUserIds = overrideUser.ToString()
        });

        // Override user gets 30, not 20.
        Assert.Equal(30, await limiter.GetRemainingCallsAsync(overrideUser));

        for (int i = 0; i < 30; i++)
        {
            Assert.True(await limiter.TryAcquireAsync(overrideUser));
        }

        // 31st call exceeds the override quota.
        Assert.False(await limiter.TryAcquireAsync(overrideUser));
    }

    [Fact]
    public async Task OverrideUserIds_SupportsCommaSeparatedValuesWithSpaces()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        var user3 = Guid.NewGuid();
        // Mixed spacing around commas; user2 also upper-cased to prove case-insensitivity.
        var limiter = BuildLimiter(new AiRateLimiterOptions
        {
            MaxCallsPerDay = 20,
            WindowHours = 24,
            OverrideMaxCallsPerDay = 30,
            OverrideUserIds = $"  {user1} ,{user2.ToString().ToUpperInvariant()},   {user3}  "
        });

        Assert.Equal(30, await limiter.GetRemainingCallsAsync(user1));
        Assert.Equal(30, await limiter.GetRemainingCallsAsync(user2));
        Assert.Equal(30, await limiter.GetRemainingCallsAsync(user3));
    }

    [Fact]
    public async Task UnknownUser_FallsBackToDefaultQuota()
    {
        var overrideUser = Guid.NewGuid();
        var unknownUser = Guid.NewGuid();
        var limiter = BuildLimiter(new AiRateLimiterOptions
        {
            MaxCallsPerDay = 20,
            WindowHours = 24,
            OverrideMaxCallsPerDay = 30,
            OverrideUserIds = overrideUser.ToString()
        });

        // A user not in the override list uses the default quota.
        Assert.Equal(20, await limiter.GetRemainingCallsAsync(unknownUser));
    }

    [Theory]
    [InlineData(0)]   // missing / unset
    [InlineData(-5)]  // invalid
    public async Task OverrideUser_FallsBackToDefault_WhenOverrideQuotaMissingOrInvalid(int overrideValue)
    {
        var overrideUser = Guid.NewGuid();
        var limiter = BuildLimiter(new AiRateLimiterOptions
        {
            MaxCallsPerDay = 20,
            WindowHours = 24,
            OverrideMaxCallsPerDay = overrideValue,
            OverrideUserIds = overrideUser.ToString()
        });

        // Even though the user is listed, an absent/invalid override quota means
        // they fall back to MaxCallsPerDay (20).
        Assert.Equal(20, await limiter.GetRemainingCallsAsync(overrideUser));

        for (int i = 0; i < 20; i++)
        {
            Assert.True(await limiter.TryAcquireAsync(overrideUser));
        }

        Assert.False(await limiter.TryAcquireAsync(overrideUser));
    }

    [Fact]
    public async Task WindowHours_ControlsCacheExpiration()
    {
        // Fixed-window semantics: the window starts when the user's first request
        // creates the rate-limit cache entry, and the entry expires after WindowHours.
        // Once the entry expires, the per-user counter resets and the user can acquire
        // again. WindowHours is an int (hours), so we can't drive a sub-hour real-time
        // expiry in a unit test; instead we evict the entry — which is exactly what the
        // cache's AbsoluteExpiration does once WindowHours elapses — and confirm the
        // counter resets.
        var user = Guid.NewGuid();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new InMemoryAiRateLimiter(
            cache,
            NullLogger<InMemoryAiRateLimiter>.Instance,
            Options.Create(new AiRateLimiterOptions
            {
                MaxCallsPerDay = 1,
                WindowHours = 24
            }));

        Assert.True(await limiter.TryAcquireAsync(user));   // first call opens the window
        Assert.False(await limiter.TryAcquireAsync(user));  // quota of 1 exhausted within window

        // Simulate the window expiring after WindowHours.
        cache.Remove($"ratelimit:ai:{user}");

        // After expiry the window reopens and the user can acquire again.
        Assert.True(await limiter.TryAcquireAsync(user));
        Assert.Equal(0, await limiter.GetRemainingCallsAsync(user)); // back to 1-call window, now spent
    }
}
