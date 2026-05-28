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
    private readonly ILogger<PostsController> _logger;

    public PostsController(
        AppDbContext context,
        IPostScheduler scheduler,
        IFacebookInsightsService facebookInsights,
        ICurrentWorkspaceProvider currentWorkspace,
        ILogger<PostsController> logger)
    {
        _context = context;
        _scheduler = scheduler;
        _facebookInsights = facebookInsights;
        _currentWorkspace = currentWorkspace;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<PaginatedResponse<PostDto>>> GetPosts(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] PostStatus? status = null,
        [FromQuery] PostType? postType = null)
    {
        // Ensure valid pagination parameters
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);

        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        var query = _context.Posts.Where(p => p.WorkspaceId == workspaceId);

        if (status.HasValue)
        {
            query = query.Where(p => p.Status == status.Value);
        }

        if (postType.HasValue)
        {
            query = query.Where(p => p.PostType == postType.Value);
        }

        // Only show posts whose target page/IG account AND its parent MetaConnection
        // are currently connected. Disconnected rows survive in the DB for audit/history
        // but are not surfaced through this list endpoint.
        query = query.Where(p =>
            (p.Platform == Platform.Facebook
                && p.TargetPage != null
                && p.TargetPage.IsConnected
                && (p.TargetPage.MetaConnection == null || p.TargetPage.MetaConnection.IsConnected))
            || (p.Platform == Platform.Instagram
                && p.TargetInstagramAccount != null
                && p.TargetInstagramAccount.IsConnected
                && (p.TargetInstagramAccount.MetaConnection == null || p.TargetInstagramAccount.MetaConnection.IsConnected)));

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var posts = await query
            .Include(p => p.TargetPage)
            .Include(p => p.TargetInstagramAccount)
            .Include(p => p.MediaItems)
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => PostDto.FromEntity(p))
            .ToListAsync();

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

        return PostDto.FromEntity(post);
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

        return new PostDetailsDto(
            Id: post.Id,
            Content: post.Content,
            MediaUrl: post.MediaUrl,
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
            MediaItems: post.MediaItems?.Count > 0
                ? post.MediaItems.OrderBy(m => m.Order)
                    .Select(m => new PostDetailsMediaItemDto(m.Id, m.Order, m.MediaUrl, m.MediaType.ToString()))
                    .ToList()
                : null,
            TargetConnectionActive: post.Platform switch
            {
                Platform.Facebook  => post.TargetPage?.IsConnected,
                Platform.Instagram => post.TargetInstagramAccount?.IsConnected,
                _ => (bool?)null,
            }
        );
    }

    [HttpPost]
    public async Task<ActionResult<PostDto>> CreatePost(CreatePostRequest request)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        var validationErrors = ValidateCreatePostRequest(request);
        if (validationErrors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(validationErrors));
        }

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

        // Note: Media validation is done client-side via POST /api/media/validate before submission.
        // The frontend blocks submission if media is invalid.

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

        return CreatedAtAction(nameof(GetPost), new { id = post.Id }, PostDto.FromEntity(post));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdatePost(Guid id, UpdatePostRequest request)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        var post = await _context.Posts.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == workspaceId);

        if (post == null)
        {
            return NotFound();
        }

        // Only allow updates to scheduled posts
        if (post.Status != PostStatus.Scheduled)
        {
            return BadRequest(new { error = "Cannot update a post that is not scheduled" });
        }

        var validationErrors = ValidateUpdatePostRequest(request);
        if (validationErrors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(validationErrors));
        }

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

            if (result.Success)
            {
                return Ok(PostDto.FromEntity(post));
            }
            else
            {
                // Publishing failed — return the error but don't 500
                return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
                {
                    Title = "Publishing failed",
                    Detail = result.ErrorMessage ?? "An error occurred while publishing to the platform.",
                    Status = StatusCodes.Status502BadGateway,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during publish-now for post {PostId}", post.Id);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Publishing failed",
                Detail = ex.Message,
                Status = StatusCodes.Status502BadGateway,
            });
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
    string MediaUrl,
    MediaType MediaType,
    int Order
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
    Dictionary<int, List<InstagramUserTagDto>>? InstagramMediaTags = null
);

public record UpdatePostRequest(
    string Content,
    string? MediaUrl,
    MediaType? MediaType,
    Platform Platform,
    DateTime ScheduledAt,
    Guid? TargetPageId = null,
    Guid? TargetInstagramAccountId = null
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

public record PostMediaItemDto(
    Guid Id,
    int Order,
    string MediaUrl,
    MediaType MediaType
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
    List<PostMediaItemDto>? MediaItems = null,
    /// <summary>
    /// True if the post's target page/IG account is currently connected. False if it was
    /// disconnected (frontend can render a "disconnected" badge). Null if the post has no target.
    /// </summary>
    bool? TargetConnectionActive = null
)
{
    public static PostDto FromEntity(Post post)
    {
        bool? targetConnectionActive = post.Platform switch
        {
            Platform.Facebook  => post.TargetPage?.IsConnected,
            Platform.Instagram => post.TargetInstagramAccount?.IsConnected,
            _ => (bool?)null,
        };

        return new(
            post.Id,
            post.Content,
            post.MediaUrl,
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
            post.MediaItems?.Count > 0
                ? post.MediaItems.OrderBy(m => m.Order).Select(m => new PostMediaItemDto(m.Id, m.Order, m.MediaUrl, m.MediaType)).ToList()
                : null,
            targetConnectionActive
        );
    }
}
