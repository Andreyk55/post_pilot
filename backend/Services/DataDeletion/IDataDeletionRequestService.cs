using PostPilot.Api.Entities;
using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.DataDeletion;

/// <summary>
/// Owns the lifecycle of <see cref="DataDeletionRequest"/> audit rows and confirmation
/// codes. Does NOT perform any purge — that is <see cref="IMetaDataDeletionService"/>.
/// Separated so the controller can record/track a request independently of the work.
/// </summary>
public interface IDataDeletionRequestService
{
    /// <summary>
    /// Creates a <see cref="DataDeletionStatus.Processing"/> request with a fresh,
    /// random, URL-safe confirmation code and persists it.
    /// </summary>
    Task<DataDeletionRequest> CreateProcessingAsync(
        ProviderType provider,
        string providerAccountId,
        CancellationToken ct);

    /// <summary>Marks the request Completed and stamps the resolved user/workspace + optional warning.</summary>
    Task MarkCompletedAsync(string confirmationCode, Guid? userId, Guid? workspaceId, string? warning, CancellationToken ct);

    /// <summary>Marks the request AlreadyDeleted (no matching connection/data).</summary>
    Task MarkAlreadyDeletedAsync(string confirmationCode, CancellationToken ct);

    /// <summary>Marks the request Failed with a safe, non-leaking error summary.</summary>
    Task MarkFailedAsync(string confirmationCode, string safeError, CancellationToken ct);

    /// <summary>Public status projection for the status endpoint, or null if the code is unknown.</summary>
    Task<DataDeletionStatusDto?> GetStatusAsync(string confirmationCode, CancellationToken ct);
}

/// <summary>
/// Public, non-identifying status projection. Deliberately omits internal DB ids,
/// userId, workspaceId, providerAccountId, tokens, and raw error detail.
/// </summary>
public sealed record DataDeletionStatusDto(
    string ConfirmationCode,
    string Provider,
    string Status,
    DateTime RequestedAt,
    DateTime? CompletedAt);
