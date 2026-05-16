using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services;

public class MetaOAuthService : IMetaOAuthService
{
    // Reason codes stamped onto Post.ErrorMessage when a post is canceled because
    // its connected asset/account went away. Format: "[ReasonCode] human message".
    internal const string ReasonAssetUnlinked = "AssetUnlinked";
    internal const string ReasonAccountDisconnected = "AccountDisconnected";
    private const string MessageAssetUnlinked = "Post canceled because the target page or account was unlinked.";
    private const string MessageAccountDisconnected = "Post canceled because the Meta account was disconnected.";

    private readonly AppDbContext _context;
    private readonly HttpClient _httpClient;
    private readonly MetaOptions _settings;
    private readonly ILogger<MetaOAuthService> _logger;
    private readonly IPostScheduler _scheduler;
    private readonly string _graphApiBaseUrl;
    private readonly string _oAuthBaseUrl;
    private readonly int _oAuthStateExpirationMinutes;

    public MetaOAuthService(
        AppDbContext context,
        HttpClient httpClient,
        MetaOptions settings,
        ILogger<MetaOAuthService> logger,
        IPostScheduler scheduler,
        MetaApiOptions metaApiOptions,
        PublishingOptions publishingOptions)
    {
        _context = context;
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
        _scheduler = scheduler;
        _graphApiBaseUrl = metaApiOptions.GraphApiBaseUrl;
        _oAuthBaseUrl = metaApiOptions.OAuthDialogBaseUrl;
        _oAuthStateExpirationMinutes = publishingOptions.OAuthStateExpirationMinutes;
    }

    /// <summary>
    /// Cancel any active (Scheduled/RetryPending/Processing) posts whose target page or
    /// Instagram account is about to be removed. Must be called BEFORE the asset rows are
    /// deleted, while TargetPageId / TargetInstagramAccountId still point at them.
    /// </summary>
    private async Task CancelPostsForRemovedAssetsAsync(
        IEnumerable<Guid> removedPageIds,
        IEnumerable<Guid> removedInstagramAccountIds,
        string reasonCode,
        string userMessage)
    {
        var pageIds = removedPageIds.ToHashSet();
        var igIds = removedInstagramAccountIds.ToHashSet();
        if (pageIds.Count == 0 && igIds.Count == 0) return;

        var affected = await _context.Posts
            .Where(p => p.Status == PostStatus.Scheduled
                     || p.Status == PostStatus.RetryPending
                     || p.Status == PostStatus.Processing)
            .Where(p =>
                (p.TargetPageId != null && pageIds.Contains(p.TargetPageId.Value)) ||
                (p.TargetInstagramAccountId != null && igIds.Contains(p.TargetInstagramAccountId.Value)))
            .ToListAsync();

        if (affected.Count == 0) return;

        var now = DateTime.UtcNow;
        var stampedMessage = $"[{reasonCode}] {userMessage}";

        foreach (var post in affected)
        {
            try
            {
                await _scheduler.CancelScheduleAsync(post);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CancelScheduleAsync failed for post {PostId} during {ReasonCode}", post.Id, reasonCode);
            }

            post.Status = PostStatus.Canceled;
            post.CanceledAt = now;
            post.UpdatedAt = now;
            post.ScheduleArn = null;
            post.NextRetryAt = null;
            post.ErrorMessage = stampedMessage;
        }

        await _context.SaveChangesAsync();
        _logger.LogInformation(
            "Canceled {Count} active post(s) due to {ReasonCode}", affected.Count, reasonCode);
    }

    public async Task<MetaOAuthStartResponse> StartOAuthAsync()
    {
        // Generate secure state parameter
        var state = GenerateSecureState();

        // Store state in database for validation
        var oauthState = new MetaOAuthState
        {
            Id = Guid.NewGuid(),
            State = state,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(_oAuthStateExpirationMinutes)
        };

        _context.MetaOAuthStates.Add(oauthState);
        await _context.SaveChangesAsync();

        // Build OAuth URL - Facebook Login scopes only.
        // instagram_business_account discovery works via the Page node with
        // pages_show_list + business_management (no extra IG scopes needed).
        // Instagram publishing scopes (instagram_basic, instagram_content_publish)
        // require Meta app review and will be added later.
        var scopes = string.Join(",", new[]
        {
            "pages_show_list",
            "pages_read_engagement",
            "pages_manage_posts",
            "business_management",
            "public_profile",

            "instagram_basic",
            "instagram_content_publish"
        });

        _logger.LogInformation("Meta OAuth requested scopes: {Scopes}", scopes);

        var authUrl = $"{_oAuthBaseUrl}?" +
            $"client_id={_settings.AppId}" +
            $"&redirect_uri={Uri.EscapeDataString(_settings.RedirectUri)}" +
            $"&state={state}" +
            $"&scope={Uri.EscapeDataString(scopes)}" +
            $"&response_type=code" +
            $"&auth_type=rerequest";

        return new MetaOAuthStartResponse(authUrl, state);
    }

    public async Task<MetaOAuthCallbackResponse> HandleCallbackAsync(string code, string state)
    {
        // Validate state
        var oauthState = await _context.MetaOAuthStates
            .FirstOrDefaultAsync(s => s.State == state && s.ExpiresAt > DateTime.UtcNow);

        if (oauthState == null)
        {
            throw new InvalidOperationException("Invalid or expired OAuth state");
        }

        // Exchange code for access token
        var tokenUrl = $"{_graphApiBaseUrl}/oauth/access_token?" +
            $"client_id={_settings.AppId}" +
            $"&client_secret={_settings.AppSecret}" +
            $"&redirect_uri={Uri.EscapeDataString(_settings.RedirectUri)}" +
            $"&code={code}";

        var tokenResponse = await _httpClient.GetAsync(tokenUrl);
        tokenResponse.EnsureSuccessStatusCode();
        var tokenJson = await tokenResponse.Content.ReadAsStringAsync();
        var tokenData = JsonSerializer.Deserialize<MetaTokenResponse>(tokenJson);
        if (tokenData?.AccessToken == null)
        {
            throw new InvalidOperationException("Failed to obtain access token");
        }

        await DebugWhoLoggedInAsync(tokenData.AccessToken);
        await DebugPermissionsAsync(tokenData.AccessToken);

        // Exchange for long-lived token
        var longLivedTokenUrl = $"{_graphApiBaseUrl}/oauth/access_token?" +
            $"grant_type=fb_exchange_token" +
            $"&client_id={_settings.AppId}" +
            $"&client_secret={_settings.AppSecret}" +
            $"&fb_exchange_token={tokenData.AccessToken}";

        var longLivedResponse = await _httpClient.GetAsync(longLivedTokenUrl);
        longLivedResponse.EnsureSuccessStatusCode();

        var longLivedJson = await longLivedResponse.Content.ReadAsStringAsync();
        var longLivedData = JsonSerializer.Deserialize<MetaTokenResponse>(longLivedJson);

        var accessToken = longLivedData?.AccessToken ?? tokenData.AccessToken;
        var expiresIn = longLivedData?.ExpiresIn ?? tokenData.ExpiresIn ?? 3600;

        // Store token temporarily in state record
        oauthState.TempAccessToken = accessToken;
        oauthState.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
        await _context.SaveChangesAsync();

        // Fetch available pages
        var pages = await FetchUserPagesAsync(accessToken);

        // Return temp token (state ID) and pages
        return new MetaOAuthCallbackResponse(
            oauthState.Id.ToString(),
            pages
        );
    }

    public async Task<MetaOAuthCompleteResponse> CompleteOAuthAsync(string code, string state)
    {
        _logger.LogInformation("CompleteOAuth called with state: {State}", state);

        // Validate state
        var oauthState = await _context.MetaOAuthStates
            .FirstOrDefaultAsync(s => s.State == state && s.ExpiresAt > DateTime.UtcNow);

        if (oauthState == null)
        {
            _logger.LogWarning("State validation failed. State not found or expired: {State}", state);
            throw new InvalidOperationException("Invalid or expired OAuth state");
        }

        _logger.LogInformation("State validated successfully. Exchanging code for token...");

        // Exchange code for access token
        var tokenUrl = $"{_graphApiBaseUrl}/oauth/access_token?" +
            $"client_id={_settings.AppId}" +
            $"&client_secret={_settings.AppSecret}" +
            $"&redirect_uri={Uri.EscapeDataString(_settings.RedirectUri)}" +
            $"&code={code}";

        _logger.LogInformation("Token exchange URL (without secrets): redirect_uri={RedirectUri}", _settings.RedirectUri);

        var tokenResponse = await _httpClient.GetAsync(tokenUrl);

        if (!tokenResponse.IsSuccessStatusCode)
        {
            var errorBody = await tokenResponse.Content.ReadAsStringAsync();
            _logger.LogError("Token exchange failed. Status: {Status}, Body: {Body}", tokenResponse.StatusCode, errorBody);
            throw new InvalidOperationException($"Failed to exchange code for token: {errorBody}");
        }
        var tokenJson = await tokenResponse.Content.ReadAsStringAsync();
        var tokenData = JsonSerializer.Deserialize<MetaTokenResponse>(tokenJson);
        if (tokenData?.AccessToken == null)
        {
            _logger.LogError("Token response did not contain access_token. Response: {Json}", tokenJson);
            throw new InvalidOperationException("Failed to obtain access token");
        }

        _logger.LogInformation("Short-lived token obtained. Exchanging for long-lived token...");

        // Exchange for long-lived token
        var longLivedTokenUrl = $"{_graphApiBaseUrl}/oauth/access_token?" +
            $"grant_type=fb_exchange_token" +
            $"&client_id={_settings.AppId}" +
            $"&client_secret={_settings.AppSecret}" +
            $"&fb_exchange_token={tokenData.AccessToken}";

        var longLivedResponse = await _httpClient.GetAsync(longLivedTokenUrl);
        string? longLivedJson = null;
        MetaTokenResponse? longLivedData = null;

        if (longLivedResponse.IsSuccessStatusCode)
        {
            longLivedJson = await longLivedResponse.Content.ReadAsStringAsync();
            longLivedData = JsonSerializer.Deserialize<MetaTokenResponse>(longLivedJson);
            _logger.LogInformation("Long-lived token obtained successfully");
        }
        else
        {
            var errorBody = await longLivedResponse.Content.ReadAsStringAsync();
            _logger.LogWarning("Long-lived token exchange failed (will use short-lived token). Status: {Status}, Body: {Body}", longLivedResponse.StatusCode, errorBody);
        }

        var accessToken = longLivedData?.AccessToken ?? tokenData.AccessToken;
        var expiresIn = longLivedData?.ExpiresIn ?? tokenData.ExpiresIn ?? 3600;

        var userId = GetCurrentUserId();
        _logger.LogInformation("Saving connection for user {UserId}", userId);

        // Look up any existing connection for this user — connected OR disconnected.
        // If a disconnected one is found we reattach it (resurrect the historical breadcrumb)
        // along with any child pages/IGs that were previously soft-disconnected.
        var existingConnection = await _context.MetaConnections
            .Include(c => c.Pages)
            .Include(c => c.InstagramAccounts)
            .OrderByDescending(c => c.IsConnected)         // prefer currently-connected if both somehow exist
            .ThenByDescending(c => c.ConnectedAt)
            .FirstOrDefaultAsync(c => c.UserId == userId);

        var now = DateTime.UtcNow;
        MetaConnection connection;
        if (existingConnection != null)
        {
            // Refresh tokens. Never delete the child asset rows — existing Posts FK them.
            existingConnection.AccessToken = accessToken;
            existingConnection.TokenExpiresAt = now.AddSeconds(expiresIn);
            existingConnection.UpdatedAt = now;

            if (!existingConnection.IsConnected)
            {
                // Reattaching after a disconnect: clear the soft-delete marker.
                existingConnection.IsConnected = true;
                existingConnection.DisconnectedAt = null;
                existingConnection.ConnectedAt = now;
                _logger.LogInformation(
                    "Reattaching previously-disconnected connection {ConnectionId} for user {UserId}",
                    existingConnection.Id, userId);
            }
            else
            {
                _logger.LogInformation("Refreshing token on connection {ConnectionId}", existingConnection.Id);
            }

            connection = existingConnection;
        }
        else
        {
            connection = new MetaConnection
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AccessToken = accessToken,
                TokenExpiresAt = now.AddSeconds(expiresIn),
                ConnectedAt = now,
                UpdatedAt = now,
                IsConnected = true,
                DisconnectedAt = null,
            };
            _context.MetaConnections.Add(connection);
            _logger.LogInformation("Creating new connection {ConnectionId}", connection.Id);
        }

        // Clean up OAuth state
        _context.MetaOAuthStates.Remove(oauthState);

        await _context.SaveChangesAsync();

        _logger.LogInformation("Meta connection saved successfully. Connection ID: {ConnectionId}", connection.Id);

        return new MetaOAuthCompleteResponse(MapToDto(connection));
    }

    private async Task DebugWhoLoggedInAsync(string accessToken)
    {
        var resp = await _httpClient.GetAsync(
            $"{_graphApiBaseUrl}/me?fields=id,name&access_token={accessToken}");

        var body = await resp.Content.ReadAsStringAsync();
        _logger.LogInformation("Meta /me response: {Body}", body);
    }

    private async Task DebugPermissionsAsync(string accessToken)
    {
        var resp = await _httpClient.GetAsync(
            $"{_graphApiBaseUrl}/me/permissions?access_token={accessToken}");

        var body = await resp.Content.ReadAsStringAsync();
        _logger.LogInformation("Meta /me/permissions response: {Body}", body);
    }

    public async Task<MetaDiscoverInstagramResponse> DiscoverInstagramAccountsAsync(string tempToken, List<string> pageIds)
    {
        string accessToken;

        // Check if tempToken is a GUID (OAuth state ID) or empty (manage mode)
        if (Guid.TryParse(tempToken, out var stateId))
        {
            var oauthState = await _context.MetaOAuthStates.FindAsync(stateId);
            if (oauthState?.TempAccessToken == null)
            {
                throw new InvalidOperationException("Invalid or expired temp token");
            }
            accessToken = oauthState.TempAccessToken;
        }
        else
        {
            // Manage mode - use stored connection token
            // For now, assume single user with fixed ID
            var connection = await _context.MetaConnections
                .FirstOrDefaultAsync(c => c.UserId == GetCurrentUserId());

            if (connection == null)
            {
                throw new InvalidOperationException("No Meta connection found");
            }
            accessToken = connection.AccessToken;
        }

        var instagramAccounts = new List<InstagramAccountDto>();

        // Get pages with their access tokens first
        var pages = await FetchUserPagesAsync(accessToken);
        var selectedPages = pages.Where(p => pageIds.Contains(p.Id)).ToList();

        foreach (var page in selectedPages)
        {
            if (page.AccessToken == null) continue;

            try
            {
                // Get Instagram account linked to this page (Business or Creator)
                // Step 1: Try with subfield expansion for full profile details
                var fields = "name,instagram_business_account{id,username,name,profile_picture_url}," +
                             "connected_instagram_account{id,username,name,profile_picture_url}";
                var igUrl = $"{_graphApiBaseUrl}/{page.Id}?fields={fields}&access_token={page.AccessToken}";
                var response = await _httpClient.GetAsync(igUrl);

                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<MetaPageInstagramResponse>(json);

                var ig = data?.InstagramBusinessAccount ?? data?.ConnectedInstagramAccount;

                // Step 2: If expanded query found nothing, try without subfield expansion
                if (ig == null || string.IsNullOrEmpty(ig.Id))
                {
                    var plainFields = "name,instagram_business_account,connected_instagram_account";
                    var plainUrl = $"{_graphApiBaseUrl}/{page.Id}?fields={plainFields}&access_token={page.AccessToken}";
                    var plainResponse = await _httpClient.GetAsync(plainUrl);

                    if (plainResponse.IsSuccessStatusCode)
                    {
                        var plainJson = await plainResponse.Content.ReadAsStringAsync();
                        var plainData = JsonSerializer.Deserialize<MetaPageInstagramResponse>(plainJson);
                        var igPlain = plainData?.InstagramBusinessAccount ?? plainData?.ConnectedInstagramAccount;
                        if (igPlain != null && !string.IsNullOrEmpty(igPlain.Id))
                        {
                            ig = igPlain;
                        }
                    }
                }

                if (ig != null && !string.IsNullOrEmpty(ig.Id))
                {
                    instagramAccounts.Add(new InstagramAccountDto(
                        ig.Id,
                        ig.Username ?? "",
                        ig.Name,
                        ig.ProfilePictureUrl,
                        page.Id,
                        page.Name
                    ));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Instagram account for page {PageId}", page.Id);
            }
        }

        return new MetaDiscoverInstagramResponse(instagramAccounts);
    }

    public async Task<MetaSaveConnectionResponse> SaveConnectionAsync(string tempToken, List<string> selectedPageIds, List<string> selectedInstagramIds)
    {
        if (!Guid.TryParse(tempToken, out var stateId))
        {
            throw new InvalidOperationException("Invalid temp token");
        }

        var oauthState = await _context.MetaOAuthStates.FindAsync(stateId);
        if (oauthState?.TempAccessToken == null || oauthState.TokenExpiresAt == null)
        {
            throw new InvalidOperationException("Invalid or expired temp token");
        }

        var userId = GetCurrentUserId();
        var now = DateTime.UtcNow;

        // Reuse the existing connection (connected OR disconnected) for this user,
        // or create a fresh one. We NEVER hard-delete — historical child rows are
        // FK targets for Posts and must survive across disconnect/reconnect cycles.
        var connection = await _context.MetaConnections
            .Include(c => c.Pages)
            .Include(c => c.InstagramAccounts)
            .OrderByDescending(c => c.IsConnected)
            .ThenByDescending(c => c.ConnectedAt)
            .FirstOrDefaultAsync(c => c.UserId == userId);

        if (connection == null)
        {
            connection = new MetaConnection
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AccessToken = oauthState.TempAccessToken,
                TokenExpiresAt = oauthState.TokenExpiresAt.Value,
                ConnectedAt = now,
                UpdatedAt = now,
                IsConnected = true,
                DisconnectedAt = null,
            };
            _context.MetaConnections.Add(connection);
        }
        else
        {
            connection.AccessToken = oauthState.TempAccessToken;
            connection.TokenExpiresAt = oauthState.TokenExpiresAt.Value;
            connection.UpdatedAt = now;
            if (!connection.IsConnected)
            {
                connection.IsConnected = true;
                connection.DisconnectedAt = null;
                connection.ConnectedAt = now;
            }
        }

        // Fetch pages with tokens
        var allPages = await FetchUserPagesAsync(oauthState.TempAccessToken);
        var selectedPages = allPages.Where(p => selectedPageIds.Contains(p.Id)).ToList();

        if (!selectedPages.Any())
        {
            throw new InvalidOperationException("At least one page must be selected");
        }

        // Discover Instagram accounts for selected pages
        var igResponse = await DiscoverInstagramAccountsAsync(tempToken, selectedPageIds);
        var selectedIgAccounts = igResponse.InstagramAccounts
            .Where(ig => selectedInstagramIds.Contains(ig.Id))
            .ToList();

        await ReconcileSelectedAssetsAsync(connection, selectedPages, selectedIgAccounts, now);

        // Clean up OAuth state
        _context.MetaOAuthStates.Remove(oauthState);

        await _context.SaveChangesAsync();

        return new MetaSaveConnectionResponse(MapToDto(connection));
    }

    /// <summary>
    /// Reconciles the set of pages/IG accounts attached to <paramref name="connection"/> against
    /// the user's selection. Existing rows (connected or disconnected) with matching external IDs
    /// are reattached and refreshed in-place; new ones are inserted; previously-connected ones
    /// that are not in the selection are soft-disconnected (and their active posts canceled).
    /// </summary>
    private async Task ReconcileSelectedAssetsAsync(
        MetaConnection connection,
        List<FacebookPageDto> selectedPages,
        List<InstagramAccountDto> selectedIgAccounts,
        DateTime now)
    {
        var selectedFbPageIds = selectedPages.Select(p => p.Id).ToHashSet();
        var selectedIgBusinessIds = selectedIgAccounts.Select(i => i.Id).ToHashSet();

        var pagesToDisconnect = new List<ConnectedPage>();
        var igsToDisconnect = new List<ConnectedInstagramAccount>();

        // Reattach or disconnect existing pages
        foreach (var existing in connection.Pages)
        {
            if (selectedFbPageIds.Contains(existing.PageId))
            {
                var src = selectedPages.First(p => p.Id == existing.PageId);
                existing.Name = src.Name;
                existing.Category = src.Category;
                existing.PictureUrl = src.PictureUrl;
                existing.AccessToken = src.AccessToken ?? existing.AccessToken;
                if (!existing.IsConnected)
                {
                    existing.IsConnected = true;
                    existing.DisconnectedAt = null;
                }
            }
            else if (existing.IsConnected)
            {
                pagesToDisconnect.Add(existing);
            }
        }

        // Insert pages that weren't already present.
        // IMPORTANT: use _context.ConnectedPages.Add (not connection.Pages.Add) — when a
        // new entity has a non-default key (Guid.NewGuid() ≠ Guid.Empty), adding via the
        // tracked parent's navigation collection can land in Modified state. Using DbSet.Add
        // forces Added state and generates the INSERT we want.
        var existingPageFbIds = connection.Pages.Select(p => p.PageId).ToHashSet();
        foreach (var page in selectedPages.Where(p => !existingPageFbIds.Contains(p.Id)))
        {
            var newPage = new ConnectedPage
            {
                Id = Guid.NewGuid(),
                MetaConnectionId = connection.Id,
                PageId = page.Id,
                Name = page.Name,
                Category = page.Category,
                PictureUrl = page.PictureUrl,
                AccessToken = page.AccessToken!,
                CreatedAt = now,
                IsConnected = true,
                DisconnectedAt = null,
            };
            _context.ConnectedPages.Add(newPage);
            connection.Pages.Add(newPage);
        }

        // Reattach or disconnect existing IG accounts
        foreach (var existing in connection.InstagramAccounts)
        {
            if (selectedIgBusinessIds.Contains(existing.IgBusinessId))
            {
                var src = selectedIgAccounts.First(i => i.Id == existing.IgBusinessId);
                existing.Username = src.Username;
                existing.Name = src.Name;
                existing.ProfilePictureUrl = src.ProfilePictureUrl;
                existing.PageId = src.PageId;
                existing.PageName = src.PageName;
                if (!existing.IsConnected)
                {
                    existing.IsConnected = true;
                    existing.DisconnectedAt = null;
                }
            }
            else if (existing.IsConnected)
            {
                igsToDisconnect.Add(existing);
            }
        }

        // Insert IGs that weren't already present (same rationale as the page insert above).
        var existingIgBusinessIds = connection.InstagramAccounts.Select(i => i.IgBusinessId).ToHashSet();
        foreach (var ig in selectedIgAccounts.Where(i => !existingIgBusinessIds.Contains(i.Id)))
        {
            var newIg = new ConnectedInstagramAccount
            {
                Id = Guid.NewGuid(),
                MetaConnectionId = connection.Id,
                IgBusinessId = ig.Id,
                Username = ig.Username,
                Name = ig.Name,
                ProfilePictureUrl = ig.ProfilePictureUrl,
                PageId = ig.PageId,
                PageName = ig.PageName,
                CreatedAt = now,
                IsConnected = true,
                DisconnectedAt = null,
            };
            _context.ConnectedInstagramAccounts.Add(newIg);
            connection.InstagramAccounts.Add(newIg);
        }

        // Soft-disconnect the ones the user no longer wants, then cancel their active posts.
        // (Schedule cancellation must happen against the same DbContext so it sees the new state.)
        foreach (var page in pagesToDisconnect)
        {
            page.IsConnected = false;
            page.DisconnectedAt = now;
        }
        foreach (var ig in igsToDisconnect)
        {
            ig.IsConnected = false;
            ig.DisconnectedAt = now;
        }

        if (pagesToDisconnect.Count > 0 || igsToDisconnect.Count > 0)
        {
            await CancelPostsForRemovedAssetsAsync(
                pagesToDisconnect.Select(p => p.Id),
                igsToDisconnect.Select(i => i.Id),
                ReasonAssetUnlinked,
                MessageAssetUnlinked);
        }
    }

    public async Task<MetaConnectionResponse> GetConnectionAsync(Guid userId)
    {
        // Only return the currently-connected MetaConnection. Disconnected rows are kept
        // as historical breadcrumbs for posts but are never surfaced to the UI as "connected".
        var connection = await _context.MetaConnections
            .Include(c => c.Pages.Where(p => p.IsConnected))
            .Include(c => c.InstagramAccounts.Where(i => i.IsConnected))
            .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected);

        if (connection == null)
        {
            return new MetaConnectionResponse(null, false);
        }

        return new MetaConnectionResponse(MapToDto(connection), true);
    }

    public async Task<MetaAvailablePagesResponse> GetAvailablePagesAsync(Guid userId)
    {
        var connection = await _context.MetaConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected);
        if (connection == null)
        {
            throw new InvalidOperationException("No Meta connection found");
        }

        var pages = await FetchUserPagesAsync(connection.AccessToken);
        return new MetaAvailablePagesResponse(pages.Select(p => new FacebookPageDto(
            p.Id, p.Name, p.Category, p.PictureUrl, null // Don't expose access tokens
        )).ToList());
    }

    public async Task<MetaSaveConnectionResponse> UpdateConnectionAsync(Guid userId, List<string> selectedPageIds, List<string> selectedInstagramIds)
    {
        var connection = await _context.MetaConnections
            .Include(c => c.Pages)
            .Include(c => c.InstagramAccounts)
            .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected);

        if (connection == null)
        {
            throw new InvalidOperationException("No Meta connection found");
        }

        // Fetch all available pages
        var allPages = await FetchUserPagesAsync(connection.AccessToken);
        var selectedPages = allPages.Where(p => selectedPageIds.Contains(p.Id)).ToList();

        // Discover Instagram accounts (only for selected pages). Zero is allowed —
        // the user can soft-disconnect every page while keeping the Meta identity.
        var igResponse = selectedPageIds.Any()
            ? await DiscoverInstagramAccountsAsync("", selectedPageIds)
            : new MetaDiscoverInstagramResponse(new List<InstagramAccountDto>());
        var selectedIgAccounts = igResponse.InstagramAccounts
            .Where(ig => selectedInstagramIds.Contains(ig.Id))
            .ToList();

        var now = DateTime.UtcNow;
        connection.UpdatedAt = now;

        await ReconcileSelectedAssetsAsync(connection, selectedPages, selectedIgAccounts, now);

        await _context.SaveChangesAsync();

        return new MetaSaveConnectionResponse(MapToDto(connection));
    }

    public async Task DisconnectAsync(Guid userId)
    {
        var connection = await _context.MetaConnections
            .Include(c => c.Pages)
            .Include(c => c.InstagramAccounts)
            .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected);

        if (connection == null)
        {
            return; // Already disconnected
        }

        // Cancel any active posts targeting this connection's assets first so the worker
        // never picks up something whose connection is gone.
        await CancelPostsForRemovedAssetsAsync(
            connection.Pages.Where(p => p.IsConnected).Select(p => p.Id),
            connection.InstagramAccounts.Where(i => i.IsConnected).Select(i => i.Id),
            ReasonAccountDisconnected,
            MessageAccountDisconnected);

        // Optionally revoke token with Meta — best-effort.
        try
        {
            var revokeUrl = $"{_graphApiBaseUrl}/me/permissions?access_token={connection.AccessToken}";
            await _httpClient.DeleteAsync(revokeUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to revoke Meta token");
        }

        // Soft-disconnect the connection itself and every child asset.
        // Rows are PRESERVED so that historical Post FKs stay valid.
        var now = DateTime.UtcNow;
        connection.IsConnected = false;
        connection.DisconnectedAt = now;
        connection.UpdatedAt = now;
        // Invalidate the user token but leave the column populated for audit.
        // (If you want to scrub it for security, set to string.Empty here.)

        foreach (var page in connection.Pages.Where(p => p.IsConnected))
        {
            page.IsConnected = false;
            page.DisconnectedAt = now;
        }
        foreach (var ig in connection.InstagramAccounts.Where(i => i.IsConnected))
        {
            ig.IsConnected = false;
            ig.DisconnectedAt = now;
        }

        await _context.SaveChangesAsync();
    }

    public async Task<InstagramDiscoveryResponse> DiscoverInstagramEligibilityAsync(Guid userId)
    {
        var connection = await _context.MetaConnections
            .Include(c => c.Pages)
            .Include(c => c.InstagramAccounts)
            .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected);

        if (connection == null)
            throw new InvalidOperationException("No Meta connection found");

        // Log token scopes for diagnostics
        await LogTokenScopesAsync(connection.AccessToken);

        var allPages = await FetchUserPagesAsync(connection.AccessToken);
        _logger.LogInformation("Instagram discovery: found {PageCount} pages for user {UserId}", allPages.Count, userId);

        var eligibilityResults = new List<InstagramEligibilityDto>();
        var linkedCount = 0;

        foreach (var page in allPages)
        {
            var eligibility = await CheckInstagramEligibilityForPageAsync(page);
            eligibilityResults.Add(eligibility);

            if (eligibility.EligibilityStatus == InstagramEligibilityStatus.Connected)
                linkedCount++;

            _logger.LogInformation(
                "Instagram discovery for page {PageId} ({PageName}): status={Status}, igUserId={IgUserId}",
                page.Id, page.Name, eligibility.EligibilityStatus, eligibility.IgUserId ?? "none");
        }

        return new InstagramDiscoveryResponse(eligibilityResults, allPages.Count, linkedCount);
    }

    public async Task<object> DebugInstagramDiscoveryAsync(Guid userId)
    {
        var connection = await _context.MetaConnections
            .Include(c => c.Pages)
            .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected);

        if (connection == null)
            return new { error = "No Meta connection found" };

        // 1) Check token permissions
        string? permissionsJson = null;
        try
        {
            var permResp = await _httpClient.GetAsync(
                $"{_graphApiBaseUrl}/me/permissions?access_token={connection.AccessToken}");
            permissionsJson = await permResp.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            permissionsJson = $"ERROR: {ex.Message}";
        }

        // 2) Requested OAuth scopes
        var requestedScopes = "pages_show_list,pages_read_engagement,pages_manage_posts,business_management,public_profile";

        // 3) Fetch pages with their tokens
        var allPages = await FetchUserPagesAsync(connection.AccessToken);

        // 4) For each page, make BOTH expanded and plain IG queries
        var pageResults = new List<object>();
        foreach (var page in allPages)
        {
            // Query A: with subfield expansion
            var expandedFields = "name,instagram_business_account{id,username,name,profile_picture_url}," +
                         "connected_instagram_account{id,username,name,profile_picture_url}";
            var expandedUrl = $"{_graphApiBaseUrl}/{page.Id}?fields={expandedFields}&access_token={page.AccessToken}";

            string? expandedResponse = null;
            int expandedStatus = 0;
            MetaPageInstagramResponse? expandedData = null;
            try
            {
                var response = await _httpClient.GetAsync(expandedUrl);
                expandedStatus = (int)response.StatusCode;
                expandedResponse = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                    expandedData = JsonSerializer.Deserialize<MetaPageInstagramResponse>(expandedResponse);
            }
            catch (Exception ex)
            {
                expandedResponse = $"ERROR: {ex.Message}";
            }

            // Query B: without subfield expansion (plain)
            var plainFields = "name,instagram_business_account,connected_instagram_account";
            var plainUrl = $"{_graphApiBaseUrl}/{page.Id}?fields={plainFields}&access_token={page.AccessToken}";

            string? plainResponse = null;
            int plainStatus = 0;
            MetaPageInstagramResponse? plainData = null;
            try
            {
                var resp2 = await _httpClient.GetAsync(plainUrl);
                plainStatus = (int)resp2.StatusCode;
                plainResponse = await resp2.Content.ReadAsStringAsync();
                if (resp2.IsSuccessStatusCode)
                    plainData = JsonSerializer.Deserialize<MetaPageInstagramResponse>(plainResponse);
            }
            catch (Exception ex)
            {
                plainResponse = $"ERROR: {ex.Message}";
            }

            // Query C: with USER token instead of page token
            string? userTokenResponse = null;
            int userTokenStatus = 0;
            try
            {
                var userUrl = $"{_graphApiBaseUrl}/{page.Id}?fields={expandedFields}&access_token={connection.AccessToken}";
                var resp3 = await _httpClient.GetAsync(userUrl);
                userTokenStatus = (int)resp3.StatusCode;
                userTokenResponse = await resp3.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                userTokenResponse = $"ERROR: {ex.Message}";
            }

            // Compute linkage result
            var igExpanded = expandedData?.InstagramBusinessAccount ?? expandedData?.ConnectedInstagramAccount;
            var igPlain = plainData?.InstagramBusinessAccount ?? plainData?.ConnectedInstagramAccount;
            var effectiveIgId = (igExpanded != null && !string.IsNullOrEmpty(igExpanded.Id))
                ? igExpanded.Id
                : (igPlain != null && !string.IsNullOrEmpty(igPlain.Id) ? igPlain.Id : null);

            pageResults.Add(new
            {
                pageId = page.Id,
                pageName = page.Name,
                hasPageToken = !string.IsNullOrEmpty(page.AccessToken),
                pageTokenPrefix = page.AccessToken?.Substring(0, Math.Min(20, page.AccessToken?.Length ?? 0)) + "...",
                expandedQuery = new
                {
                    fields = expandedFields,
                    status = expandedStatus,
                    rawJson = expandedResponse,
                    deserializedIBA = expandedData?.InstagramBusinessAccount?.Id ?? "null",
                    deserializedCIA = expandedData?.ConnectedInstagramAccount?.Id ?? "null"
                },
                plainQuery = new
                {
                    fields = plainFields,
                    status = plainStatus,
                    rawJson = plainResponse,
                    deserializedIBA = plainData?.InstagramBusinessAccount?.Id ?? "null",
                    deserializedCIA = plainData?.ConnectedInstagramAccount?.Id ?? "null"
                },
                withUserToken = new { status = userTokenStatus, rawJson = userTokenResponse },
                computedResult = new
                {
                    linked = effectiveIgId != null,
                    effectiveIgId,
                    source = effectiveIgId != null
                        ? (igExpanded != null && !string.IsNullOrEmpty(igExpanded.Id) ? "expanded_query" : "plain_query")
                        : "none"
                }
            });
        }

        return new
        {
            graphApiVersion = "v21.0",
            requestedOAuthScopes = requestedScopes,
            grantedPermissions = permissionsJson,
            userTokenPrefix = connection.AccessToken?.Substring(0, Math.Min(20, connection.AccessToken?.Length ?? 0)) + "...",
            pageCount = allPages.Count,
            pages = pageResults
        };
    }

    internal static InstagramEligibilityDto MapEligibility(
        string pageId,
        string pageName,
        MetaPageInstagramResponse? igResponse,
        bool apiCallFailed,
        string? errorMessage)
    {
        if (apiCallFailed)
        {
            return new InstagramEligibilityDto(
                pageId, pageName, null, null, null, null,
                InstagramEligibilityStatus.Unknown,
                errorMessage ?? "Could not check Instagram status for this Page.");
        }

        // Prefer instagram_business_account; fall back to connected_instagram_account
        // (covers both Business and Creator professional accounts)
        var ig = igResponse?.InstagramBusinessAccount ?? igResponse?.ConnectedInstagramAccount;

        if (ig == null)
        {
            return new InstagramEligibilityDto(
                pageId, pageName, null, null, null, null,
                InstagramEligibilityStatus.NotLinked,
                "No Instagram account is linked to this Facebook Page. Link an Instagram professional account in Meta Business Suite.");
        }

        if (string.IsNullOrEmpty(ig.Id))
        {
            return new InstagramEligibilityDto(
                pageId, pageName, null, null, null, null,
                InstagramEligibilityStatus.NotProfessional,
                "The linked Instagram account is not a Business or Creator account. Convert it in Instagram settings.");
        }

        return new InstagramEligibilityDto(
            pageId, pageName,
            ig.Id, ig.Username, ig.Name, ig.ProfilePictureUrl,
            InstagramEligibilityStatus.Connected,
            "Instagram professional account linked and ready.");
    }

    private async Task<InstagramEligibilityDto> CheckInstagramEligibilityForPageAsync(FacebookPageDto page)
    {
        if (string.IsNullOrEmpty(page.AccessToken))
        {
            _logger.LogWarning("IG discovery: page {PageId} ({PageName}) has no access token", page.Id, page.Name);
            return MapEligibility(page.Id, page.Name, null, true, "Page access token not available. Missing permission.");
        }

        try
        {
            // Step 1: Try with subfield expansion for full profile details
            var expandedFields = "name," +
                         "instagram_business_account{id,username,name,profile_picture_url}," +
                         "connected_instagram_account{id,username,name,profile_picture_url}";
            var igUrl = $"{_graphApiBaseUrl}/{page.Id}?fields={expandedFields}&access_token={page.AccessToken}";

            _logger.LogDebug(
                "IG discovery: querying page {PageId} ({PageName}), fields={Fields}, tokenType=page_token, graphVersion=v21.0",
                page.Id, page.Name, expandedFields);

            var response = await _httpClient.GetAsync(igUrl);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "IG discovery: API call FAILED for page {PageId}: status={Status}, body={Body}",
                    page.Id, response.StatusCode, errorBody);

                if ((int)response.StatusCode == 403 || errorBody.Contains("OAuthException"))
                {
                    return MapEligibility(page.Id, page.Name, null, true,
                        "Missing Instagram permissions. Reconnect your Meta account to grant Instagram access.");
                }

                return MapEligibility(page.Id, page.Name, null, true,
                    "Could not check Instagram status for this Page.");
            }

            var json = await response.Content.ReadAsStringAsync();
            _logger.LogDebug("IG discovery: raw Graph response for page {PageId}: {Json}", page.Id, json);

            var data = JsonSerializer.Deserialize<MetaPageInstagramResponse>(json);

            var igFromExpanded = data?.InstagramBusinessAccount ?? data?.ConnectedInstagramAccount;
            var hasIgFromExpanded = igFromExpanded != null && !string.IsNullOrEmpty(igFromExpanded.Id);

            _logger.LogDebug(
                "IG discovery: deserialized for page {PageId}: IBA={IBA}, CIA={CIA}, hasIg={HasIg}",
                page.Id,
                data?.InstagramBusinessAccount?.Id ?? "null",
                data?.ConnectedInstagramAccount?.Id ?? "null",
                hasIgFromExpanded);

            // Step 2: If expanded query didn't find an IG account, retry WITHOUT subfield
            // expansion. The Graph API may return the linked IG id at the top level even when
            // subfield expansion fails due to permission or account-type issues.
            if (!hasIgFromExpanded)
            {
                var plainFields = "name,instagram_business_account,connected_instagram_account";
                var plainUrl = $"{_graphApiBaseUrl}/{page.Id}?fields={plainFields}&access_token={page.AccessToken}";

                _logger.LogDebug(
                    "IG discovery: retrying page {PageId} without subfield expansion, fields={Fields}",
                    page.Id, plainFields);

                var plainResponse = await _httpClient.GetAsync(plainUrl);
                if (plainResponse.IsSuccessStatusCode)
                {
                    var plainJson = await plainResponse.Content.ReadAsStringAsync();
                    _logger.LogDebug("IG discovery: plain response for page {PageId}: {Json}", page.Id, plainJson);

                    var plainData = JsonSerializer.Deserialize<MetaPageInstagramResponse>(plainJson);

                    // The plain response returns {"instagram_business_account":{"id":"123"}}
                    // Merge: if plain found an ID but expanded didn't, use the plain result
                    var igFromPlain = plainData?.InstagramBusinessAccount ?? plainData?.ConnectedInstagramAccount;
                    if (igFromPlain != null && !string.IsNullOrEmpty(igFromPlain.Id))
                    {
                        _logger.LogInformation(
                            "IG discovery: plain query found IG for page {PageId}: id={IgId} (expanded query missed it)",
                            page.Id, igFromPlain.Id);
                        data = plainData;
                    }
                }
                else
                {
                    var plainError = await plainResponse.Content.ReadAsStringAsync();
                    _logger.LogWarning(
                        "IG discovery: plain query also failed for page {PageId}: status={Status}, body={Body}",
                        page.Id, plainResponse.StatusCode, plainError);
                }
            }

            return MapEligibility(page.Id, page.Name, data, false, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IG discovery: exception for page {PageId}", page.Id);
            return MapEligibility(page.Id, page.Name, null, true,
                "Could not check Instagram status for this Page.");
        }
    }

    private async Task LogTokenScopesAsync(string accessToken)
    {
        try
        {
            var resp = await _httpClient.GetAsync(
                $"{_graphApiBaseUrl}/me/permissions?access_token={accessToken}");
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                _logger.LogInformation("Instagram discovery - token scopes: {Scopes}", body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log token scopes");
        }
    }

    private async Task<List<FacebookPageDto>> FetchUserPagesAsync(string accessToken)
    {
        var pages = new List<FacebookPageDto>();
        var url = $"{_graphApiBaseUrl}/me/accounts?fields=id,name,category,picture{{url}},access_token&access_token={accessToken}";

        while (!string.IsNullOrEmpty(url))
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            _logger.LogDebug("RAW /me/accounts JSON: {Json}", json);
            var data = JsonSerializer.Deserialize<MetaPagesResponse>(json);

            if (data?.Data != null)
            {
                foreach (var page in data.Data)
                {
                    pages.Add(new FacebookPageDto(
                        page.Id,
                        page.Name,
                        page.Category,
                        page.Picture?.Data?.Url,
                        page.AccessToken
                    ));
                }
            }

            url = data?.Paging?.Next;
        }

        return pages;
    }

    private static string GenerateSecureState()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static Guid GetCurrentUserId()
    {
        // TODO: Implement proper user authentication
        // For now, using a fixed user ID
        return Guid.Parse("00000000-0000-0000-0000-000000000001");
    }

    private static MetaConnectionDto MapToDto(MetaConnection connection)
    {
        return new MetaConnectionDto(
            connection.Id.ToString(),
            connection.UserId.ToString(),
            connection.TokenExpiresAt,
            connection.ConnectedAt,
            connection.Pages.Select(p => new ConnectedPageDto(
                p.Id.ToString(),
                p.PageId,
                p.Name,
                p.Category,
                p.PictureUrl,
                p.IsConnected,
                p.DisconnectedAt
            )).ToList(),
            connection.InstagramAccounts.Select(ig => new ConnectedInstagramAccountDto(
                ig.Id.ToString(),
                ig.IgBusinessId,
                ig.Username,
                ig.Name,
                ig.ProfilePictureUrl,
                ig.PageId,
                ig.PageName,
                ig.IsConnected,
                ig.DisconnectedAt
            )).ToList(),
            connection.IsConnected,
            connection.DisconnectedAt
        );
    }
}

// JSON response models for Meta Graph API (using snake_case naming)
internal class MetaTokenResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }
}

internal class MetaPagesResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public List<MetaPageData>? Data { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("paging")]
    public MetaPaging? Paging { get; set; }
}

internal class MetaPageData
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("category")]
    public string? Category { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("picture")]
    public MetaPicture? Picture { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }
}

internal class MetaPicture
{
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public MetaPictureData? Data { get; set; }
}

internal class MetaPictureData
{
    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string? Url { get; set; }
}

internal class MetaPaging
{
    [System.Text.Json.Serialization.JsonPropertyName("next")]
    public string? Next { get; set; }
}

internal class MetaPageInstagramResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("instagram_business_account")]
    public MetaInstagramAccount? InstagramBusinessAccount { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("connected_instagram_account")]
    public MetaInstagramAccount? ConnectedInstagramAccount { get; set; }
}

internal class MetaInstagramAccount
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("username")]
    public string? Username { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("profile_picture_url")]
    public string? ProfilePictureUrl { get; set; }
}