using PostPilot.Api.DTOs;

namespace PostPilot.Api.Services.Support;

/// <summary>
/// Creates authenticated in-app "Contact Us" support messages. The implementation owns
/// all validation, trimming, length enforcement, and per-user abuse limiting, so the
/// controller stays thin.
///
/// <para>The caller (controller) is responsible for supplying the authenticated user id
/// and an optional already-resolved workspace id. This service NEVER reads ids from the
/// request body — the body carries only category/subject/message.</para>
/// </summary>
public interface ISupportContactService
{
    /// <summary>
    /// Validates and persists a new support request for <paramref name="authenticatedUserId"/>.
    /// Throws <see cref="SupportValidationException"/> for bad input and
    /// <see cref="SupportRateLimitExceededException"/> when the per-user window cap is hit.
    /// </summary>
    Task<SupportContactResponse> CreateAsync(
        Guid authenticatedUserId,
        Guid? workspaceId,
        CreateSupportContactRequest request,
        CancellationToken ct);
}
