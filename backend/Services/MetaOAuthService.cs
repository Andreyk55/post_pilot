using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Providers;
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
    private readonly IProviderConnectionService _providerConnections;
    private readonly string _graphApiBaseUrl;
    private readonly string _oAuthBaseUrl;
    private readonly int _oAuthStateExpirationMinutes;

    public MetaOAuthService(
        AppDbContext context,
        HttpClient httpClient,
        MetaOptions settings,
        ILogger<MetaOAuthService> logger,
        IPostScheduler scheduler,
        IProviderConnectionService providerConnections,
        MetaApiOptions metaApiOptions,
        PublishingOptions publishingOptions)
    {
        _context = context;
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
        _scheduler = scheduler;
        _providerConnections = providerConnections;
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
        Guid workspaceId,
        IEnumerable<Guid> removedPageIds,
        IEnumerable<Guid> removedInstagramAccountIds,
        string reasonCode,
        string userMessage)
    {
        var pageIds = removedPageIds.ToHashSet();
        var igIds = removedInstagramAccountIds.ToHashSet();
        if (pageIds.Count == 0 && igIds.Count == 0) return;

        var affected = await _context.Posts
            .Where(p => p.WorkspaceId == workspaceId)
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

    public async Task<MetaOAuthStartResponse> StartOAuthAsync(Guid workspaceId)
    {
        // Generate secure state parameter
        var state = GenerateSecureState();

        // Store state in database for validation, bound to the workspace.
        var oauthState = new MetaOAuthState
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
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

    public async Task<MetaOAuthCallbackResponse> HandleCallbackAsync(string code, string state, Guid currentWorkspaceId)
    {
        // Validate state (fail closed on missing/expired).
        var oauthState = await _context.MetaOAuthStates
            .FirstOrDefaultAsync(s => s.State == state && s.ExpiresAt > DateTime.UtcNow);

        if (oauthState == null)
        {
            throw new InvalidOperationException("Invalid or expired OAuth state");
        }

        // SESSION BINDING: the state must belong to the caller's current (membership-checked)
        // workspace before we exchange the code or touch any provider data.
        EnsureStateBelongsToWorkspace(oauthState, currentWorkspaceId);

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

        // Resolve the stable Meta identity NOW and validate permanent ownership BEFORE
        // doing anything else (fetching pages, storing temp state, showing the
        // page-selection UI). If this Meta account is permanently bound elsewhere — to
        // a different workspace, or this workspace is bound to a different account — we
        // fail immediately with 409 and never reach page discovery or selection state.
        var (metaUserId, _) = await FetchMetaUserIdentityAsync(accessToken);
        await _providerConnections.ValidateIncomingProviderAccountForWorkspaceAsync(
            oauthState.WorkspaceId, ProviderType.Meta, metaUserId);

        // Store token temporarily in state record (only after ownership passed).
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

    public async Task<MetaOAuthCompleteResponse> CompleteOAuthAsync(string code, string state, Guid userId, Guid currentWorkspaceId)
    {
        _logger.LogInformation("CompleteOAuth called with state: {State}", state);

        // Validate state (fail closed on missing/expired).
        var oauthState = await _context.MetaOAuthStates
            .FirstOrDefaultAsync(s => s.State == state && s.ExpiresAt > DateTime.UtcNow);

        if (oauthState == null)
        {
            _logger.LogWarning("State validation failed. State not found or expired: {State}", state);
            throw new InvalidOperationException("Invalid or expired OAuth state");
        }

        // SESSION BINDING: reject a state minted for a different workspace than the caller's
        // current (membership-checked) workspace before exchanging the code.
        EnsureStateBelongsToWorkspace(oauthState, currentWorkspaceId);

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
            var safeBody = RedactSensitive(await tokenResponse.Content.ReadAsStringAsync());
            _logger.LogError("Token exchange failed. Status: {Status}, Body: {Body}", tokenResponse.StatusCode, safeBody);
            throw new InvalidOperationException($"Failed to exchange code for token: {safeBody}");
        }
        var tokenJson = await tokenResponse.Content.ReadAsStringAsync();
        var tokenData = JsonSerializer.Deserialize<MetaTokenResponse>(tokenJson);
        if (tokenData?.AccessToken == null)
        {
            _logger.LogError("Token response did not contain access_token. Response: {Json}", RedactSensitive(tokenJson));
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
            var errorBody = RedactSensitive(await longLivedResponse.Content.ReadAsStringAsync());
            _logger.LogWarning("Long-lived token exchange failed (will use short-lived token). Status: {Status}, Body: {Body}", longLivedResponse.StatusCode, errorBody);
        }

        var accessToken = longLivedData?.AccessToken ?? tokenData.AccessToken;
        var expiresIn = longLivedData?.ExpiresIn ?? tokenData.ExpiresIn ?? 3600;

        var workspaceId = oauthState.WorkspaceId;
        _logger.LogInformation("Saving connection for workspace {WorkspaceId} (user {UserId})", workspaceId, userId);

        // Resolve the stable Meta identity FIRST. Used both for the permanent-ownership
        // guards and the "reconnect same account ⇒ resurface history" rule.
        var (metaUserId, metaUserName) = await FetchMetaUserIdentityAsync(accessToken);

        // PERMANENT OWNERSHIP: validate the resolved identity the instant we know it and
        // before persisting anything. Rejects (409) if this workspace is bound to a
        // different account, or if this account belongs to another workspace (connected
        // or disconnected). Throws ProviderAccountMismatchException / ProviderOwnedByAnotherWorkspaceException.
        await _providerConnections.ValidateIncomingProviderAccountForWorkspaceAsync(
            workspaceId, ProviderType.Meta, metaUserId);

        // Same-workspace reconnect rule: if THIS workspace already owns an active Meta
        // connection for the SAME account (e.g. recovering from ReauthRequired, or a
        // plain re-grant), allow it — we update the existing row in place rather than
        // rejecting as a "second account". Only a connect attempt for a DIFFERENT
        // account in a workspace that already owns one is rejected.
        var existingActive = await _providerConnections.GetActiveConnectionAsync(workspaceId, ProviderType.Meta);
        var sameAccountReconnect =
            existingActive != null
            && !string.IsNullOrEmpty(metaUserId)
            && existingActive.ProviderAccountId == metaUserId;

        if (!sameAccountReconnect)
        {
            // Reject second-account connect (product rule: at most one active Meta per workspace).
            await _providerConnections.EnsureCanConnectAsync(workspaceId, ProviderType.Meta);
        }

        var connection = await ResolveOrCreateMetaConnectionAsync(
            workspaceId,
            userId,
            metaUserId,
            metaUserName,
            accessToken,
            expiresIn);

        // Clean up OAuth state
        _context.MetaOAuthStates.Remove(oauthState);

        await _context.SaveChangesAsync();

        _logger.LogInformation("Meta connection saved successfully. Connection ID: {ConnectionId}", connection.Id);

        return new MetaOAuthCompleteResponse(MapToDto(connection));
    }

    /// <summary>
    /// Implements the connect-side of the provider lifecycle for Meta:
    ///
    ///   1. If a previously-disconnected connection exists for the SAME provider
    ///      account (Provider=Meta, matching ProviderAccountId), reactivate it so
    ///      its historical Pages/IGs/Posts come back into view.
    ///   2. Otherwise create a fresh connection row.
    ///
    /// Assumes the caller has already enforced "no active connection exists" via
    /// <see cref="IProviderConnectionService.EnsureCanConnectAsync"/>.
    /// </summary>
    private async Task<MetaConnection> ResolveOrCreateMetaConnectionAsync(
        Guid workspaceId,
        Guid userId,
        string? providerAccountId,
        string? providerAccountName,
        string accessToken,
        int expiresIn)
    {
        var now = DateTime.UtcNow;

        // Same-workspace reconnect of an account that is STILL owned (IsConnected=true)
        // — typically recovering from ReauthRequired. Reuse that exact row so we don't
        // create a duplicate active connection (which the unique index would reject too).
        if (!string.IsNullOrEmpty(providerAccountId))
        {
            var stillOwned = await _context.MetaConnections
                .Include(c => c.Pages)
                .Include(c => c.InstagramAccounts)
                .FirstOrDefaultAsync(c =>
                    c.WorkspaceId == workspaceId
                    && c.Provider == ProviderType.Meta
                    && c.ProviderAccountId == providerAccountId
                    && c.IsConnected);
            if (stillOwned != null)
            {
                stillOwned.AccessToken = accessToken;
                stillOwned.TokenExpiresAt = now.AddSeconds(expiresIn);
                stillOwned.UpdatedAt = now;
                stillOwned.Status = ConnectionStatus.Active; // clears ReauthRequired
                stillOwned.UserId = userId;
                if (!string.IsNullOrEmpty(providerAccountName))
                {
                    stillOwned.ProviderAccountName = providerAccountName;
                }
                // Clear the reauth flag mirrored onto the owned asset rows too, so the
                // publish gate (which checks asset Status) unblocks. This is the identity-
                // level CompleteOAuth recovery path that does NOT run ReconcileSelectedAssets.
                foreach (var p in stillOwned.Pages.Where(p => p.IsConnected))
                {
                    p.Status = ConnectionStatus.Active;
                }
                foreach (var ig in stillOwned.InstagramAccounts.Where(i => i.IsConnected))
                {
                    ig.Status = ConnectionStatus.Active;
                }
                _logger.LogInformation(
                    "Refreshing already-owned Meta connection {ConnectionId} for provider account {ProviderAccountId} (reauth recovery)",
                    stillOwned.Id, providerAccountId);
                return stillOwned;
            }
        }

        // Find a previously-disconnected row to REACTIVATE rather than orphan.
        //
        // Reactivating (vs. inserting a fresh row) is what keeps historical Pages,
        // IG accounts and their Posts — including Failed posts — attached to a row
        // the visibility query treats as active. Insert a duplicate instead and the
        // old page (with its Failed post) stays pinned to a permanently-disconnected
        // row and disappears from My Posts forever. See ResolveDisconnectedMetaForReconnect.
        var existing = await ResolveDisconnectedMetaForReconnectAsync(workspaceId, providerAccountId);

        if (existing != null)
        {
            existing.AccessToken = accessToken;
            existing.TokenExpiresAt = now.AddSeconds(expiresIn);
            existing.UpdatedAt = now;
            existing.IsConnected = true;
            // Reconnecting with a fresh token clears any prior reauth flag.
            existing.Status = ConnectionStatus.Active;
            existing.DisconnectedAt = null;
            existing.ConnectedAt = now;
            existing.UserId = userId; // Latest user who initiated the reconnect.
            // Backfill the stable identity if we resolved one this time but the
            // historical row never had it (Graph /me had failed on an earlier connect,
            // or the row predates the ProviderAccountId column). Without this, the next
            // reconnect would fail to match by id again.
            if (!string.IsNullOrEmpty(providerAccountId))
            {
                existing.ProviderAccountId = providerAccountId;
            }
            if (!string.IsNullOrEmpty(providerAccountName))
            {
                existing.ProviderAccountName = providerAccountName;
            }

            _logger.LogInformation(
                "Reactivating Meta connection {ConnectionId} for provider account {ProviderAccountId}",
                existing.Id, providerAccountId);
            return existing;
        }

        var fresh = new MetaConnection
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Provider = ProviderType.Meta,
            ProviderAccountId = providerAccountId,
            ProviderAccountName = providerAccountName,
            AccessToken = accessToken,
            TokenExpiresAt = now.AddSeconds(expiresIn),
            ConnectedAt = now,
            UpdatedAt = now,
            IsConnected = true,
            DisconnectedAt = null,
        };
        _context.MetaConnections.Add(fresh);
        _logger.LogInformation(
            "Creating new Meta connection {ConnectionId} for provider account {ProviderAccountId}",
            fresh.Id, providerAccountId);
        return fresh;
    }

    /// <summary>
    /// Finds the disconnected <see cref="MetaConnection"/> row that a reconnect should
    /// reactivate, so its historical Pages/IGs/Posts come back into view.
    ///
    /// Matching strategy (callers have already enforced "no ACTIVE connection exists"
    /// via <see cref="IProviderConnectionService.EnsureCanConnectAsync"/>):
    ///
    ///   1. If we resolved a stable <paramref name="providerAccountId"/>, prefer the
    ///      disconnected row with that exact id — that is unambiguously "the same account".
    ///   2. If no id-match exists (or we couldn't resolve an id at all, e.g. Graph /me
    ///      failed), fall back to the most-recently-disconnected row whose stored
    ///      ProviderAccountId is null/empty. A null stored id means "we never learned
    ///      which account this was", so adopting it for the current reconnect is safe —
    ///      and it's exactly the row a token-invalid → disconnect → reconnect cycle
    ///      leaves behind when identity resolution was flaky. Reusing it reattaches the
    ///      old page + its Failed post instead of orphaning them on a dead row.
    ///
    /// We deliberately DO NOT fall back to a disconnected row that carries a DIFFERENT
    /// non-null ProviderAccountId — that would merge two distinct accounts' history.
    /// </summary>
    private async Task<MetaConnection?> ResolveDisconnectedMetaForReconnectAsync(
        Guid workspaceId,
        string? providerAccountId)
    {
        // 1. Exact stable-identity match.
        if (!string.IsNullOrEmpty(providerAccountId))
        {
            var byId = await _context.MetaConnections
                .Include(c => c.Pages)
                .Include(c => c.InstagramAccounts)
                .FirstOrDefaultAsync(c =>
                    c.WorkspaceId == workspaceId
                    && c.Provider == ProviderType.Meta
                    && c.ProviderAccountId == providerAccountId
                    && !c.IsConnected);
            if (byId != null)
            {
                return byId;
            }
        }

        // 2. Identity-unknown fallback: a disconnected row that never recorded which
        // account it was. Pick the most recent so repeated cycles converge on one row.
        //
        // Guard the permanent-binding rule here too: if this workspace already carries
        // a DIFFERENT non-null bound identity for Meta, do NOT adopt an identity-unknown
        // row (that would silently rebind the workspace to a new account). The upstream
        // EnsureAccountMatchesWorkspaceBindingAsync already rejects mismatches when we
        // resolved an id; this covers the id-unresolved path defensively.
        var hasOtherBoundIdentity =
            !string.IsNullOrEmpty(providerAccountId)
            && await _context.MetaConnections.AnyAsync(c =>
                c.WorkspaceId == workspaceId
                && c.Provider == ProviderType.Meta
                && c.ProviderAccountId != null
                && c.ProviderAccountId != ""
                && c.ProviderAccountId != providerAccountId);
        if (hasOtherBoundIdentity)
        {
            return null;
        }

        return await _context.MetaConnections
            .Include(c => c.Pages)
            .Include(c => c.InstagramAccounts)
            .Where(c =>
                c.WorkspaceId == workspaceId
                && c.Provider == ProviderType.Meta
                && !c.IsConnected
                && (c.ProviderAccountId == null || c.ProviderAccountId == ""))
            .OrderByDescending(c => c.DisconnectedAt)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Look up the stable Meta user identity (FB user id + name) for the supplied
    /// token. This is the <c>ProviderAccountId</c> we persist on the MetaConnection
    /// so that disconnect/reconnect of the SAME account can resurface history.
    ///
    /// Returns <c>(null, null)</c> if Graph API fails — the OAuth flow continues
    /// (the column is nullable) but logs a warning so we can investigate.
    /// </summary>
    private async Task<(string? Id, string? Name)> FetchMetaUserIdentityAsync(string accessToken)
    {
        try
        {
            var resp = await GraphGetAsync($"{_graphApiBaseUrl}/me?fields=id,name", accessToken);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                // Log status only — the body may echo request context; never log it raw.
                _logger.LogWarning(
                    "Meta /me failed while resolving provider identity. Status: {Status}",
                    resp.StatusCode);
                return (null, null);
            }

            var data = JsonSerializer.Deserialize<MetaMeResponse>(body);
            return (data?.Id, data?.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve Meta provider identity from /me");
            return (null, null);
        }
    }

    public async Task<MetaDiscoverInstagramResponse> DiscoverInstagramAccountsAsync(string tempToken, List<string> pageIds, Guid workspaceId)
    {
        string accessToken;

        // Check if tempToken is a GUID (OAuth state ID) or empty (manage mode)
        if (Guid.TryParse(tempToken, out var stateId))
        {
            var oauthState = await _context.MetaOAuthStates.FindAsync(stateId);
            // Fail closed on missing/consumed/expired temp token.
            if (oauthState?.TempAccessToken == null || oauthState.ExpiresAt <= DateTime.UtcNow)
            {
                throw new InvalidOperationException("Invalid or expired temp token");
            }
            // SESSION BINDING: the state must belong to the caller's current (membership-checked)
            // workspace — never trust the state's own WorkspaceId for a cross-workspace caller.
            EnsureStateBelongsToWorkspace(oauthState, workspaceId);
            accessToken = oauthState.TempAccessToken;
        }
        else
        {
            // Manage mode - use the workspace's stored connection token.
            var connection = await _context.MetaConnections
                .FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.IsConnected);

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
                var igUrl = $"{_graphApiBaseUrl}/{page.Id}?fields={fields}";
                var response = await GraphGetAsync(igUrl, page.AccessToken);

                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<MetaPageInstagramResponse>(json);

                var ig = data?.InstagramBusinessAccount ?? data?.ConnectedInstagramAccount;

                // Step 2: If expanded query found nothing, try without subfield expansion
                if (ig == null || string.IsNullOrEmpty(ig.Id))
                {
                    var plainFields = "name,instagram_business_account,connected_instagram_account";
                    var plainUrl = $"{_graphApiBaseUrl}/{page.Id}?fields={plainFields}";
                    var plainResponse = await GraphGetAsync(plainUrl, page.AccessToken);

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

    public async Task<MetaSaveConnectionResponse> SaveConnectionAsync(string tempToken, List<string> selectedPageIds, List<string> selectedInstagramIds, Guid userId, Guid currentWorkspaceId)
    {
        if (!Guid.TryParse(tempToken, out var stateId))
        {
            throw new InvalidOperationException("Invalid temp token");
        }

        var oauthState = await _context.MetaOAuthStates.FindAsync(stateId);
        // Fail closed on missing/consumed/expired temp token.
        if (oauthState?.TempAccessToken == null || oauthState.TokenExpiresAt == null
            || oauthState.ExpiresAt <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Invalid or expired temp token");
        }

        // SESSION BINDING: the state must belong to the caller's current (membership-checked)
        // workspace. This also guarantees the connection lands in that same workspace below.
        EnsureStateBelongsToWorkspace(oauthState, currentWorkspaceId);

        var workspaceId = oauthState.WorkspaceId;
        var now = DateTime.UtcNow;

        // Resolve the stable Meta identity so we can resurface history if the SAME
        // account is reconnecting after a disconnect.
        var (metaUserId, metaUserName) = await FetchMetaUserIdentityAsync(oauthState.TempAccessToken);

        // PERMANENT OWNERSHIP: validate the resolved identity immediately — before
        // fetching pages, reconciling assets, or creating/updating any connection.
        // Rejects (409) if this workspace is bound to a different account or the account
        // belongs to another workspace (connected or disconnected). The callback path
        // (HandleCallbackAsync) already enforces this, but SaveConnection is also a
        // direct write path, so it must validate independently.
        await _providerConnections.ValidateIncomingProviderAccountForWorkspaceAsync(
            workspaceId, ProviderType.Meta, metaUserId);

        // Same-workspace reconnect (incl. reauth recovery) is allowed; only a connect
        // for a DIFFERENT account in a workspace that already owns one is rejected.
        var existingActive = await _providerConnections.GetActiveConnectionAsync(workspaceId, ProviderType.Meta);
        var sameAccountReconnect =
            existingActive != null
            && !string.IsNullOrEmpty(metaUserId)
            && existingActive.ProviderAccountId == metaUserId;

        if (!sameAccountReconnect)
        {
            // Enforce the product rule before touching state: at most one active Meta
            // connection per workspace. Throws ProviderAlreadyConnectedException → 409.
            await _providerConnections.EnsureCanConnectAsync(workspaceId, ProviderType.Meta);
        }

        // Discover Instagram accounts for selected pages. We persist EVERY IG linked to a
        // selected page (auto-promotion), so the ownership guard below must cover those
        // discovered IG ids too — not only the ids the user explicitly checked. selectedInstagramIds
        // is retained for backward compatibility with the caller's contract but no longer gates
        // which linked IGs become connected.
        var igResponse = await DiscoverInstagramAccountsAsync(tempToken, selectedPageIds, workspaceId);
        var discoveredIgIds = igResponse.InstagramAccounts
            .Where(ig => !string.IsNullOrEmpty(ig.Id))
            .Select(ig => ig.Id);

        // Asset-level cross-workspace ownership guard against the SELECTED pages + every
        // linked IG that will be auto-promoted (the account-level guard already ran above).
        // Throws ProviderOwnedByAnotherWorkspaceException → 409.
        await _providerConnections.EnsureNotOwnedByAnotherWorkspaceAsync(
            workspaceId,
            ProviderType.Meta,
            metaUserId,
            selectedPageIds.Concat(discoveredIgIds));

        var expiresIn = (int)Math.Max(0, (oauthState.TokenExpiresAt.Value - now).TotalSeconds);
        var connection = await ResolveOrCreateMetaConnectionAsync(
            workspaceId,
            userId,
            metaUserId,
            metaUserName,
            oauthState.TempAccessToken,
            expiresIn);

        // Re-fetch with navigation collections loaded so the asset reconcile below
        // sees existing rows that ResolveOrCreate may not have included on a fresh row.
        if (connection.Pages.Count == 0 && connection.InstagramAccounts.Count == 0)
        {
            await _context.Entry(connection).Collection(c => c.Pages).LoadAsync();
            await _context.Entry(connection).Collection(c => c.InstagramAccounts).LoadAsync();
        }

        // Fetch pages with tokens
        var allPages = await FetchUserPagesAsync(oauthState.TempAccessToken);
        var selectedPages = allPages.Where(p => selectedPageIds.Contains(p.Id)).ToList();

        if (!selectedPages.Any())
        {
            throw new InvalidOperationException("At least one page must be selected");
        }

        await ReconcileSelectedAssetsAsync(connection, selectedPages, igResponse.InstagramAccounts, now);

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
    ///
    /// PRODUCT RULE — IG follows its parent Page: any Instagram professional account that Meta
    /// reports as linked to a SELECTED (connected) Facebook Page is auto-promoted to a connected
    /// publishable IG asset, even when its id was not in the user's explicit IG selection. This
    /// is what makes "connect a Page that has a linked IG" => "that IG is a connected Instagram
    /// account" hold everywhere (Assets, SchedulePost, post validation, publisher). It also acts
    /// as the idempotent repair path for production rows that predate this rule: every connect /
    /// page-update re-discovers and re-promotes. <paramref name="discoveredIgAccounts"/> carries
    /// the full set of IGs discovered for the selected pages (already filtered to selected pages
    /// by the caller's discovery call); the union of these with any historically-connected IG on
    /// a still-selected page is the connected set.
    /// </summary>
    private async Task ReconcileSelectedAssetsAsync(
        MetaConnection connection,
        List<FacebookPageDto> selectedPages,
        List<InstagramAccountDto> discoveredIgAccounts,
        DateTime now)
    {
        var selectedFbPageIds = selectedPages.Select(p => p.Id).ToHashSet();

        // Auto-promote: an IG linked to a connected page is a connected publishable asset.
        // Discovery is already scoped to the selected pages, but guard PageId membership
        // defensively so a stray discovery row can never connect an IG for an unselected page.
        var selectedIgAccounts = discoveredIgAccounts
            .Where(ig => !string.IsNullOrEmpty(ig.Id) && selectedFbPageIds.Contains(ig.PageId))
            .GroupBy(ig => ig.Id)
            .Select(g => g.First())
            .ToList();
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
                // Refreshing with a new token clears any reauth flag on this asset.
                existing.Status = ConnectionStatus.Active;
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
                WorkspaceId = connection.WorkspaceId,
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
                // Refreshing with a new token clears any reauth flag on this asset.
                existing.Status = ConnectionStatus.Active;
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
                WorkspaceId = connection.WorkspaceId,
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
                connection.WorkspaceId,
                pagesToDisconnect.Select(p => p.Id),
                igsToDisconnect.Select(i => i.Id),
                ReasonAssetUnlinked,
                MessageAssetUnlinked);
        }
    }

    public async Task<MetaConnectionResponse> GetConnectionAsync(Guid workspaceId)
    {
        // Only return the currently-connected MetaConnection in this workspace.
        // Disconnected rows are kept as historical breadcrumbs for posts but are
        // never surfaced to the UI as "connected".
        var connection = await _context.MetaConnections
            .Include(c => c.Pages.Where(p => p.IsConnected))
            .Include(c => c.InstagramAccounts.Where(i => i.IsConnected))
            .FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.IsConnected);

        if (connection == null)
        {
            return new MetaConnectionResponse(null, false);
        }

        return new MetaConnectionResponse(MapToDto(connection), true);
    }

    public async Task<MetaAvailablePagesResponse> GetAvailablePagesAsync(Guid workspaceId)
    {
        var connection = await _context.MetaConnections
            .FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.IsConnected);
        if (connection == null)
        {
            throw new InvalidOperationException("No Meta connection found");
        }

        var pages = await FetchUserPagesAsync(connection.AccessToken);
        return new MetaAvailablePagesResponse(pages.Select(p => new FacebookPageDto(
            p.Id, p.Name, p.Category, p.PictureUrl, null // Don't expose access tokens
        )).ToList());
    }

    public async Task<MetaSaveConnectionResponse> UpdateConnectionAsync(Guid workspaceId, List<string> selectedPageIds, List<string> selectedInstagramIds)
    {
        var connection = await _context.MetaConnections
            .Include(c => c.Pages)
            .Include(c => c.InstagramAccounts)
            .FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.IsConnected);

        if (connection == null)
        {
            throw new InvalidOperationException("No Meta connection found");
        }

        // Fetch all available pages
        var allPages = await FetchUserPagesAsync(connection.AccessToken);
        var selectedPages = allPages.Where(p => selectedPageIds.Contains(p.Id)).ToList();

        // Discover Instagram accounts (only for selected pages). Zero is allowed —
        // the user can soft-disconnect every page while keeping the Meta identity.
        // Every IG linked to a still-selected page is auto-promoted to connected, so we
        // pass the FULL discovered list (selectedInstagramIds no longer gates promotion).
        // This is also the repair path: removing a page drops its linked IG (via reconcile
        // soft-disconnect), and re-connecting/refreshing a page re-promotes its linked IG.
        var igResponse = selectedPageIds.Any()
            ? await DiscoverInstagramAccountsAsync("", selectedPageIds, workspaceId)
            : new MetaDiscoverInstagramResponse(new List<InstagramAccountDto>());

        var now = DateTime.UtcNow;
        connection.UpdatedAt = now;

        await ReconcileSelectedAssetsAsync(connection, selectedPages, igResponse.InstagramAccounts, now);

        await _context.SaveChangesAsync();

        return new MetaSaveConnectionResponse(MapToDto(connection));
    }

    /// <summary>
    /// Idempotent repair: re-discover Instagram accounts for the workspace's currently
    /// connected Facebook Pages and promote any linked IG professional account to a
    /// connected publishable asset — WITHOUT changing which pages are connected.
    ///
    /// This fixes production rows created before IG auto-promotion existed: a connected
    /// Page whose linked IG never became a <see cref="ConnectedInstagramAccount"/> (so it
    /// showed "Linked" in discovery but was missing from the connected list and blocked the
    /// composer). Safe to call repeatedly; a no-op when every linked IG is already connected.
    /// Provider/workspace scoped — never touches other workspaces' assets, ownership rules,
    /// or the disconnect/reconnect lifecycle.
    /// </summary>
    public async Task<MetaSaveConnectionResponse> RefreshAssetsAsync(Guid workspaceId)
    {
        var connection = await _context.MetaConnections
            .Include(c => c.Pages)
            .Include(c => c.InstagramAccounts)
            .FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.IsConnected);

        if (connection == null)
        {
            throw new InvalidOperationException("No Meta connection found");
        }

        // Keep exactly the pages that are currently connected — this is a repair, not a
        // re-selection. Re-fetch live page metadata (incl. tokens) for them.
        var connectedPageIds = connection.Pages
            .Where(p => p.IsConnected)
            .Select(p => p.PageId)
            .ToList();

        if (connectedPageIds.Count == 0)
        {
            // Nothing connected to derive IGs from; return current state untouched.
            return new MetaSaveConnectionResponse(MapToDto(connection));
        }

        var allPages = await FetchUserPagesAsync(connection.AccessToken);
        var selectedPages = allPages.Where(p => connectedPageIds.Contains(p.Id)).ToList();

        var igResponse = await DiscoverInstagramAccountsAsync("", connectedPageIds, workspaceId);

        var now = DateTime.UtcNow;
        connection.UpdatedAt = now;

        await ReconcileSelectedAssetsAsync(connection, selectedPages, igResponse.InstagramAccounts, now);

        await _context.SaveChangesAsync();

        return new MetaSaveConnectionResponse(MapToDto(connection));
    }

    public async Task DisconnectAsync(Guid workspaceId)
    {
        // Snapshot the token BEFORE the generic disconnect flips the row off,
        // so the best-effort revoke call still has something to send.
        var accessTokenToRevoke = await _context.MetaConnections
            .Where(c => c.WorkspaceId == workspaceId
                     && c.Provider == ProviderType.Meta
                     && c.IsConnected)
            .Select(c => c.AccessToken)
            .FirstOrDefaultAsync();

        // Generic lifecycle: cancels non-executed posts, soft-disconnects assets,
        // and flips the connection row to IsConnected=false. Idempotent on
        // already-disconnected workspaces.
        await _providerConnections.DisconnectAsync(workspaceId, ProviderType.Meta);

        // Meta-specific: best-effort revoke. We never block disconnect on this —
        // even if Meta is down, the workspace is fully disconnected locally.
        if (!string.IsNullOrEmpty(accessTokenToRevoke))
        {
            try
            {
                await GraphDeleteAsync($"{_graphApiBaseUrl}/me/permissions", accessTokenToRevoke);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to revoke Meta token");
            }
        }
    }

    public async Task<InstagramDiscoveryResponse> DiscoverInstagramEligibilityAsync(Guid workspaceId)
    {
        var connection = await _context.MetaConnections
            .Include(c => c.Pages)
            .Include(c => c.InstagramAccounts)
            .FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.IsConnected);

        if (connection == null)
            throw new InvalidOperationException("No Meta connection found");

        // Log token scopes for diagnostics
        await LogTokenScopesAsync(connection.AccessToken);

        var allPages = await FetchUserPagesAsync(connection.AccessToken);
        _logger.LogInformation("Instagram discovery: found {PageCount} pages in workspace {WorkspaceId}", allPages.Count, workspaceId);

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

    public async Task<object> DebugInstagramDiscoveryAsync(Guid workspaceId)
    {
        var connection = await _context.MetaConnections
            .Include(c => c.Pages)
            .FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.IsConnected);

        if (connection == null)
            return new { error = "No Meta connection found" };

        // 1) Granted permission NAMES only. The raw /me/permissions body is never returned;
        //    tokens never appear in this endpoint's output.
        string grantedPermissions;
        try
        {
            var permResp = await GraphGetAsync($"{_graphApiBaseUrl}/me/permissions", connection.AccessToken);
            grantedPermissions = SummarizePermissions(await permResp.Content.ReadAsStringAsync());
        }
        catch (Exception)
        {
            grantedPermissions = "(error)";
        }

        // 2) Requested OAuth scopes
        var requestedScopes = "pages_show_list,pages_read_engagement,pages_manage_posts,business_management,public_profile";

        // 3) Fetch pages (page ids/names are already shown in-app; page tokens are NOT surfaced).
        var allPages = await FetchUserPagesAsync(connection.AccessToken);

        // 4) For each page, resolve IG linkage. We return ONLY the computed linkage result and
        //    HTTP status codes — never raw Graph JSON (which can carry tokens/PII) or token prefixes.
        var pageResults = new List<object>();
        foreach (var page in allPages)
        {
            var expandedFields = "name,instagram_business_account{id,username,name,profile_picture_url}," +
                         "connected_instagram_account{id,username,name,profile_picture_url}";
            var plainFields = "name,instagram_business_account,connected_instagram_account";

            int expandedStatus = 0;
            MetaPageInstagramResponse? expandedData = null;
            try
            {
                var response = await GraphGetAsync($"{_graphApiBaseUrl}/{page.Id}?fields={expandedFields}", page.AccessToken);
                expandedStatus = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                    expandedData = JsonSerializer.Deserialize<MetaPageInstagramResponse>(await response.Content.ReadAsStringAsync());
            }
            catch (Exception) { /* status stays 0; linkage falls back to plain query */ }

            int plainStatus = 0;
            MetaPageInstagramResponse? plainData = null;
            try
            {
                var resp2 = await GraphGetAsync($"{_graphApiBaseUrl}/{page.Id}?fields={plainFields}", page.AccessToken);
                plainStatus = (int)resp2.StatusCode;
                if (resp2.IsSuccessStatusCode)
                    plainData = JsonSerializer.Deserialize<MetaPageInstagramResponse>(await resp2.Content.ReadAsStringAsync());
            }
            catch (Exception) { /* status stays 0 */ }

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
                expandedQueryStatus = expandedStatus,
                plainQueryStatus = plainStatus,
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
            grantedPermissions,
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
            var igUrl = $"{_graphApiBaseUrl}/{page.Id}?fields={expandedFields}";

            _logger.LogDebug(
                "IG discovery: querying page {PageId} ({PageName}), fields={Fields}, tokenType=page_token, graphVersion=v21.0",
                page.Id, page.Name, expandedFields);

            var response = await GraphGetAsync(igUrl, page.AccessToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "IG discovery: API call FAILED for page {PageId}: status={Status}, body={Body}",
                    page.Id, response.StatusCode, RedactSensitive(errorBody));

                if ((int)response.StatusCode == 403 || errorBody.Contains("OAuthException"))
                {
                    return MapEligibility(page.Id, page.Name, null, true,
                        "Missing Instagram permissions. Reconnect your Meta account to grant Instagram access.");
                }

                return MapEligibility(page.Id, page.Name, null, true,
                    "Could not check Instagram status for this Page.");
            }

            // Do NOT log the raw Graph body; only the deserialized (non-sensitive) IG ids below.
            var json = await response.Content.ReadAsStringAsync();

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
                var plainUrl = $"{_graphApiBaseUrl}/{page.Id}?fields={plainFields}";

                _logger.LogDebug(
                    "IG discovery: retrying page {PageId} without subfield expansion, fields={Fields}",
                    page.Id, plainFields);

                var plainResponse = await GraphGetAsync(plainUrl, page.AccessToken);
                if (plainResponse.IsSuccessStatusCode)
                {
                    var plainJson = await plainResponse.Content.ReadAsStringAsync();

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
                        page.Id, plainResponse.StatusCode, RedactSensitive(plainError));
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
            var resp = await GraphGetAsync($"{_graphApiBaseUrl}/me/permissions", accessToken);
            if (resp.IsSuccessStatusCode)
            {
                // Log permission NAMES only (the raw body carries no tokens, but we summarize anyway).
                var summary = SummarizePermissions(await resp.Content.ReadAsStringAsync());
                _logger.LogInformation("Instagram discovery - granted scopes: {Scopes}", summary);
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
        // access_token is requested as a FIELD (pages carry their own tokens) but the AUTH
        // token travels in the Authorization header, not the URL — see GraphGetAsync.
        var url = $"{_graphApiBaseUrl}/me/accounts?fields=id,name,category,picture{{url}},access_token";

        while (!string.IsNullOrEmpty(url))
        {
            var response = await GraphGetAsync(url, accessToken);
            response.EnsureSuccessStatusCode();

            // Do NOT log the raw /me/accounts body: it contains per-page access tokens.
            var json = await response.Content.ReadAsStringAsync();
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

        // Safe summary only: count + page ids/names (both are already displayed in-app). No tokens.
        _logger.LogInformation(
            "Fetched {Count} Meta page(s): {PageIds}",
            pages.Count,
            string.Join(", ", pages.Select(p => $"{p.Id}:{p.Name}")));

        return pages;
    }

    // ── Graph HTTP helpers ────────────────────────────────────────────────────
    // Send the access token via the Authorization: Bearer header instead of an
    // access_token query parameter, so tokens never appear in request URLs (and thus
    // never leak into any HTTP/proxy access logs). Any access_token already present in the
    // URL's query is stripped first; a bare `access_token` requested as a FIELD is preserved.

    private Task<HttpResponseMessage> GraphGetAsync(string url, string? accessToken, CancellationToken ct = default)
        => SendGraphAsync(HttpMethod.Get, url, accessToken, ct);

    private Task<HttpResponseMessage> GraphDeleteAsync(string url, string? accessToken, CancellationToken ct = default)
        => SendGraphAsync(HttpMethod.Delete, url, accessToken, ct);

    private Task<HttpResponseMessage> SendGraphAsync(HttpMethod method, string url, string? accessToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, StripAccessTokenFromQuery(url));
        if (!string.IsNullOrEmpty(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return _httpClient.SendAsync(request, ct);
    }

    // Removes an `access_token=...` AUTH parameter from the query while preserving a bare
    // `access_token` field request (e.g. ...fields=...,access_token). Only matches when a '='
    // follows, which never happens for the field form.
    private static readonly Regex AccessTokenQueryRegex =
        new(@"([?&])access_token=[^&]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string StripAccessTokenFromQuery(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var stripped = AccessTokenQueryRegex.Replace(url, "$1");
        stripped = stripped.Replace("?&", "?").Replace("&&", "&");
        return stripped.TrimEnd('?', '&');
    }

    // Redacts secret values from a JSON/query-like string before it is ever logged. Matches
    // access_token, token, user/page token, refresh_token, authorization, and client_secret in
    // "key":"value" (JSON), key=value (query), and "Authorization: Bearer <token>" (header) forms.
    private static readonly Regex SensitiveValueRegex =
        new(@"(""?(?:access_token|refresh_token|client_secret|authorization|user_?token|page_?token|token)""?\s*[:=]\s*(?:Bearer\s+)?""?)([^""&,}\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string RedactSensitive(string? body)
    {
        if (string.IsNullOrEmpty(body)) return body ?? string.Empty;
        return SensitiveValueRegex.Replace(body, "$1[REDACTED]");
    }

    // Parses a Graph /me/permissions payload into a safe "name=status" summary (no tokens are
    // present in permissions responses, but we still avoid logging the raw body verbatim).
    private static string SummarizePermissions(string? permissionsJson)
    {
        if (string.IsNullOrWhiteSpace(permissionsJson)) return "(none)";
        try
        {
            using var doc = JsonDocument.Parse(permissionsJson);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return "(none)";
            var parts = new List<string>();
            foreach (var perm in data.EnumerateArray())
            {
                var name = perm.TryGetProperty("permission", out var p) ? p.GetString() : null;
                var status = perm.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (!string.IsNullOrEmpty(name))
                    parts.Add(status is null ? name : $"{name}={status}");
            }
            return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
        }
        catch (JsonException)
        {
            return "(unparseable)";
        }
    }

    private static string GenerateSecureState()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    /// <summary>
    /// Session-binding guard for every state/temp-token consumer. <paramref name="currentWorkspaceId"/>
    /// is resolved by the controller via <see cref="Auth.ICurrentWorkspaceProvider"/>, which re-checks
    /// the caller's membership of their currently-selected workspace. Requiring the state's workspace
    /// to equal it therefore proves BOTH that the caller is a member of the state's workspace AND that
    /// it is their current selection — so a state minted for workspace B can never be consumed from a
    /// workspace-A context (even if B's high-entropy state value leaked). Throws
    /// <see cref="OAuthStateAccessDeniedException"/> (→ 403) on mismatch. Does not weaken the separate
    /// permanent provider-ownership guards, which still run afterwards.
    /// </summary>
    private void EnsureStateBelongsToWorkspace(MetaOAuthState oauthState, Guid currentWorkspaceId)
    {
        if (oauthState.WorkspaceId != currentWorkspaceId)
        {
            _logger.LogWarning(
                "OAuth state workspace binding rejected: state belongs to {StateWorkspaceId} but caller's current workspace is {CurrentWorkspaceId}.",
                oauthState.WorkspaceId, currentWorkspaceId);
            throw new OAuthStateAccessDeniedException(oauthState.WorkspaceId, currentWorkspaceId);
        }
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
            connection.DisconnectedAt,
            connection.ProviderAccountId,
            connection.ProviderAccountName,
            connection.Status.ToString()
        );
    }
}

// JSON response models for Meta Graph API (using snake_case naming)
internal class MetaMeResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string? Id { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }
}

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