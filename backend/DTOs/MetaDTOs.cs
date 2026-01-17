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
    List<ConnectedInstagramAccountDto> InstagramAccounts
);

public record ConnectedPageDto(
    string Id,
    string PageId,
    string Name,
    string? Category,
    string? PictureUrl
);

public record ConnectedInstagramAccountDto(
    string Id,
    string IgBusinessId,
    string Username,
    string? Name,
    string? ProfilePictureUrl,
    string PageId,
    string PageName
);

// Available Pages (for manage flow)
public record MetaAvailablePagesResponse(
    List<FacebookPageDto> Pages
);
