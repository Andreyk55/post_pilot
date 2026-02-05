using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Validation;

/// <summary>
/// Platform and placement-specific media validation rules.
/// This is the single source of truth for all media validation constraints.
/// </summary>
public static class MediaValidationRules
{
    /// <summary>
    /// Gets the validation rules for a specific platform, placement, and media type combination.
    /// Returns null if no rules are defined for the combination.
    /// </summary>
    public static MediaValidationRule? GetRules(Platform platform, Placement placement, MediaType mediaType)
    {
        var key = (platform, placement, mediaType);
        return Rules.TryGetValue(key, out var rule) ? rule : null;
    }

    /// <summary>
    /// Checks if rules exist for a specific combination.
    /// </summary>
    public static bool HasRules(Platform platform, Placement placement, MediaType mediaType)
    {
        return Rules.ContainsKey((platform, placement, mediaType));
    }

    /// <summary>
    /// All validation rules keyed by (Platform, Placement, MediaType).
    /// </summary>
    private static readonly Dictionary<(Platform, Placement, MediaType), MediaValidationRule> Rules = new()
    {
        // ============================================
        // FACEBOOK PAGE - FEED
        // ============================================
        // Facebook Feed Image Rules
        // Source: https://developers.facebook.com/docs/graph-api/reference/page/photos/
        [(Platform.Facebook, Placement.Feed, MediaType.Image)] = new MediaValidationRule
        {
            AllowedMimeTypes = ["image/jpeg", "image/png", "image/gif", "image/bmp", "image/tiff", "image/webp"],
            MaxBytes = 4L * 1024 * 1024, // 4MB (Facebook limit)
            MinWidth = 320,
            MinHeight = 320,
            MaxWidth = 2048, // Recommended max (larger images are resized)
            MaxHeight = 2048,
            AspectRatioMin = 0.5625, // 9:16 (portrait)
            AspectRatioMax = 1.91, // ~1.91:1 (landscape)
            RecommendedWidth = 1200,
            RecommendedHeight = 630,
        },

        // Facebook Feed Video Rules
        // Source: https://developers.facebook.com/docs/video-api/getting-started
        [(Platform.Facebook, Placement.Feed, MediaType.Video)] = new MediaValidationRule
        {
            AllowedMimeTypes = ["video/mp4", "video/quicktime", "video/x-msvideo", "video/webm"],
            AllowedContainers = ["mp4", "mov", "avi", "webm"],
            AllowedVideoCodecs = ["h264", "hevc", "vp8", "vp9"],
            AllowedAudioCodecs = ["aac", "mp3", "vorbis", "opus"],
            MaxBytes = 1024L * 1024 * 1024, // 1GB (Facebook limit for API uploads)
            MinWidth = 120,
            MinHeight = 120,
            MaxWidth = 4096,
            MaxHeight = 4096,
            AspectRatioMin = 0.5625, // 9:16 (portrait)
            AspectRatioMax = 1.91, // ~1.91:1 (landscape)
            DurationMinSeconds = 1,
            DurationMaxSeconds = 240 * 60, // 240 minutes (4 hours)
            MaxFps = 60,
            RecommendedWidth = 1280,
            RecommendedHeight = 720,
        },

        // ============================================
        // INSTAGRAM - FEED (for future implementation)
        // ============================================
        // Instagram Feed Image Rules
        [(Platform.Instagram, Placement.Feed, MediaType.Image)] = new MediaValidationRule
        {
            AllowedMimeTypes = ["image/jpeg", "image/png"],
            MaxBytes = 8L * 1024 * 1024, // 8MB
            MinWidth = 320,
            MinHeight = 320,
            MaxWidth = 1440,
            MaxHeight = 1440,
            AspectRatioMin = 0.8, // 4:5 (portrait)
            AspectRatioMax = 1.91, // 1.91:1 (landscape)
            RecommendedWidth = 1080,
            RecommendedHeight = 1080,
        },

        // Instagram Feed Video Rules
        [(Platform.Instagram, Placement.Feed, MediaType.Video)] = new MediaValidationRule
        {
            AllowedMimeTypes = ["video/mp4", "video/quicktime"],
            AllowedContainers = ["mp4", "mov"],
            AllowedVideoCodecs = ["h264"],
            AllowedAudioCodecs = ["aac"],
            MaxBytes = 100L * 1024 * 1024, // 100MB for feed videos
            MinWidth = 500,
            MinHeight = 500,
            MaxWidth = 1920,
            MaxHeight = 1920,
            AspectRatioMin = 0.8, // 4:5 (portrait)
            AspectRatioMax = 1.91, // 1.91:1 (landscape)
            DurationMinSeconds = 3,
            DurationMaxSeconds = 60, // 60 seconds for feed videos
            MinFps = 23,
            MaxFps = 60,
            RecommendedWidth = 1080,
            RecommendedHeight = 1080,
        },

        // ============================================
        // TWITTER/X - FEED (for future implementation)
        // ============================================
        [(Platform.Twitter, Placement.Feed, MediaType.Image)] = new MediaValidationRule
        {
            AllowedMimeTypes = ["image/jpeg", "image/png", "image/gif", "image/webp"],
            MaxBytes = 5L * 1024 * 1024, // 5MB (15MB for GIFs)
            MinWidth = 100,
            MinHeight = 100,
            MaxWidth = 4096,
            MaxHeight = 4096,
            AspectRatioMin = 0.5, // 1:2
            AspectRatioMax = 3.0, // 3:1
            RecommendedWidth = 1200,
            RecommendedHeight = 675,
        },

        [(Platform.Twitter, Placement.Feed, MediaType.Video)] = new MediaValidationRule
        {
            AllowedMimeTypes = ["video/mp4"],
            AllowedContainers = ["mp4"],
            AllowedVideoCodecs = ["h264"],
            AllowedAudioCodecs = ["aac"],
            MaxBytes = 512L * 1024 * 1024, // 512MB
            MinWidth = 32,
            MinHeight = 32,
            MaxWidth = 1920,
            MaxHeight = 1200,
            AspectRatioMin = 0.5, // 1:2
            AspectRatioMax = 2.0, // 2:1
            DurationMinSeconds = 0.5,
            DurationMaxSeconds = 140, // 2 minutes 20 seconds
            MinFps = 25,
            MaxFps = 60,
            RecommendedWidth = 1280,
            RecommendedHeight = 720,
        },

        // ============================================
        // LINKEDIN - FEED (for future implementation)
        // ============================================
        [(Platform.LinkedIn, Placement.Feed, MediaType.Image)] = new MediaValidationRule
        {
            AllowedMimeTypes = ["image/jpeg", "image/png", "image/gif"],
            MaxBytes = 8L * 1024 * 1024, // 8MB
            MinWidth = 276,
            MinHeight = 276,
            MaxWidth = 4320, // 36MP max
            MaxHeight = 4320,
            AspectRatioMin = 0.57, // ~9:16
            AspectRatioMax = 3.0, // 3:1
            RecommendedWidth = 1200,
            RecommendedHeight = 627,
        },

        [(Platform.LinkedIn, Placement.Feed, MediaType.Video)] = new MediaValidationRule
        {
            AllowedMimeTypes = ["video/mp4", "video/quicktime", "video/x-msvideo"],
            AllowedContainers = ["mp4", "mov", "avi"],
            AllowedVideoCodecs = ["h264"],
            AllowedAudioCodecs = ["aac", "mp3"],
            MaxBytes = 200L * 1024 * 1024, // 200MB for standard, 5GB for Premium
            MinWidth = 256,
            MinHeight = 144,
            MaxWidth = 4096,
            MaxHeight = 2304,
            AspectRatioMin = 0.5625, // 9:16
            AspectRatioMax = 2.4, // 2.4:1
            DurationMinSeconds = 3,
            DurationMaxSeconds = 600, // 10 minutes (30 minutes for some accounts)
            MinFps = 10,
            MaxFps = 60,
            RecommendedWidth = 1920,
            RecommendedHeight = 1080,
        },
    };
}

/// <summary>
/// Represents validation rules for a specific platform/placement/media type combination.
/// </summary>
public class MediaValidationRule
{
    // File type constraints
    public required string[] AllowedMimeTypes { get; init; }
    public string[]? AllowedContainers { get; init; } // For videos: mp4, mov, etc.
    public string[]? AllowedVideoCodecs { get; init; } // h264, hevc, vp9, etc.
    public string[]? AllowedAudioCodecs { get; init; } // aac, mp3, etc.

    // Size constraints
    public long MaxBytes { get; init; }

    // Dimension constraints
    public int MinWidth { get; init; }
    public int MinHeight { get; init; }
    public int MaxWidth { get; init; }
    public int MaxHeight { get; init; }

    // Aspect ratio constraints (width / height)
    public double AspectRatioMin { get; init; }
    public double AspectRatioMax { get; init; }

    // Video-specific constraints
    public double? DurationMinSeconds { get; init; }
    public double? DurationMaxSeconds { get; init; }
    public int? MinFps { get; init; }
    public int? MaxFps { get; init; }

    // Recommendations (for warnings, not errors)
    public int? RecommendedWidth { get; init; }
    public int? RecommendedHeight { get; init; }
}
