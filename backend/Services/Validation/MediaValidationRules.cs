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
            // Final product policy: JPG/JPEG + PNG only (no GIF/BMP/TIFF/WebP — they are
            // neither advertised in the UI nor accepted by upload init).
            AllowedMimeTypes = ["image/jpeg", "image/png"],
            MaxBytes = 10L * 1024 * 1024, // 10MB — MVP-supported limit, aligned with the current Facebook Page photo cap
            MinWidth = 320,
            MinHeight = 320,
            MaxWidth = 2048, // Recommended max (larger images are resized)
            MaxHeight = 2048,
            AspectRatioMin = 0.5625, // 9:16 (portrait)
            AspectRatioMax = 1.91, // ~1.91:1 (landscape)
            QualityWarningMinWidth = 600,
            QualityWarningMinHeight = 600,
        },

        // Facebook Feed Video Rules
        // Source: https://developers.facebook.com/docs/video-api/getting-started
        [(Platform.Facebook, Placement.Feed, MediaType.Video)] = new MediaValidationRule
        {
            // Final product policy: MP4 + MOV only. MOV (video/quicktime) stays supported for
            // iPhone compatibility, but its codec/audio must still pass when metadata is
            // available. AVI/WebM are intentionally NOT supported (no upload/publisher support).
            AllowedMimeTypes = ["video/mp4", "video/quicktime"],
            AllowedContainers = ["mp4", "mov"],
            AllowedVideoCodecs = ["h264", "hevc"],
            AllowedAudioCodecs = ["aac"],
            MaxBytes = 50L * 1024 * 1024, // 50MB — product cap for Facebook Feed video, matching the Supabase Free global upload limit
            MinWidth = 120,
            MinHeight = 120,
            MaxWidth = 4096,
            MaxHeight = 4096,
            AspectRatioMin = 0.5625, // 9:16 (portrait)
            AspectRatioMax = 1.91, // ~1.91:1 (landscape)
            DurationMinSeconds = 3, // product/MVP: small social videos only
            DurationMaxSeconds = 180, // product/MVP duration cap, NOT Meta's maximum (Meta allows hours)
            MaxFps = 60,
            RecommendedWidth = 1280,
            RecommendedHeight = 720,
        },

        // ============================================
        // FACEBOOK PAGE - STORY
        // ============================================
        // Facebook Story Image Rules
        // Facebook Story images are validated ONLY for supported type + file size. Facebook
        // Stories have NO dimension, resolution, orientation, or aspect-ratio requirements in
        // this product: a square, landscape, tiny, huge, wide, or tall image is accepted as long
        // as it is a valid JPG/PNG within the size cap. Deliberately NO Min/Max width/height, no
        // aspect-ratio range, no preferred 9:16, no quality warning, no recommended dimensions.
        [(Platform.Facebook, Placement.Story, MediaType.Image)] = new MediaValidationRule
        {
            // Final product policy: JPG/JPEG + PNG only.
            AllowedMimeTypes = ["image/jpeg", "image/png"],
            MaxBytes = 10L * 1024 * 1024, // 10MB — unchanged Facebook Story image cap
        },

        // Facebook Story Video Rules
        // Facebook Story videos are validated ONLY for container/type, file size, duration, and
        // readability. NO dimension, resolution, orientation, aspect-ratio, frame-rate, OR CODEC
        // requirements: a readable MP4/MOV within the size + duration limits is accepted whatever
        // its video/audio codec (including no audio stream, and unknown/missing codec names), and
        // Meta decides at publish time whether an uncommon codec combination is playable.
        // Codec validation is deliberately omitted (no AllowedVideoCodecs / AllowedAudioCodecs →
        // the engine skips both codec checks). FPS was likewise removed. Container + size +
        // duration + metadata readability remain fully enforced below.
        [(Platform.Facebook, Placement.Story, MediaType.Video)] = new MediaValidationRule
        {
            // Final product policy: MP4 + MOV container only (MOV for iPhone compatibility).
            AllowedMimeTypes = ["video/mp4", "video/quicktime"],
            AllowedContainers = ["mp4", "mov"],
            MaxBytes = 50L * 1024 * 1024, // 50MB (exactly 52,428,800 bytes) — Facebook Story video product cap
            DurationMinSeconds = 3, // supported duration range: Facebook Story videos are 3-90 seconds (inclusive)
            DurationMaxSeconds = 90,
        },

        // ============================================
        // INSTAGRAM - FEED
        // ============================================
        // Instagram Feed Image Rules
        // Source: https://developers.facebook.com/docs/instagram-platform/instagram-graph-api/reference/ig-user/media/
        // Meta accepts JPEG ONLY for Instagram. This rule is the NATIVE publish format; a PNG
        // upload is validated against its Instagram JPEG derivative instead (see
        // EffectiveMediaResolver), so PNG is supported end-to-end without being marked invalid
        // here. Width > 1440 is downscaled by Meta, not rejected → advisory, not a hard error.
        [(Platform.Instagram, Placement.Feed, MediaType.Image)] = new MediaValidationRule
        {
            AllowedMimeTypes = ["image/jpeg"],
            MaxBytes = 8L * 1024 * 1024, // 8MB — Instagram platform limit
            MinWidth = 320,
            MinHeight = 320,
            MaxWidth = 1440,
            MaxHeight = 2560,
            MaxWidthIsAdvisory = true, // Meta downscales > 1440px instead of rejecting
            AspectRatioMin = 0.8, // 4:5 (portrait) — Meta rejects feed IMAGES below 4:5 (9:16 is video-only)
            AspectRatioMax = 1.91, // 1.91:1 (landscape)
            QualityWarningMinWidth = 600,
            QualityWarningMinHeight = 600,
        },

        // Instagram Feed Video Rules (a single IG feed video is published by Meta as a Reel;
        // vertical 9:16 is the standard Reel format and MUST pass).
        [(Platform.Instagram, Placement.Feed, MediaType.Video)] = new MediaValidationRule
        {
            // MP4 + MOV (MOV for iPhone compatibility). H.264 or HEVC/H.265, AAC audio.
            AllowedMimeTypes = ["video/mp4", "video/quicktime"],
            AllowedContainers = ["mp4", "mov"],
            AllowedVideoCodecs = ["h264", "hevc"],
            AllowedAudioCodecs = ["aac"],
            MaxBytes = 100L * 1024 * 1024, // 100MB — product/MVP cap (Meta allows more for Reels)
            MinWidth = 500,
            MinHeight = 500,
            MaxWidth = 1920,
            MaxHeight = 1920,
            AspectRatioMin = 0.5625, // 9:16 (vertical Reel) — Meta accepts far wider; this keeps sane bounds
            AspectRatioMax = 1.91, // 1.91:1 (landscape)
            DurationMinSeconds = 3,
            DurationMaxSeconds = 180, // product/MVP duration cap, NOT Meta's maximum (Reels allow 15 min)
            MinFps = 23,
            MaxFps = 60,
            RecommendedWidth = 1080,
            RecommendedHeight = 1080,
        },

        // ============================================
        // INSTAGRAM - STORY
        // ============================================
        // Instagram Story Image Rules
        // Source: https://developers.facebook.com/docs/instagram-platform/instagram-graph-api/reference/ig-user/media
        [(Platform.Instagram, Placement.Story, MediaType.Image)] = new MediaValidationRule
        {
            // Meta accepts JPEG ONLY for Instagram. This rule is the NATIVE publish format;
            // a PNG upload is validated against its Instagram JPEG derivative instead (see
            // EffectiveMediaResolver), so PNG users are not rejected for being non-JPEG.
            AllowedMimeTypes = ["image/jpeg"],
            MaxBytes = 8L * 1024 * 1024, // 8MB — Instagram platform limit
            MinWidth = 320,
            MinHeight = 320,
            MaxWidth = 1080,
            MaxHeight = 1920,
            AspectRatioMin = 0.50,
            AspectRatioMax = 0.75,
            PreferredAspectRatio = 0.5625, // 9:16
            AspectRatioWarningTolerance = 0.02,
            QualityWarningMinWidth = 600,
            QualityWarningMinHeight = 600,
            RecommendedWidth = 1080,
            RecommendedHeight = 1920,
        },

        // Instagram Story Video Rules
        [(Platform.Instagram, Placement.Story, MediaType.Video)] = new MediaValidationRule
        {
            // MP4 + MOV (MOV for iPhone compatibility). H.264 or HEVC/H.265, AAC audio.
            AllowedMimeTypes = ["video/mp4", "video/quicktime"],
            AllowedContainers = ["mp4", "mov"],
            AllowedVideoCodecs = ["h264", "hevc"],
            AllowedAudioCodecs = ["aac"],
            MaxBytes = 100L * 1024 * 1024, // 100MB — Instagram platform limit for story videos
            MinWidth = 320,
            MinHeight = 320,
            MaxWidth = 1080,
            MaxHeight = 1920,
            AspectRatioMin = 0.50,
            AspectRatioMax = 0.75,
            PreferredAspectRatio = 0.5625, // 9:16
            AspectRatioWarningTolerance = 0.02,
            DurationMinSeconds = 3, // Meta/platform limit: story videos are 3-60 seconds
            DurationMaxSeconds = 60,
            MinFps = 23,
            MaxFps = 60,
            RecommendedWidth = 1080,
            RecommendedHeight = 1920,
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

    // Dimension constraints. Null means "no dimension rule for this combination": the engine
    // skips the corresponding min/max check entirely (e.g. Facebook Story media, which has no
    // dimension, resolution, or orientation requirements). Non-null values are enforced as hard
    // limits (unless MaxWidthIsAdvisory downgrades the max to a warning).
    public int? MinWidth { get; init; }
    public int? MinHeight { get; init; }
    public int? MaxWidth { get; init; }
    public int? MaxHeight { get; init; }

    /// <summary>
    /// When true, exceeding <see cref="MaxWidth"/> produces a WARNING instead of a
    /// hard error, because the platform downscales oversized images rather than
    /// rejecting them (Instagram scales any width &gt; 1440px down to 1440px).
    /// Default false: most platforms reject oversized media outright.
    /// </summary>
    public bool MaxWidthIsAdvisory { get; init; }

    // Aspect ratio constraints (width / height). Null Min/Max means "no aspect-ratio rule": the
    // engine skips the range check AND any preferred-ratio warning (e.g. Facebook Story media,
    // which has no aspect-ratio requirement).
    public double? AspectRatioMin { get; init; }
    public double? AspectRatioMax { get; init; }
    public double? PreferredAspectRatio { get; init; }
    public double? AspectRatioWarningTolerance { get; init; }

    // Video-specific constraints
    public double? DurationMinSeconds { get; init; }
    public double? DurationMaxSeconds { get; init; }
    public int? MinFps { get; init; }
    public int? MaxFps { get; init; }

    // Recommendations (for warnings, not errors)
    public int? RecommendedWidth { get; init; }
    public int? RecommendedHeight { get; init; }
    public int? QualityWarningMinWidth { get; init; }
    public int? QualityWarningMinHeight { get; init; }
}
