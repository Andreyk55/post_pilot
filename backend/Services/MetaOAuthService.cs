using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;

namespace PostPilot.Api.Services;

public class MetaOAuthService : IMetaOAuthService
{
    private readonly AppDbContext _context;
    private readonly HttpClient _httpClient;
    private readonly MetaOAuthSettings _settings;
    private readonly ILogger<MetaOAuthService> _logger;

    private const string GraphApiBaseUrl = "https://graph.facebook.com/v21.0";
    private const string OAuthBaseUrl = "https://www.facebook.com/v21.0/dialog/oauth";

    public MetaOAuthService(
        AppDbContext context,
        HttpClient httpClient,
        MetaOAuthSettings settings,
        ILogger<MetaOAuthService> logger)
    {
        _context = context;
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
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
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };

        _context.MetaOAuthStates.Add(oauthState);
        await _context.SaveChangesAsync();

        // Build OAuth URL - Facebook Pages only for now
        var scopes = string.Join(",", new[]
        {
            "pages_show_list",
            "pages_read_engagement",
            "pages_manage_posts",
            "business_management",
            "public_profile"
        });

        var authUrl = $"{OAuthBaseUrl}?" +
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
        var tokenUrl = $"{GraphApiBaseUrl}/oauth/access_token?" +
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
        var longLivedTokenUrl = $"{GraphApiBaseUrl}/oauth/access_token?" +
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
        var tokenUrl = $"{GraphApiBaseUrl}/oauth/access_token?" +
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
        var longLivedTokenUrl = $"{GraphApiBaseUrl}/oauth/access_token?" +
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

        // Use upsert pattern: update existing or create new
        var existingConnection = await _context.MetaConnections
            .Include(c => c.Pages)
            .Include(c => c.InstagramAccounts)
            .FirstOrDefaultAsync(c => c.UserId == userId);

        MetaConnection connection;
        if (existingConnection != null)
        {
            // Update existing connection
            existingConnection.AccessToken = accessToken;
            existingConnection.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
            existingConnection.UpdatedAt = DateTime.UtcNow;
            // Clear pages and Instagram accounts (will be re-added in manage flow)
            existingConnection.Pages.Clear();
            existingConnection.InstagramAccounts.Clear();
            connection = existingConnection;
            _logger.LogInformation("Updating existing connection {ConnectionId}", connection.Id);
        }
        else
        {
            // Create new connection (identity-level only, no pages selected yet)
            connection = new MetaConnection
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AccessToken = accessToken,
                TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn),
                ConnectedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
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
            $"https://graph.facebook.com/v21.0/me?fields=id,name&access_token={accessToken}");

        var body = await resp.Content.ReadAsStringAsync();
        _logger.LogInformation("Meta /me response: {Body}", body);
    }

    private async Task DebugPermissionsAsync(string accessToken)
    {
        var resp = await _httpClient.GetAsync(
            $"https://graph.facebook.com/v21.0/me/permissions?access_token={accessToken}");

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
                // Get Instagram Business Account linked to this page
                var igUrl = $"{GraphApiBaseUrl}/{page.Id}?fields=instagram_business_account{{id,username,name,profile_picture_url}}&access_token={page.AccessToken}";
                var response = await _httpClient.GetAsync(igUrl);

                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<MetaPageInstagramResponse>(json);

                if (data?.InstagramBusinessAccount != null)
                {
                    var ig = data.InstagramBusinessAccount;
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

        // Remove existing connection if any
        var existingConnection = await _context.MetaConnections
            .Include(c => c.Pages)
            .Include(c => c.InstagramAccounts)
            .FirstOrDefaultAsync(c => c.UserId == userId);

        if (existingConnection != null)
        {
            _context.MetaConnections.Remove(existingConnection);
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

        // Create new connection
        var connection = new MetaConnection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccessToken = oauthState.TempAccessToken,
            TokenExpiresAt = oauthState.TokenExpiresAt.Value,
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Add pages
        foreach (var page in selectedPages)
        {
            connection.Pages.Add(new ConnectedPage
            {
                Id = Guid.NewGuid(),
                PageId = page.Id,
                Name = page.Name,
                Category = page.Category,
                PictureUrl = page.PictureUrl,
                AccessToken = page.AccessToken!,
                CreatedAt = DateTime.UtcNow
            });
        }

        // Add Instagram accounts
        foreach (var ig in selectedIgAccounts)
        {
            connection.InstagramAccounts.Add(new ConnectedInstagramAccount
            {
                Id = Guid.NewGuid(),
                IgBusinessId = ig.Id,
                Username = ig.Username,
                Name = ig.Name,
                ProfilePictureUrl = ig.ProfilePictureUrl,
                PageId = ig.PageId,
                PageName = ig.PageName,
                CreatedAt = DateTime.UtcNow
            });
        }

        _context.MetaConnections.Add(connection);

        // Clean up OAuth state
        _context.MetaOAuthStates.Remove(oauthState);

        await _context.SaveChangesAsync();

        return new MetaSaveConnectionResponse(MapToDto(connection));
    }

    public async Task<MetaConnectionResponse> GetConnectionAsync(Guid userId)
    {
        var connection = await _context.MetaConnections
            .Include(c => c.Pages)
            .Include(c => c.InstagramAccounts)
            .FirstOrDefaultAsync(c => c.UserId == userId);

        if (connection == null)
        {
            return new MetaConnectionResponse(null, false);
        }

        return new MetaConnectionResponse(MapToDto(connection), true);
    }

    public async Task<MetaAvailablePagesResponse> GetAvailablePagesAsync(Guid userId)
    {
        var connection = await _context.MetaConnections.FirstOrDefaultAsync(c => c.UserId == userId);
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
            .FirstOrDefaultAsync(c => c.UserId == userId);

        if (connection == null)
        {
            throw new InvalidOperationException("No Meta connection found");
        }

        // Fetch all available pages
        var allPages = await FetchUserPagesAsync(connection.AccessToken);
        var selectedPages = allPages.Where(p => selectedPageIds.Contains(p.Id)).ToList();

        // Allow zero pages - user can disconnect all pages while keeping Meta identity connected

        // Discover Instagram accounts (only for selected pages)
        var igResponse = selectedPageIds.Any()
            ? await DiscoverInstagramAccountsAsync("", selectedPageIds)
            : new MetaDiscoverInstagramResponse(new List<InstagramAccountDto>());
        var selectedIgAccounts = igResponse.InstagramAccounts
            .Where(ig => selectedInstagramIds.Contains(ig.Id))
            .ToList();

        // Explicitly remove existing pages and Instagram accounts from the context
        _context.ConnectedPages.RemoveRange(connection.Pages);
        _context.ConnectedInstagramAccounts.RemoveRange(connection.InstagramAccounts);

        connection.UpdatedAt = DateTime.UtcNow;

        // Add new pages
        foreach (var page in selectedPages)
        {
            var connectedPage = new ConnectedPage
            {
                Id = Guid.NewGuid(),
                MetaConnectionId = connection.Id,
                PageId = page.Id,
                Name = page.Name,
                Category = page.Category,
                PictureUrl = page.PictureUrl,
                AccessToken = page.AccessToken!,
                CreatedAt = DateTime.UtcNow
            };
            _context.ConnectedPages.Add(connectedPage);
        }

        // Add new Instagram accounts
        foreach (var ig in selectedIgAccounts)
        {
            var connectedIg = new ConnectedInstagramAccount
            {
                Id = Guid.NewGuid(),
                MetaConnectionId = connection.Id,
                IgBusinessId = ig.Id,
                Username = ig.Username,
                Name = ig.Name,
                ProfilePictureUrl = ig.ProfilePictureUrl,
                PageId = ig.PageId,
                PageName = ig.PageName,
                CreatedAt = DateTime.UtcNow
            };
            _context.ConnectedInstagramAccounts.Add(connectedIg);
        }

        await _context.SaveChangesAsync();

        // Reload the connection with the new pages/accounts for the response
        await _context.Entry(connection).Collection(c => c.Pages).LoadAsync();
        await _context.Entry(connection).Collection(c => c.InstagramAccounts).LoadAsync();

        return new MetaSaveConnectionResponse(MapToDto(connection));
    }

    public async Task DisconnectAsync(Guid userId)
    {
        var connection = await _context.MetaConnections
            .Include(c => c.Pages)
            .Include(c => c.InstagramAccounts)
            .FirstOrDefaultAsync(c => c.UserId == userId);

        if (connection == null)
        {
            return; // Already disconnected
        }

        // Optionally revoke token with Meta
        try
        {
            var revokeUrl = $"{GraphApiBaseUrl}/me/permissions?access_token={connection.AccessToken}";
            await _httpClient.DeleteAsync(revokeUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to revoke Meta token");
        }

        _context.MetaConnections.Remove(connection);
        await _context.SaveChangesAsync();
    }

    private async Task<List<FacebookPageDto>> FetchUserPagesAsync(string accessToken)
    {
        var pages = new List<FacebookPageDto>();
        var url = $"{GraphApiBaseUrl}/me/accounts?fields=id,name,category,picture{{url}},access_token&access_token={accessToken}";

        while (!string.IsNullOrEmpty(url))
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("RAW /me/accounts JSON: {Json}", json);
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
                p.PictureUrl
            )).ToList(),
            connection.InstagramAccounts.Select(ig => new ConnectedInstagramAccountDto(
                ig.Id.ToString(),
                ig.IgBusinessId,
                ig.Username,
                ig.Name,
                ig.ProfilePictureUrl,
                ig.PageId,
                ig.PageName
            )).ToList()
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

// Settings class
public class MetaOAuthSettings
{
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
}
