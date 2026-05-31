namespace PostPilot.Api.DTOs;

// OAuth Start
public record MetaOAuthStartResponse(
    string AuthUrl,
    string State
);

// OAuth Callback
public record MetaOAuthCallbackRequest(
    string Code,
    string State
);

public record MetaOAuthCallbackResponse(
    string TempToken,
    List<FacebookPageDto> Pages
);

// OAuth Complete (identity-level connection only, no page selection)
public record MetaOAuthCompleteRequest(
    string Code,
    string State
);

public record MetaOAuthCompleteResponse(
    MetaConnectionDto Connection
);

// Facebook Page
public record FacebookPageDto(
    string Id,
    string Name,
    string? Category,
    string? PictureUrl,
    string? AccessToken = null // Only included in internal responses
);

// Instagram Discovery
public record MetaDiscoverInstagramRequest(
    string TempToken,
    List<string> PageIds
);

public record MetaDiscoverInstagramResponse(
    List<InstagramAccountDto> InstagramAccounts
);

public record InstagramAccountDto(
    string Id,
    string Username,
    string? Name,
    string? ProfilePictureUrl,
    string PageId,
    string PageName
);

// Save Connection
public record MetaSaveConnectionRequest(
    string TempToken,
    List<string> SelectedPageIds,
    List<string> SelectedInstagramIds
);

public record MetaSaveConnectionResponse(
    MetaConnectionDto Connection
);

// Get/Update Connection
public record MetaConnectionResponse(
    MetaConnectionDto? Connection,
    bool IsConnected
);

public record MetaUpdatePagesRequest(
    List<string> SelectedPageIds,
    List<string> SelectedInstagramIds
);

// Connection DTO
public record MetaConnectionDto(
    string Id,
    string UserId,
    DateTime TokenExpiresAt,
    DateTime ConnectedAt,
    List<ConnectedPageDto> Pages,
    List<ConnectedInstagramAccountDto> InstagramAccounts,
    bool IsConnected = true,
    DateTime? DisconnectedAt = null,
    // Stable Meta account identity (FB user id + display name). Nullable on
    // legacy rows that pre-date the AddProviderIdentityAndCancellationMetadata
    // migration; populated for any new/reconnected row.
    string? ProviderAccountId = null,
    string? ProviderAccountName = null,
    // Refines the owned state for the UI: "Active" or "ReauthRequired". When
    // ReauthRequired the connection is still owned but the stored token is invalid;
    // the UI should show a reconnect action. Posts remain visible/retryable.
    string Status = "Active"
);

public record ConnectedPageDto(
    string Id,
    string PageId,
    string Name,
    string? Category,
    string? PictureUrl,
    bool IsConnected = true,
    DateTime? DisconnectedAt = null
);

public record ConnectedInstagramAccountDto(
    string Id,
    string IgBusinessId,
    string Username,
    string? Name,
    string? ProfilePictureUrl,
    string PageId,
    string PageName,
    bool IsConnected = true,
    DateTime? DisconnectedAt = null
);

// Available Pages (for manage flow)
public record MetaAvailablePagesResponse(
    List<FacebookPageDto> Pages
);

// Instagram eligibility discovery (per-page breakdown)
public enum InstagramEligibilityStatus
{
    Connected,
    NotLinked,
    MissingPermission,
    NotProfessional,
    Unknown
}

public record InstagramEligibilityDto(
    string PageId,
    string PageName,
    string? IgUserId,
    string? IgUsername,
    string? IgDisplayName,
    string? IgProfilePictureUrl,
    InstagramEligibilityStatus EligibilityStatus,
    string Reason
);

public record InstagramDiscoveryResponse(
    List<InstagramEligibilityDto> Pages,
    int TotalPages,
    int LinkedCount
);

// Validation Limits
public record ValidationLimitsResponse(
    VoiceProfileLimits VoiceProfile,
    PostLimits Post,
    MediaLimits Media
);

public record VoiceProfileLimits(
    int NameMinLength,
    int NameMaxLength,
    int DescriptionMaxLength,
    int DoRulesMaxLength,
    int DontRulesMaxLength,
    int BannedWordsMaxLength,
    int ExamplePostsMaxLength,
    int TotalMaxLength
);

public record PostLimits(
    int TextMaxLength,
    int TitleMaxLength,
    int MaxHashtags,
    int MaxMediaFiles
);

public record MediaLimits(
    long ImageMaxBytes,
    long VideoMaxBytes
);
