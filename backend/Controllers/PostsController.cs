using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Services.Validation;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class PostsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IPostScheduler _scheduler;
    private readonly IFacebookInsightsService _facebookInsights;
    private readonly ICurrentWorkspaceProvider _currentWorkspace;
    private readonly IMediaValidationGate _mediaGate;
    private readonly ScheduleGuard _scheduleGuard;
    private readonly ILogger<PostsController> _logger;

    public PostsController(
        AppDbContext context,
        IPostScheduler scheduler,
        IFacebookInsightsService facebookInsights,
        ICurrentWorkspaceProvider currentWorkspace,
        IMediaValidationGate mediaGate,
        ILogger<PostsController> logger,
        SchedulingOptions? schedulingOptions = null)
    {
        _context = context;
        _scheduler = scheduler;
        _facebookInsights = facebookInsights;
        _currentWorkspace = currentWorkspace;
        _mediaGate = mediaGate;
        // Optional so existing callers/tests keep working; production DI injects the configured
        // options. Falls back to safe defaults (past-grace 2m, max 365d, cap 500) when absent.
        _scheduleGuard = new ScheduleGuard(context, schedulingOptions ?? new SchedulingOptions());
        _logger = logger;
    }

    /// <summary>
    /// Backend statuses that the "In Progress" UI tab collapses into a single bucket.
    /// These are all "the system is actively working on this" states — a user-facing
    /// simplification only; the underlying <see cref="PostStatus"/> values are unchanged.
    /// </summary>
    private static readonly PostStatus[] InProgressStatuses =
    {
        PostStatus.Publishing,
        PostStatus.Processing,
        PostStatus.RetryPending,
    };

    [HttpGet]
    public async Task<ActionResult<PaginatedResponse<PostDto>>> GetPosts(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] PostStatus? status = null,
        [FromQuery] string? statusGroup = null,
        [FromQuery] PostType? postType = null)
    {
        // Ensure valid pagination parameters
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);

        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        var query = _context.Posts.Where(p => p.WorkspaceId == workspaceId);

        // statusGroup collapses several backend statuses into one UI filter (e.g.
        // "inProgress" → Publishing/Processing/RetryPending). When provided it takes
        // precedence over a single `status` value. Unknown group names match nothing.
        if (!string.IsNullOrWhiteSpace(statusGroup))
        {
            if (string.Equals(statusGroup, "inProgress", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(p => InProgressStatuses.Contains(p.Status));
            }
            else
            {
                query = query.Where(p => false);
            }
        }
        else if (status.HasValue)
        {
            query = query.Where(p => p.Status == status.Value);
        }

        if (postType.HasValue)
        {
            query = query.Where(p => p.PostType == postType.Value);
        }

        // Provider- and asset-aware visibility filter (strict product rule).
        //
        // A post is visible iff BOTH hold:
        //   1. Its target's MetaConnection is the workspace's currently active
        //      Meta connection, identified by stable (Provider + ProviderAccountId),
        //      with a connection-id fallback for legacy/transitional rows whose
        //      ProviderAccountId hasn't been backfilled yet — those rows are
        //      equivalent to the active row by definition (they ARE the active row).
        //   2. The target asset itself (Page / IG account) is currently connected.
        //      A provider-level reconnect alone does NOT resurface an asset's post
        //      history: the Page/IG must be re-selected, which flips the SAME asset
        //      row back to IsConnected (ReconcileSelectedAssetsAsync), keeping post
        //      FKs intact so history reappears only for re-connected assets.
        var activeMeta = await _context.MetaConnections
            .Where(c => c.WorkspaceId == workspaceId
                     && c.Provider == ProviderType.Meta
                     && c.IsConnected)
            .Select(c => new { c.Id, c.ProviderAccountId })
            .FirstOrDefaultAsync();

        if (activeMeta == null)
        {
            // No active Meta provider connection → hide every Meta-tied post.
            query = query.Where(p => false);
        }
        else
        {
            var activeMetaId = activeMeta.Id;
            var activeMetaProviderAccountId = activeMeta.ProviderAccountId;

            query = query.Where(p =>
                (p.Platform == Platform.Facebook
                    && p.TargetPage != null
                    && p.TargetPage.MetaConnection != null
                    && p.TargetPage.MetaConnection.Provider == ProviderType.Meta
                    && p.TargetPage.MetaConnection.IsConnected
                    && p.TargetPage.IsConnected
                    && (
                        // Stable identity match (new rows post-migration).
                        (activeMetaProviderAccountId != null
                            && p.TargetPage.MetaConnection.ProviderAccountId == activeMetaProviderAccountId)
                        // Legacy fallback: id-equal to the currently active row.
                        || p.TargetPage.MetaConnection.Id == activeMetaId
                    ))
                || (p.Platform == Platform.Instagram
                    && p.TargetInstagramAccount != null
                    && p.TargetInstagramAccount.MetaConnection != null
                    && p.TargetInstagramAccount.MetaConnection.Provider == ProviderType.Meta
                    && p.TargetInstagramAccount.MetaConnection.IsConnected
                    && p.TargetInstagramAccount.IsConnected
                    && (
                        (activeMetaProviderAccountId != null
                            && p.TargetInstagramAccount.MetaConnection.ProviderAccountId == activeMetaProviderAccountId)
                        || p.TargetInstagramAccount.MetaConnection.Id == activeMetaId
                    )));
        }

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var postEntities = await query
            .Include(p => p.TargetPage)
            .Include(p => p.TargetInstagramAccount)
            .Include(p => p.MediaItems)
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var mediaLookup = await LoadMediaLookupAsync(workspaceId, postEntities);
        var posts = postEntities
            .Select(p => PostDto.FromEntity(p, mediaLookup))
            .ToList();

        return new PaginatedResponse<PostDto>(
            posts,
            page,
            pageSize,
            totalCount,
            totalPages
        );
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PostDto>> GetPost(Guid id)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        var post = await _context.Posts
            .Include(p => p.TargetPage)
            .Include(p => p.TargetInstagramAccount)
            .Include(p => p.MediaItems)
            .FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == workspaceId);

        if (post == null)
        {
            return NotFound();
        }

        var mediaLookup = await LoadMediaLookupAsync(workspaceId, new[] { post });
        return PostDto.FromEntity(post, mediaLookup);
    }

    [HttpGet("{id}/details")]
    public async Task<ActionResult<PostDetailsDto>> GetPostDetails(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(cancellationToken);
        var post = await _context.Posts
            .Include(p => p.TargetPage)
            .Include(p => p.TargetInstagramAccount)
            .Include(p => p.MediaItems)
            .FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == workspaceId, cancellationToken);

        if (post == null)
        {
            return NotFound();
        }

        // Fetch engagement metrics for published Facebook posts
        PostEngagementDto? engagement = null;
        string? externalPostUrl = post.ExternalPostUrl; // Use stored permalink (e.g. from Instagram, FB stories)
        string? profileUrl = post.ProfileUrl;
        string? pageUrl = null;

        // Compute profileUrl for Instagram stories (fallback when story permalink not available)
        if (post.Platform == Platform.Instagram &&
            post.PostType == PostType.Story &&
            post.TargetInstagramAccount != null &&
            string.IsNullOrEmpty(profileUrl))
        {
            profileUrl = $"https://www.instagram.com/{post.TargetInstagramAccount.Username}/";
        }

        // Compute pageUrl for Facebook posts/stories (fallback for stories when permalink unavailable)
        if (post.Platform == Platform.Facebook && post.TargetPage != null)
        {
            pageUrl = $"https://www.facebook.com/{post.TargetPage.PageId}";
        }

        if (post.Platform == Platform.Facebook &&
            post.Status == PostStatus.Published &&
            !string.IsNullOrEmpty(post.ExternalPostId))
        {
            // For FB stories, use the stored ExternalPostUrl (permalink_url fetched after publish)
            // For FB feed posts, construct the URL if not already stored
            if (post.PostType == PostType.Feed && string.IsNullOrEmpty(externalPostUrl))
            {
                externalPostUrl = $"https://www.facebook.com/{post.ExternalPostId}";
            }

            // Try to get page access token - first from TargetPage, then look up by Facebook PageId.
            // CRITICAL: every ConnectedPage lookup MUST be filtered by the current workspaceId.
            // Using a page from another workspace would call Meta with that workspace's token,
            // returning cross-tenant engagement data.
            string? pageAccessToken = post.TargetPage?.AccessToken;

            if (string.IsNullOrEmpty(pageAccessToken))
            {
                // TargetPage might be null if user reconnected Meta (new ConnectedPage IDs).
                // Try to find the page by Facebook PageId from ExternalPostId (format: pageId_postId)
                // within the SAME workspace as the post. No cross-workspace fallback — if the post's
                // workspace has no matching page, we return engagement = null rather than risk leaking
                // another workspace's access token.
                var externalIdParts = post.ExternalPostId.Split('_');
                if (externalIdParts.Length >= 2)
                {
                    var facebookPageId = externalIdParts[0];
                    _logger.LogInformation(
                        "Looking up page by Facebook PageId {FacebookPageId} in workspace {WorkspaceId}",
                        facebookPageId, workspaceId);

                    var currentPage = await _context.Set<ConnectedPage>()
                        .FirstOrDefaultAsync(
                            p => p.PageId == facebookPageId && p.WorkspaceId == workspaceId,
                            cancellationToken);

                    pageAccessToken = currentPage?.AccessToken;

                    if (currentPage != null)
                    {
                        _logger.LogInformation("Found page {PageName} for engagement fetch", currentPage.Name);
                    }
                }
            }

            if (!string.IsNullOrEmpty(pageAccessToken))
            {
                _logger.LogInformation("Fetching engagement for post {PostId}", post.Id);

                engagement = await _facebookInsights.GetPostEngagementAsync(
                    post.ExternalPostId,
                    pageAccessToken,
                    cancellationToken);
            }
            else
            {
                _logger.LogWarning(
                    "Cannot fetch engagement for post {PostId}: no page access token available in workspace {WorkspaceId}",
                    post.Id, workspaceId);
            }
        }

        var mediaLookup = await LoadMediaLookupAsync(workspaceId, new[] { post }, cancellationToken);
        var mainMedia = ResolveMediaForDetails(mediaLookup, post.MediaUrl);

        return new PostDetailsDto(
            Id: post.Id,
            Content: post.Content,
            MediaUrl: ResolveMediaUrlForDetails(mainMedia, post.MediaUrl),
            MediaType: post.MediaType.ToString(),
            PostType: post.PostType.ToString(),
            Platform: post.Platform.ToString(),
            ScheduledAt: post.ScheduledAt,
            Status: post.Status.ToString(),
            CreatedAt: post.CreatedAt,
            UpdatedAt: post.UpdatedAt,
            TargetPageId: post.TargetPageId,
            TargetPageName: post.TargetPage?.Name,
            TargetInstagramAccountId: post.TargetInstagramAccountId,
            TargetInstagramAccountName: post.TargetInstagramAccount != null
                ? $"@{post.TargetInstagramAccount.Username}"
                : null,
            PublishedAt: post.PublishedAt,
            ExternalPostId: post.ExternalPostId,
            ErrorMessage: post.ErrorMessage,
            RetryCount: post.RetryCount,
            ProcessingPollCount: post.ProcessingPollCount,
            NextRetryAt: post.NextRetryAt,
            Engagement: engagement,
            ExternalPostUrl: externalPostUrl,
            ProfileUrl: profileUrl,
            PageUrl: pageUrl,
            InstagramMediaType: post.InstagramMediaType?.ToString(),
            Thumbnail: mainMedia?.Thumbnail,
            MediaItems: post.MediaItems?.Count > 0
                ? post.MediaItems.OrderBy(m => m.Order)
                    .Select(m =>
                    {
                        var itemMedia = ResolveMediaForDetails(mediaLookup, m.MediaUrl);
                        return new PostDetailsMediaItemDto(
                            m.Id,
                            m.Order,
                            ResolveMediaUrlForDetails(itemMedia, m.MediaUrl),
                            m.MediaType.ToString(),
                            itemMedia?.Thumbnail,
                            itemMedia?.MediaId);
                    })
                    .ToList()
                : null,
            TargetConnectionActive: post.Platform switch
            {
                Platform.Facebook  => post.TargetPage?.IsConnected,
                Platform.Instagram => post.TargetInstagramAccount?.IsConnected,
                _ => (bool?)null,
            },
            MediaId: mainMedia?.MediaId
        );
    }

    private async Task<Dictionary<string, MediaLookupEntry>> LoadMediaLookupAsync(
        Guid workspaceId,
        IEnumerable<Post> posts,
        CancellationToken cancellationToken = default)
    {
        var storageKeys = posts
            .SelectMany(CollectStorageKeys)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (storageKeys.Length == 0)
            return new Dictionary<string, MediaLookupEntry>(StringComparer.Ordinal);

        var mediaRows = await _context.Media
            .AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId && storageKeys.Contains(m.StorageKey))
            .ToListAsync(cancellationToken);

        return mediaRows.ToDictionary(
            media => media.StorageKey,
            BuildMediaLookupEntry,
            StringComparer.Ordinal);
    }

    private IEnumerable<string?> CollectStorageKeys(Post post)
    {
        yield return post.MediaUrl;

        if (post.MediaItems is null)
            yield break;

        foreach (var mediaItem in post.MediaItems)
            yield return mediaItem.MediaUrl;
    }

    private MediaLookupEntry BuildMediaLookupEntry(Entities.Media media)
    {
        MediaThumbnailDto? thumbnail = null;
        if (!string.IsNullOrWhiteSpace(media.ThumbnailStorageKey))
        {
            thumbnail = new MediaThumbnailDto(
                MediaId: media.Id,
                Url: BuildMediaFileUrl(media.Id, variant: "thumbnail"),
                MimeType: media.ThumbnailMimeType,
                Width: media.ThumbnailWidth,
                Height: media.ThumbnailHeight,
                SizeBytes: media.ThumbnailSizeBytes,
                CreatedAtUtc: media.ThumbnailCreatedAtUtc);
        }

        return new MediaLookupEntry(media.Id, BuildMediaFileUrl(media.Id), thumbnail);
    }

    private static MediaLookupEntry? ResolveMediaForDetails(
        IReadOnlyDictionary<string, MediaLookupEntry> mediaLookup,
        string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            return null;

        return mediaLookup.TryGetValue(storageKey, out var entry) ? entry : null;
    }

    /// <summary>See the identical rationale on <c>PostDto.ResolveMediaUrl</c> — never returns a raw StorageKey.</summary>
    private static string? ResolveMediaUrlForDetails(MediaLookupEntry? resolved, string? rawMediaUrl)
    {
        if (resolved != null)
            return resolved.PreviewUrl;

        return LooksLikeStorageKey(rawMediaUrl) ? null : rawMediaUrl;
    }

    /// <summary>
    /// Builds the frontend-safe preview URL for a media item (<c>/api/media/{mediaId}/file</c>).
    /// The frontend only ever learns this URL (or the bare mediaId) — never the StorageKey.
    /// </summary>
    private string BuildMediaFileUrl(Guid mediaId, string? variant = null)
    {
        var query = variant is null ? string.Empty : $"?variant={variant}";
        if (Request?.Host.HasValue == true && !string.IsNullOrWhiteSpace(Request.Scheme))
            return $"{Request.Scheme}://{Request.Host}/api/media/{mediaId}/file{query}";

        return $"/api/media/{mediaId}/file{query}";
    }

    /// <summary>
    /// True when the value has the shape of a server-issued storage key (as opposed to a plain
    /// external URL). Internal so <see cref="PostDto"/>'s DTO-mapping code (same file) can reuse
    /// it to decide whether an unresolvable media reference is safe to pass through verbatim.
    /// </summary>
    internal static bool LooksLikeStorageKey(string? mediaUrl)
    {
        if (string.IsNullOrWhiteSpace(mediaUrl))
            return false;
        if (mediaUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return false;

        return mediaUrl.StartsWith("media/", StringComparison.OrdinalIgnoreCase)
            || mediaUrl.StartsWith("workspaces/", StringComparison.OrdinalIgnoreCase)
            || mediaUrl.StartsWith("users/", StringComparison.OrdinalIgnoreCase);
    }

    [HttpPost]
    public async Task<ActionResult<PostDto>> CreatePost(CreatePostRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(cancellationToken);

        // Resolve frontend-supplied MediaId(s) to the internal StorageKey(s) BEFORE any other
        // validation runs. Media.Id is the only media reference the frontend should submit;
        // the resolved request below has MediaUrl/MediaItems[].MediaUrl populated from the
        // owning Media row (never trusted from the client), so the rest of this method is
        // unchanged from the pre-MediaId design.
        var (resolvedRequest, mediaResolutionError) = await ResolveCreatePostMediaAsync(workspaceId, request, cancellationToken);
        if (mediaResolutionError != null)
            return StatusCode(mediaResolutionError.Status ?? StatusCodes.Status404NotFound, mediaResolutionError);
        request = resolvedRequest;

        var validationErrors = ValidateCreatePostRequest(request);
        if (validationErrors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(validationErrors));
        }

        // SCHEDULE GUARD: reject past-dated / far-future schedules and enforce the per-workspace
        // active scheduled-post cap server-side (the SPA also checks, but that is advisory).
        var timingError = _scheduleGuard.ValidateTiming(request.ScheduledAt);
        if (timingError != null)
            return ScheduleProblem(StatusCodes.Status400BadRequest, timingError.Value);

        var capError = await _scheduleGuard.ValidateActiveCapAsync(workspaceId);
        if (capError != null)
            return ScheduleProblem(StatusCodes.Status409Conflict, capError.Value);

        // For Facebook posts, verify the target page is connected and has a valid token
        if (request.Platform == Platform.Facebook)
        {
            if (!request.TargetPageId.HasValue)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration required",
                    Detail = "A Facebook Page must be selected to schedule a Facebook post.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            var targetPage = await _context.Set<ConnectedPage>()
                .FirstOrDefaultAsync(p => p.Id == request.TargetPageId.Value && p.WorkspaceId == workspaceId && p.IsConnected);

            if (targetPage == null)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration disconnected",
                    Detail = "The selected Facebook Page is no longer connected. Please reconnect in Connected Accounts.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            if (string.IsNullOrEmpty(targetPage.AccessToken))
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration token missing",
                    Detail = "The selected Facebook Page's access token is missing. Please reconnect in Connected Accounts.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            // Facebook multi-photo validation: 2-10 images via MediaItems
            if (request.MediaItems is { Count: > 0 })
            {
                var fbVideosCount = request.MediaItems.Count(m => m.MediaType == MediaType.Video);
                var fbImagesCount = request.MediaItems.Count(m => m.MediaType == MediaType.Image);

                // Facebook does not support multi-video
                if (fbVideosCount > 1)
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Unsupported media combination",
                        Detail = "Facebook supports 1 video per post. Remove extra videos or use Instagram for video carousel.",
                        Status = StatusCodes.Status400BadRequest,
                        Extensions =
                        {
                            ["code"] = "UNSUPPORTED_MEDIA_COMBINATION",
                            ["imagesCount"] = fbImagesCount,
                            ["videosCount"] = fbVideosCount,
                            ["platforms"] = new[] { "Facebook" },
                        }
                    });
                }

                // No mixed media
                if (fbImagesCount > 0 && fbVideosCount > 0)
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Unsupported media combination",
                        Detail = "Mixed image+video posts aren't supported. Choose only images or a single video.",
                        Status = StatusCodes.Status400BadRequest,
                        Extensions =
                        {
                            ["code"] = "UNSUPPORTED_MEDIA_COMBINATION",
                            ["imagesCount"] = fbImagesCount,
                            ["videosCount"] = fbVideosCount,
                            ["platforms"] = new[] { "Facebook" },
                        }
                    });
                }

                if (request.MediaItems.Count < 2 || request.MediaItems.Count > 10)
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Invalid multi-photo post",
                        Detail = "Facebook multi-photo posts require 2 to 10 images.",
                        Status = StatusCodes.Status400BadRequest,
                    });
                }

                // All items must be images (videos handled above)
                if (request.MediaItems.Any(m => m.MediaType != MediaType.Image))
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Invalid multi-photo media",
                        Detail = "Facebook multi-photo posts only support images.",
                        Status = StatusCodes.Status400BadRequest,
                    });
                }
            }
        }

        // Instagram-specific validation
        if (request.Platform == Platform.Instagram)
        {
            if (!request.TargetInstagramAccountId.HasValue)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration required",
                    Detail = "An Instagram Business Account must be selected to schedule an Instagram post.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            var targetIgAccount = await _context.Set<ConnectedInstagramAccount>()
                .FirstOrDefaultAsync(a => a.Id == request.TargetInstagramAccountId.Value && a.WorkspaceId == workspaceId && a.IsConnected);

            if (targetIgAccount == null)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration disconnected",
                    Detail = "The selected Instagram account is no longer connected. Please reconnect in Connected Accounts.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            // Instagram carousel: 2-10 items via MediaItems (images, videos, or mixed)
            var hasMultipleMediaItems = request.MediaItems is { Count: > 0 };
            if (hasMultipleMediaItems)
            {
                // Carousel validation
                if (request.MediaItems!.Count < 2 || request.MediaItems.Count > 10)
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = request.MediaItems.Count > 10 ? "Too many carousel items" : "Invalid carousel",
                        Detail = "Instagram carousel requires 2 to 10 items.",
                        Status = StatusCodes.Status400BadRequest,
                        Extensions =
                        {
                            ["code"] = request.MediaItems.Count > 10 ? "TOO_MANY_CAROUSEL_ITEMS" : "INVALID_CAROUSEL",
                            ["totalCount"] = request.MediaItems.Count,
                            ["platforms"] = new[] { "Instagram" },
                        }
                    });
                }

                // All items must be images or videos (no other types)
                if (request.MediaItems.Any(m => m.MediaType != MediaType.Image && m.MediaType != MediaType.Video))
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Invalid carousel media",
                        Detail = "Instagram carousel only supports images or videos.",
                        Status = StatusCodes.Status400BadRequest,
                    });
                }

                // Mixed media (images + videos) is allowed for Instagram-only carousels
            }
            else
            {
                // Single media: existing validation (image or video required)
                var mediaType = request.MediaType ?? MediaType.None;
                if (string.IsNullOrEmpty(request.MediaUrl) || (mediaType != MediaType.Image && mediaType != MediaType.Video))
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Invalid media",
                        Detail = "Instagram Feed posts require at least one media item (image or video). Text-only posts are not supported.",
                        Status = StatusCodes.Status400BadRequest,
                    });
                }
            }
        }

        // Story-specific validation
        if (request.PostType == PostType.Story)
        {
            // Stories require media (no text-only stories)
            var storyMediaType = request.MediaType ?? MediaType.None;
            if (string.IsNullOrEmpty(request.MediaUrl) || (storyMediaType != MediaType.Image && storyMediaType != MediaType.Video))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid story",
                    Detail = "Stories require exactly one media item (image or video).",
                    Status = StatusCodes.Status400BadRequest,
                });
            }

            // Stories don't support carousel/multi-media
            if (request.MediaItems is { Count: > 0 })
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid story",
                    Detail = "Stories only support a single media item. Multi-image stories are not supported.",
                    Status = StatusCodes.Status400BadRequest,
                });
            }

            // Stories only supported on Facebook and Instagram
            if (request.Platform != Platform.Facebook && request.Platform != Platform.Instagram)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Unsupported platform",
                    Detail = "Stories are only supported on Facebook and Instagram.",
                    Status = StatusCodes.Status400BadRequest,
                });
            }
        }

        // Instagram user tags validation
        string? serializedUserTags = null;
        if (request.InstagramUserTags is { Count: > 0 })
        {
            if (request.Platform != Platform.Instagram || request.PostType != PostType.Feed)
            {
                // Silently ignore tags for non-Instagram or non-Feed posts
                _logger.LogWarning("Instagram user tags provided for non-IG-Feed post (Platform={Platform}, PostType={PostType}). Ignoring.",
                    request.Platform, request.PostType);
            }
            else
            {
                var usernameRegex = new Regex(@"^[A-Za-z0-9._]{1,30}$");
                foreach (var tag in request.InstagramUserTags)
                {
                    if (!usernameRegex.IsMatch(tag.Username))
                    {
                        return BadRequest(new ProblemDetails
                        {
                            Title = "Invalid user tag",
                            Detail = $"Invalid Instagram username: '{tag.Username}'. Must be 1-30 characters of letters, digits, dots, or underscores.",
                            Status = StatusCodes.Status400BadRequest,
                        });
                    }
                    if (tag.X < 0 || tag.X > 1 || tag.Y < 0 || tag.Y > 1)
                    {
                        return BadRequest(new ProblemDetails
                        {
                            Title = "Invalid user tag position",
                            Detail = $"Tag position for @{tag.Username} is out of bounds. X and Y must be between 0 and 1.",
                            Status = StatusCodes.Status400BadRequest,
                        });
                    }
                }

                // Include tags for single image or single video posts (not carousel)
                var mediaType = request.MediaType ?? MediaType.None;
                if ((mediaType == MediaType.Image || mediaType == MediaType.Video) && request.MediaItems is not { Count: > 0 })
                {
                    serializedUserTags = JsonSerializer.Serialize(
                        request.InstagramUserTags.Select(t => new { username = t.Username, x = t.X, y = t.Y }),
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    _logger.LogInformation(
                        "Instagram user tags: {Count} tags ({Usernames}) for {MediaType} | Serialized JSON: {Json}",
                        request.InstagramUserTags.Count,
                        string.Join(", ", request.InstagramUserTags.Select(t => "@" + t.Username)),
                        mediaType,
                        serializedUserTags);
                }
                else
                {
                    _logger.LogWarning("Instagram user tags provided for unsupported post type (MediaType={MediaType}, carousel={IsCarousel}). Ignoring.",
                        mediaType, request.MediaItems is { Count: > 0 });
                }
            }
        }

        // Instagram per-media-item tags validation (carousel posts)
        string? serializedMediaTags = null;
        if (request.InstagramMediaTags is { Count: > 0 })
        {
            if (request.Platform != Platform.Instagram || request.PostType != PostType.Feed)
            {
                _logger.LogWarning("Instagram per-media tags provided for non-IG-Feed post. Ignoring.");
            }
            else if (request.MediaItems is not { Count: >= 2 })
            {
                _logger.LogWarning("Instagram per-media tags provided for non-carousel post. Ignoring.");
            }
            else
            {
                var usernameRegex = new Regex(@"^[A-Za-z0-9._]{1,30}$");
                var validMediaOrders = request.MediaItems.Select(m => m.Order).ToHashSet();

                foreach (var (order, tags) in request.InstagramMediaTags)
                {
                    if (!validMediaOrders.Contains(order))
                    {
                        return BadRequest(new ProblemDetails
                        {
                            Title = "Invalid media tag index",
                            Detail = $"Media tag index {order} does not match any media item.",
                            Status = StatusCodes.Status400BadRequest,
                        });
                    }

                    foreach (var tag in tags)
                    {
                        if (!usernameRegex.IsMatch(tag.Username))
                        {
                            return BadRequest(new ProblemDetails
                            {
                                Title = "Invalid user tag",
                                Detail = $"Invalid Instagram username: '{tag.Username}' on media item {order}.",
                                Status = StatusCodes.Status400BadRequest,
                            });
                        }
                        if (tag.X < 0 || tag.X > 1 || tag.Y < 0 || tag.Y > 1)
                        {
                            return BadRequest(new ProblemDetails
                            {
                                Title = "Invalid user tag position",
                                Detail = $"Tag position for @{tag.Username} on media item {order} is out of bounds.",
                                Status = StatusCodes.Status400BadRequest,
                            });
                        }
                    }
                }

                // Serialize: key = order string, value = tag array with username/x/y
                var tagsDict = request.InstagramMediaTags.ToDictionary(
                    kvp => kvp.Key.ToString(),
                    kvp => kvp.Value.Select(t => new { username = t.Username, x = t.X, y = t.Y }).ToList()
                );
                serializedMediaTags = JsonSerializer.Serialize(tagsDict,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                var totalTags = request.InstagramMediaTags.Values.Sum(v => v.Count);
                _logger.LogInformation(
                    "Instagram per-media tags: {TotalTags} tags across {MediaCount} media items | JSON: {Json}",
                    totalTags, request.InstagramMediaTags.Count, serializedMediaTags);
            }
        }

        // AUTHORITATIVE MEDIA VALIDATION GATE.
        // The SPA pre-validates media for UX, but that is advisory: a crafted or replayed
        // request could otherwise schedule media that Meta will reject (e.g. a PNG for
        // Instagram, or an out-of-aspect image). We re-validate every attached image against
        // every selected target here, server-side, and refuse to create the post on any
        // blocking error. Warnings do not block. (Phase 2: images only; videos pass through.)
        var mediaGateProblem = await ValidateMediaForTargetsAsync(workspaceId, request);
        if (mediaGateProblem != null)
            return BadRequest(mediaGateProblem);

        var post = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            // Stories don't support captions — ignore any content sent by the client
            Content = request.PostType == PostType.Story ? string.Empty : (request.Content ?? string.Empty),
            MediaUrl = request.MediaUrl,
            MediaType = request.MediaType ?? MediaType.None,
            PostType = request.PostType,
            Platform = request.Platform,
            ScheduledAt = request.ScheduledAt,
            TargetPageId = request.TargetPageId,
            TargetInstagramAccountId = request.TargetInstagramAccountId,
            SelectedThumbnailUrl = request.SelectedThumbnailUrl,
            InstagramUserTags = serializedUserTags,
            InstagramMediaTagsJson = serializedMediaTags,
            Status = PostStatus.Scheduled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Add media items for carousel/multi-media posts
        if (request.MediaItems is { Count: > 0 })
        {
            // Set media type based on what the carousel contains
            var firstItemType = request.MediaItems.OrderBy(m => m.Order).First().MediaType;
            post.MediaType = firstItemType;
            post.MediaUrl = request.MediaItems.OrderBy(m => m.Order).First().MediaUrl; // First item as legacy preview
            post.MediaItems = request.MediaItems
                .OrderBy(m => m.Order)
                .Select((m, i) => new PostMediaItem
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    PostId = post.Id,
                    Order = i,
                    MediaUrl = m.MediaUrl,
                    MediaType = m.MediaType,
                })
                .ToList();
        }

        _context.Posts.Add(post);
        await _context.SaveChangesAsync();

        // Schedule the post for publication
        var scheduleResult = await _scheduler.ScheduleAsync(post);
        if (scheduleResult.Success && !string.IsNullOrEmpty(scheduleResult.ScheduleIdentifier))
        {
            post.ScheduleArn = scheduleResult.ScheduleIdentifier;
            await _context.SaveChangesAsync();
        }
        else if (!scheduleResult.Success)
        {
            _logger.LogWarning("Failed to schedule post {PostId}: {Error}",
                post.Id, scheduleResult.ErrorMessage);
        }

        // Reload navigation properties for the response
        await _context.Entry(post).Reference(p => p.TargetPage).LoadAsync();
        await _context.Entry(post).Reference(p => p.TargetInstagramAccount).LoadAsync();
        await _context.Entry(post).Collection(p => p.MediaItems).LoadAsync();

        var createdMediaLookup = await LoadMediaLookupAsync(workspaceId, new[] { post }, cancellationToken);
        return CreatedAtAction(nameof(GetPost), new { id = post.Id }, PostDto.FromEntity(post, createdMediaLookup));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdatePost(Guid id, UpdatePostRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(cancellationToken);
        var post = await _context.Posts.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == workspaceId, cancellationToken);

        if (post == null)
        {
            return NotFound();
        }

        // Only allow updates to scheduled posts
        if (post.Status != PostStatus.Scheduled)
        {
            return BadRequest(new { error = "Cannot update a post that is not scheduled" });
        }

        // Resolve a frontend-supplied MediaId to its internal StorageKey before validation —
        // see the identical rationale in CreatePost.
        var (resolvedUpdateRequest, updateMediaError) = await ResolveUpdatePostMediaAsync(workspaceId, request, cancellationToken);
        if (updateMediaError != null)
            return StatusCode(updateMediaError.Status ?? StatusCodes.Status404NotFound, updateMediaError);
        request = resolvedUpdateRequest;

        var validationErrors = ValidateUpdatePostRequest(request);
        if (validationErrors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(validationErrors));
        }

        // SCHEDULE GUARD: the post stays Scheduled after an edit, so re-validate the (possibly
        // changed) schedule time. The cap check excludes THIS post — an in-place edit of an
        // already-counted scheduled post must never be blocked by the workspace being at cap.
        var timingError = _scheduleGuard.ValidateTiming(request.ScheduledAt);
        if (timingError != null)
            return ScheduleProblem(StatusCodes.Status400BadRequest, timingError.Value);

        var capError = await _scheduleGuard.ValidateActiveCapAsync(workspaceId, excludePostId: post.Id);
        if (capError != null)
            return ScheduleProblem(StatusCodes.Status409Conflict, capError.Value);

        // For Facebook posts, verify the target page is connected and has a valid token
        if (request.Platform == Platform.Facebook)
        {
            if (!request.TargetPageId.HasValue)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration required",
                    Detail = "A Facebook Page must be selected to schedule a Facebook post.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            var targetPage = await _context.Set<ConnectedPage>()
                .FirstOrDefaultAsync(p => p.Id == request.TargetPageId.Value && p.WorkspaceId == workspaceId && p.IsConnected);

            if (targetPage == null)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration disconnected",
                    Detail = "The selected Facebook Page is no longer connected. Please reconnect in Connected Accounts.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            if (string.IsNullOrEmpty(targetPage.AccessToken))
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration token missing",
                    Detail = "The selected Facebook Page's access token is missing. Please reconnect in Connected Accounts.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }
        }

        // Instagram-specific validation for updates
        if (request.Platform == Platform.Instagram)
        {
            if (!request.TargetInstagramAccountId.HasValue)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration required",
                    Detail = "An Instagram Business Account must be selected to schedule an Instagram post.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            var updateMediaType = request.MediaType ?? MediaType.None;
            if (string.IsNullOrEmpty(request.MediaUrl) || (updateMediaType != MediaType.Image && updateMediaType != MediaType.Video))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid media",
                    Detail = "Instagram Feed posts require exactly one media item (image or video).",
                    Status = StatusCodes.Status400BadRequest,
                });
            }
        }

        // AUTHORITATIVE MEDIA VALIDATION GATE (edit path).
        // Mirrors the CreatePost gate so an edit cannot save media that creation would reject
        // (e.g. swapping in a PNG for Instagram, or an out-of-aspect image). We resolve the
        // effective media/target after the update and refuse to persist any change on a blocking
        // error. Warnings do not block. The post's placement (Feed/Story) is immutable on update,
        // so it is taken from the existing row rather than the request.
        var updateItems = new List<MediaGateItem>();
        if (!string.IsNullOrEmpty(request.MediaUrl))
            updateItems.Add(new MediaGateItem(request.MediaUrl, request.MediaType ?? MediaType.None, 0));

        var updatePlacement = post.PostType == PostType.Story ? Placement.Story : Placement.Feed;
        var mediaGateProblem = await RunMediaGateAsync(
            workspaceId, updateItems, new MediaGateTarget(request.Platform, updatePlacement), "update");
        if (mediaGateProblem != null)
            return BadRequest(mediaGateProblem);

        var scheduledAtChanged = post.ScheduledAt != request.ScheduledAt;

        post.Content = request.Content;
        post.MediaUrl = request.MediaUrl;
        post.MediaType = request.MediaType ?? MediaType.None;
        post.Platform = request.Platform;
        post.ScheduledAt = request.ScheduledAt;
        post.TargetPageId = request.TargetPageId;
        post.TargetInstagramAccountId = request.TargetInstagramAccountId;
        post.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        // Reschedule if time changed
        if (scheduledAtChanged)
        {
            var scheduleResult = await _scheduler.RescheduleAsync(post);
            if (scheduleResult.Success && !string.IsNullOrEmpty(scheduleResult.ScheduleIdentifier))
            {
                post.ScheduleArn = scheduleResult.ScheduleIdentifier;
                await _context.SaveChangesAsync();
            }
        }

        return NoContent();
    }

    [HttpPost("{id}/publish-now")]
    public async Task<ActionResult<PostDto>> PublishNow(
        Guid id,
        [FromServices] IPostPublisherResolver publisherResolver,
        [FromServices] IStoryPublisherResolver storyPublisherResolver)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        var post = await _context.Posts
            .Include(p => p.TargetPage)
            .Include(p => p.TargetInstagramAccount)
            .Include(p => p.MediaItems)
            .FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == workspaceId);

        if (post == null)
            return NotFound();

        // Only Scheduled posts can be published immediately
        if (post.Status != PostStatus.Scheduled)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Cannot publish now",
                Detail = $"Only scheduled posts can be published immediately. Current status: {post.Status}.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        _logger.LogInformation(
            "Publishing post {PostId} immediately (type={PostType}, platform={Platform})",
            post.Id, post.PostType, post.Platform);

        try
        {
            PublishResult result;

            if (post.PostType == PostType.Story)
            {
                var publisher = storyPublisherResolver.GetPublisher(post.Platform);
                if (publisher == null)
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Unsupported platform",
                        Detail = $"Story publishing is not supported for {post.Platform}.",
                        Status = StatusCodes.Status400BadRequest,
                    });
                }
                result = await publisher.PublishAsync(post.Id);
            }
            else
            {
                var publisher = publisherResolver.GetPublisher(post.Platform);
                if (publisher == null)
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Unsupported platform",
                        Detail = $"Publishing is not supported for {post.Platform}.",
                        Status = StatusCodes.Status400BadRequest,
                    });
                }
                result = await publisher.PublishAsync(post.Id);
            }

            // Reload to get fresh state after publishing
            await _context.Entry(post).ReloadAsync();
            await _context.Entry(post).Reference(p => p.TargetPage).LoadAsync();
            await _context.Entry(post).Reference(p => p.TargetInstagramAccount).LoadAsync();
            await _context.Entry(post).Collection(p => p.MediaItems).LoadAsync();

            var publishNowMediaLookup = await LoadMediaLookupAsync(workspaceId, new[] { post });

            if (result.Success)
            {
                return Ok(PostDto.FromEntity(post, publishNowMediaLookup));
            }

            // Transient publisher failures are handled inside the publisher by moving
            // the post to RetryPending/Processing and scheduling the next attempt.
            // That is not a gateway failure for Publish Now; return the fresh post so
            // the UI can show the in-progress/retry state instead of an error.
            if (post.Status == PostStatus.RetryPending || post.Status == PostStatus.Processing)
            {
                return Accepted(PostDto.FromEntity(post, publishNowMediaLookup));
            }
            else
            {
                _logger.LogWarning(
                    "Publish-now returning platform failure for post {PostId}: platform={Platform} status={Status} errorType={ErrorType} retryCount={RetryCount}/{MaxRetries} nextRetryAt={NextRetryAt} error={Error}",
                    post.Id,
                    post.Platform,
                    post.Status,
                    result.ErrorType,
                    post.RetryCount,
                    post.MaxRetries,
                    post.NextRetryAt,
                    result.ErrorMessage);

                // Publishing failed — return the error but don't 500
                var problem = new ProblemDetails
                {
                    Title = $"Publishing to {post.Platform} failed",
                    Detail = result.ErrorMessage ?? "An error occurred while publishing to the platform.",
                    Status = StatusCodes.Status502BadGateway,
                };
                problem.Extensions["platform"] = post.Platform.ToString();
                problem.Extensions["postId"] = post.Id;
                problem.Extensions["postStatus"] = post.Status.ToString();
                problem.Extensions["errorType"] = result.ErrorType?.ToString();
                problem.Extensions["retryCount"] = post.RetryCount;
                problem.Extensions["maxRetries"] = post.MaxRetries;
                problem.Extensions["nextRetryAt"] = post.NextRetryAt;
                return StatusCode(StatusCodes.Status502BadGateway, problem);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during publish-now for post {PostId}", post.Id);
            var problem = new ProblemDetails
            {
                Title = $"Publishing to {post.Platform} failed",
                Detail = ex.Message,
                Status = StatusCodes.Status502BadGateway,
            };
            problem.Extensions["platform"] = post.Platform.ToString();
            problem.Extensions["postId"] = post.Id;
            return StatusCode(StatusCodes.Status502BadGateway, problem);
        }
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> CancelPost(Guid id)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        var post = await _context.Posts.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == workspaceId);

        if (post == null)
        {
            return NotFound();
        }

        switch (post.Status)
        {
            // Already canceled — idempotent
            case PostStatus.Canceled:
                return Ok();

            // Cannot cancel posts that are publishing or already published
            case PostStatus.Publishing:
                return Conflict(new ProblemDetails
                {
                    Title = "Cannot cancel post",
                    Detail = "This post is currently being published and cannot be canceled.",
                    Status = StatusCodes.Status409Conflict,
                });

            case PostStatus.Published:
                return Conflict(new ProblemDetails
                {
                    Title = "Cannot cancel post",
                    Detail = "This post has already been published and cannot be canceled.",
                    Status = StatusCodes.Status409Conflict,
                });

            // Failed — treat as idempotent (already stopped), mark Canceled
            case PostStatus.Failed:
                post.Status = PostStatus.Canceled;
                post.CanceledAt = DateTime.UtcNow;
                post.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return Ok();

            // Scheduled / RetryPending / Processing — cancel the schedule and mark as canceled
            default:
                await _scheduler.CancelScheduleAsync(post);

                post.Status = PostStatus.Canceled;
                post.CanceledAt = DateTime.UtcNow;
                post.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                return Ok();
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeletePost(Guid id)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        var post = await _context.Posts
            .Include(p => p.MediaItems)
            .FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == workspaceId);

        if (post == null)
        {
            return NotFound();
        }

        switch (post.Status)
        {
            // Only Canceled and Failed posts can be hard-deleted
            case PostStatus.Canceled:
            case PostStatus.Failed:
                _context.Posts.Remove(post);
                await _context.SaveChangesAsync();
                return NoContent();

            // Scheduled / RetryPending / Processing — must cancel first
            case PostStatus.Scheduled:
            case PostStatus.RetryPending:
            case PostStatus.Processing:
                return Conflict(new ProblemDetails
                {
                    Title = "Cannot delete post",
                    Detail = "Cancel the scheduled post before deleting.",
                    Status = StatusCodes.Status409Conflict,
                });

            // Publishing / Published — cannot delete
            case PostStatus.Publishing:
            case PostStatus.Published:
            default:
                return Conflict(new ProblemDetails
                {
                    Title = "Cannot delete post",
                    Detail = "Cannot delete a post that is publishing or already published.",
                    Status = StatusCodes.Status409Conflict,
                });
        }
    }

    /// <summary>
    /// Resolves <see cref="CreatePostRequest.MediaId"/> / <see cref="CreatePostMediaItem.MediaId"/>
    /// (the only media reference the frontend should submit) to the corresponding
    /// <see cref="Entities.Media.StorageKey"/>, scoped to <paramref name="workspaceId"/>. Returns
    /// a request with MediaUrl/MediaItems[].MediaUrl populated from the resolved StorageKey(s) so
    /// the rest of post creation is unchanged. Unknown or foreign-workspace mediaIds produce a
    /// 404 <c>MEDIA_NOT_FOUND</c> — the response never distinguishes "doesn't exist" from
    /// "belongs to another workspace". When no MediaId is supplied for an item, that item's
    /// MediaUrl passes through unchanged (back-compat path, still enforced by the ownership gate
    /// in <see cref="RunMediaGateAsync"/> below).
    /// </summary>
    private async Task<(CreatePostRequest Request, ProblemDetails? Error)> ResolveCreatePostMediaAsync(
        Guid workspaceId, CreatePostRequest request, CancellationToken cancellationToken)
    {
        if (request.MediaId is null && !string.IsNullOrWhiteSpace(request.MediaUrl))
            return (request, UnsupportedMediaReferenceProblem());

        if (request.MediaItems is { Count: > 0 } && request.MediaItems.Any(m => m.MediaId is null || !string.IsNullOrWhiteSpace(m.MediaUrl)))
            return (request, UnsupportedMediaReferenceProblem());

        var effectiveMediaUrl = request.MediaUrl;
        if (request.MediaId.HasValue)
        {
            var media = await _context.Media.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == request.MediaId.Value && m.WorkspaceId == workspaceId, cancellationToken);
            if (media == null)
                return (request, MediaNotFoundProblem());
            effectiveMediaUrl = media.StorageKey;
        }

        var effectiveMediaItems = request.MediaItems;
        if (request.MediaItems is { Count: > 0 } && request.MediaItems.Any(m => m.MediaId.HasValue))
        {
            var resolved = new List<CreatePostMediaItem>(request.MediaItems.Count);
            foreach (var item in request.MediaItems)
            {
                var media = await _context.Media.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == item.MediaId.Value && m.WorkspaceId == workspaceId, cancellationToken);
                if (media == null)
                    return (request, MediaNotFoundProblem());

                resolved.Add(item with { MediaUrl = media.StorageKey });
            }
            effectiveMediaItems = resolved;
        }

        return (request with { MediaUrl = effectiveMediaUrl, MediaItems = effectiveMediaItems }, null);
    }

    /// <summary>Update-path counterpart of <see cref="ResolveCreatePostMediaAsync"/> (single media only — updates have no carousel MediaItems input).</summary>
    private async Task<(UpdatePostRequest Request, ProblemDetails? Error)> ResolveUpdatePostMediaAsync(
        Guid workspaceId, UpdatePostRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.MediaUrl))
            return (request, UnsupportedMediaReferenceProblem());

        if (!request.MediaId.HasValue)
            return (request, null);

        var media = await _context.Media.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == request.MediaId.Value && m.WorkspaceId == workspaceId, cancellationToken);
        if (media == null)
            return (request, MediaNotFoundProblem());

        return (request with { MediaUrl = media.StorageKey }, null);
    }

    private static ProblemDetails MediaNotFoundProblem() => new()
    {
        Title = "Media not found",
        Detail = "The referenced media was not found in this workspace.",
        Status = StatusCodes.Status404NotFound,
        Extensions = { ["code"] = MediaValidationErrorCodes.MediaNotFound },
    };

    private static ProblemDetails UnsupportedMediaReferenceProblem() => new()
    {
        Title = "Unsupported media reference",
        Detail = "Posts must reference uploaded media by mediaId.",
        Status = StatusCodes.Status400BadRequest,
        Extensions = { ["code"] = "UNSUPPORTED_MEDIA_REFERENCE" },
    };

    /// <summary>
    /// Builds the (item, target) matrix for a create request and runs the authoritative
    /// media gate. Returns a structured <see cref="ProblemDetails"/> when any selected
    /// target has invalid media, or null when everything passes (warnings are non-blocking).
    ///
    /// <para>Target model for this phase: a post has exactly one platform
    /// (<see cref="CreatePostRequest.Platform"/>) and one placement derived from
    /// (<see cref="CreatePostRequest.PostType"/> (Feed→Feed, Story→Story). The matrix is
    /// "every attached image × that single target". The per-target shape is intentionally
    /// general so multi-target cross-posting can populate more than one target later.</para>
    /// </summary>
    private async Task<ProblemDetails?> ValidateMediaForTargetsAsync(
        Guid workspaceId, CreatePostRequest request)
    {
        // Resolve attached media items: carousel/multi via MediaItems, else the single MediaUrl.
        var items = new List<MediaGateItem>();
        if (request.MediaItems is { Count: > 0 })
        {
            foreach (var m in request.MediaItems.OrderBy(m => m.Order).Select((m, i) => (m, i)))
            {
                if (!string.IsNullOrEmpty(m.m.MediaUrl))
                    items.Add(new MediaGateItem(m.m.MediaUrl, m.m.MediaType, m.i));
            }
        }
        else if (!string.IsNullOrEmpty(request.MediaUrl))
        {
            items.Add(new MediaGateItem(request.MediaUrl, request.MediaType ?? MediaType.None, 0));
        }

        var placement = request.PostType == PostType.Story ? Placement.Story : Placement.Feed;
        return await RunMediaGateAsync(workspaceId, items, new MediaGateTarget(request.Platform, placement), "creation");
    }

    /// <summary>
    /// Runs the authoritative media gate for a resolved (items, target) matrix and maps any
    /// blocking failures into the same structured <see cref="ProblemDetails"/> shape used by
    /// post creation. Returns null when there is nothing to validate (text-only) or everything
    /// passes; warnings never block. Shared by <see cref="CreatePost"/> and <see cref="UpdatePost"/>
    /// so an edit cannot save media that creation would have rejected.
    /// </summary>
    private async Task<ProblemDetails?> RunMediaGateAsync(
        Guid workspaceId, IReadOnlyList<MediaGateItem> items, MediaGateTarget target, string context)
    {
        // Nothing to validate (text-only). The platform-specific "media required" checks
        // already cover cases where media is mandatory.
        if (items.Count == 0)
            return null;

        var targets = new List<MediaGateTarget> { target };

        // requireOwnedStorageKey: reject external URLs, unknown keys, and foreign-workspace
        // keys server-side. Post creation/update is the authoritative enforcement point — the
        // media a post references must be a storage key this workspace owns.
        var result = await _mediaGate.ValidateAsync(workspaceId, items, targets, requireOwnedStorageKey: true);
        if (result.IsValid)
            return null;

        // Structured, machine-readable error payload. Each entry names the failing media
        // item (order), the target (platform + placement), and the validation code so the
        // frontend can later highlight exactly which image failed for which platform.
        var mediaErrors = result.Errors
            .Select(e => new Dictionary<string, object?>
            {
                ["order"] = e.Order,
                ["platform"] = e.Platform.ToString(),
                ["placement"] = e.Placement.ToString(),
                ["code"] = e.Code,
                ["field"] = e.Field,
                ["message"] = e.Message,
            })
            .ToList();

        var affectedPlatforms = result.Errors.Select(e => e.Platform.ToString()).Distinct().ToArray();

        _logger.LogWarning(
            "Media gate blocked post {Context} in workspace {WorkspaceId}: {ErrorCount} error(s) across platforms {Platforms}",
            context, workspaceId, result.Errors.Count, string.Join(", ", affectedPlatforms));

        return new ProblemDetails
        {
            Title = "Invalid media",
            Detail = result.Errors.Count == 1
                ? result.Errors[0].Message
                : $"{result.Errors.Count} media validation error(s) for {string.Join(", ", affectedPlatforms)}.",
            Status = StatusCodes.Status400BadRequest,
            Extensions =
            {
                ["code"] = "MEDIA_VALIDATION_FAILED",
                ["platforms"] = affectedPlatforms,
                ["mediaErrors"] = mediaErrors,
            }
        };
    }

    /// <summary>
    /// Builds a ProblemDetails for a schedule-guard rejection, carrying the machine-readable
    /// <c>code</c> in Extensions (same shape as the media-gate errors). 400 for timing errors,
    /// 409 for the active-cap limit.
    /// </summary>
    private ObjectResult ScheduleProblem(int statusCode, ScheduleValidationError error)
    {
        _logger.LogInformation("Schedule guard rejected post: code={Code}", error.Code);
        var problem = new ProblemDetails
        {
            Title = "Invalid schedule",
            Detail = error.Message,
            Status = statusCode,
        };
        problem.Extensions["code"] = error.Code;
        return StatusCode(statusCode, problem);
    }

    private static Dictionary<string, string[]> ValidateCreatePostRequest(CreatePostRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        // Content length validation (stories allow empty content)
        if (!string.IsNullOrEmpty(request.Content))
        {
            var maxChars = ValidationLimits.GetPostTextMaxChars(request.Platform);
            if (request.Content.Length > maxChars)
            {
                errors["content"] = [$"Text is too long for {request.Platform}. Max {maxChars} characters."];
            }
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateUpdatePostRequest(UpdatePostRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        var maxChars = ValidationLimits.GetPostTextMaxChars(request.Platform);
        if (request.Content?.Length > maxChars)
        {
            errors["content"] = [$"Text is too long for {request.Platform}. Max {maxChars} characters."];
        }

        return errors;
    }
}

public record CreatePostMediaItem(
    string? MediaUrl,
    MediaType MediaType,
    int Order,
    /// <summary>
    /// Preferred media reference: the Media row id returned by the upload flow. When present,
    /// the server resolves it to the internal StorageKey and ignores <see cref="MediaUrl"/>.
    /// </summary>
    Guid? MediaId = null
);

public record InstagramUserTagDto(
    string Username,
    double X,
    double Y
);

public record CreatePostRequest(
    string? Content,
    string? MediaUrl,
    MediaType? MediaType,
    Platform Platform,
    DateTime ScheduledAt,
    PostType PostType = PostType.Feed,
    Guid? TargetPageId = null,
    Guid? TargetInstagramAccountId = null,
    string? SelectedThumbnailUrl = null,
    List<Guid>? MediaAssetIds = null,
    List<CreatePostMediaItem>? MediaItems = null,
    List<InstagramUserTagDto>? InstagramUserTags = null,
    /// <summary>
    /// Per-media-item Instagram user tags for carousel posts.
    /// Key = media item order (0-based), Value = list of tags for that item.
    /// </summary>
    Dictionary<int, List<InstagramUserTagDto>>? InstagramMediaTags = null,
    /// <summary>
    /// Preferred single-media reference: the Media row id returned by the upload flow. When
    /// present, the server resolves it to the internal StorageKey server-side (scoped to the
    /// current workspace) and ignores <see cref="MediaUrl"/>. This is the only media reference
    /// new frontend code should submit — StorageKeys are never accepted directly from a trusted
    /// client.
    /// </summary>
    Guid? MediaId = null
);

public record UpdatePostRequest(
    string Content,
    string? MediaUrl,
    MediaType? MediaType,
    Platform Platform,
    DateTime ScheduledAt,
    Guid? TargetPageId = null,
    Guid? TargetInstagramAccountId = null,
    /// <summary>See <see cref="CreatePostRequest.MediaId"/>.</summary>
    Guid? MediaId = null
);

public record PaginatedResponse<T>(
    List<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages
)
{
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;
}

/// <summary>
/// Internal (non-DTO) lookup value used to enrich Post/PostMediaItem responses with a
/// frontend-safe media reference. Never serialized directly — <see cref="MediaId"/> and
/// <see cref="PreviewUrl"/> are copied onto the public DTOs; the StorageKey itself never
/// leaves the backend. Public only because it appears in <see cref="PostDto.FromEntity"/>'s
/// public signature — it is not part of the wire contract (nothing maps it to JSON) and is
/// never returned from an action directly.
/// </summary>
public sealed record MediaLookupEntry(Guid MediaId, string PreviewUrl, MediaThumbnailDto? Thumbnail);


public record PostMediaItemDto(
    Guid Id,
    int Order,
    string? MediaUrl,
    MediaType MediaType,
    MediaThumbnailDto? Thumbnail = null,
    /// <summary>Media row id backing this item, when resolvable. Never a StorageKey.</summary>
    Guid? MediaId = null
);

public record PostDto(
    Guid Id,
    string Content,
    string? MediaUrl,
    MediaType MediaType,
    PostType PostType,
    Platform Platform,
    DateTime ScheduledAt,
    PostStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? TargetPageId,
    string? TargetPageName,
    Guid? TargetInstagramAccountId,
    string? TargetInstagramAccountName,
    DateTime? PublishedAt,
    string? ExternalPostId,
    string? ExternalPostUrl,
    string? ErrorMessage,
    int RetryCount,
    int ProcessingPollCount,
    DateTime? NextRetryAt,
    string? SelectedThumbnailUrl,
    string? InstagramMediaType,
    MediaThumbnailDto? Thumbnail = null,
    List<PostMediaItemDto>? MediaItems = null,
    /// <summary>
    /// True if the post's target page/IG account is currently connected. False if it was
    /// disconnected (frontend can render a "disconnected" badge). Null if the post has no target.
    /// </summary>
    bool? TargetConnectionActive = null,
    /// <summary>Media row id backing the primary MediaUrl, when resolvable. Never a StorageKey.</summary>
    Guid? MediaId = null
)
{
    public static PostDto FromEntity(
        Post post,
        IReadOnlyDictionary<string, MediaLookupEntry>? mediaLookup = null)
    {
        bool? targetConnectionActive = post.Platform switch
        {
            Platform.Facebook  => post.TargetPage?.IsConnected,
            Platform.Instagram => post.TargetInstagramAccount?.IsConnected,
            _ => (bool?)null,
        };

        var mainMedia = ResolveMedia(mediaLookup, post.MediaUrl);

        return new(
            post.Id,
            post.Content,
            ResolveMediaUrl(mainMedia, post.MediaUrl),
            post.MediaType,
            post.PostType,
            post.Platform,
            post.ScheduledAt,
            post.Status,
            post.CreatedAt,
            post.UpdatedAt,
            post.TargetPageId,
            post.TargetPage?.Name,
            post.TargetInstagramAccountId,
            post.TargetInstagramAccount != null
                ? $"@{post.TargetInstagramAccount.Username}"
                : null,
            post.PublishedAt,
            post.ExternalPostId,
            post.ExternalPostUrl,
            post.ErrorMessage,
            post.RetryCount,
            post.ProcessingPollCount,
            post.NextRetryAt,
            post.SelectedThumbnailUrl,
            post.InstagramMediaType?.ToString(),
            mainMedia?.Thumbnail,
            post.MediaItems?.Count > 0
                ? post.MediaItems.OrderBy(m => m.Order)
                    .Select(m =>
                    {
                        var itemMedia = ResolveMedia(mediaLookup, m.MediaUrl);
                        return new PostMediaItemDto(
                            m.Id,
                            m.Order,
                            ResolveMediaUrl(itemMedia, m.MediaUrl),
                            m.MediaType,
                            itemMedia?.Thumbnail,
                            itemMedia?.MediaId);
                    })
                    .ToList()
                : null,
            targetConnectionActive,
            mainMedia?.MediaId
        );
    }

    private static MediaLookupEntry? ResolveMedia(
        IReadOnlyDictionary<string, MediaLookupEntry>? mediaLookup,
        string? storageKey)
    {
        if (mediaLookup == null || string.IsNullOrWhiteSpace(storageKey))
            return null;

        return mediaLookup.TryGetValue(storageKey, out var entry) ? entry : null;
    }

    /// <summary>
    /// Never returns a raw StorageKey. When the media reference resolves to a known Media
    /// row, returns its authenticated preview URL. Otherwise, only passes the raw value
    /// through when it does NOT look like a storage key (i.e. it is a plain external URL —
    /// nothing secret to protect); a bare, unresolvable storage key (legacy/edge case) is
    /// suppressed to null rather than ever being exposed to the frontend.
    /// </summary>
    private static string? ResolveMediaUrl(MediaLookupEntry? resolved, string? rawMediaUrl)
    {
        if (resolved != null)
            return resolved.PreviewUrl;

        return PostsController.LooksLikeStorageKey(rawMediaUrl) ? null : rawMediaUrl;
    }
}
