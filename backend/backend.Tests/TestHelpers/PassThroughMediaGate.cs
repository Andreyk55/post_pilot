using PostPilot.Api.DTOs;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Validation;

namespace PostPilot.Api.Tests;

/// <summary>
/// A media validation gate that accepts everything. Used by tests that exercise behavior
/// OTHER than media validation, so the gate never becomes the reason a test passes/fails.
/// Tests that specifically assert media-gate behavior use the real <see cref="MediaValidationGate"/>.
/// </summary>
public sealed class PassThroughMediaGate : IMediaValidationGate
{
    public Task<MediaGateResult> ValidateAsync(
        Guid workspaceId,
        IReadOnlyList<MediaGateItem> items,
        IReadOnlyList<MediaGateTarget> targets,
        CancellationToken cancellationToken = default,
        bool requireOwnedStorageKey = false)
        => Task.FromResult(MediaGateResult.Valid);

    public Task<string?> ValidateSingleAsync(
        Guid workspaceId,
        MediaGateItem item,
        MediaGateTarget target,
        CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task<MediaValidationResult> ValidateForDisplayAsync(
        Guid workspaceId,
        MediaGateItem item,
        MediaGateTarget target,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new MediaValidationResult(
            ValidationStatus.Valid,
            Array.Empty<MediaValidationError>(),
            Array.Empty<MediaValidationWarning>(),
            null));
}
