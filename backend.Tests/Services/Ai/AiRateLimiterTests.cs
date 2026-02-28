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
}
