namespace PostPilot.Api.Services.Ai;

public interface IAiRateLimiter
{
    Task<bool> TryAcquireAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<int> GetRemainingCallsAsync(Guid userId, CancellationToken cancellationToken = default);
}
