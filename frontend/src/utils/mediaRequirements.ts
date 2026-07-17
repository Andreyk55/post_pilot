import {
  getClientValidationRule,
  type Placement,
  type MediaType,
} from '../constants/mediaValidationRules'
import type { PlatformId } from '../constants/validationLimits'

/**
 * Single source of truth for the user-facing media requirement + pre-validation copy
 * shown across the Schedule Post composer. Kept platform-agnostic by design: the rules
 * differ per platform/placement, but the *wording style* must be identical for
 * Facebook and Instagram (see SchedulePost / MediaUpload / MultiMediaUpload consumers).
 *
 * The friendly resolvers below mirror the decisions made by `preValidateFile` /
 * `preValidateImageDimensions` (same rule table via `getClientValidationRule`) but
 * return human copy instead of the technical strings — e.g. "Story media should be
 * vertical 9:16." rather than "Aspect ratio (1.33) is outside allowed range
 * (0.56 to 0.56)". The technical functions are left intact as the lower-level check.
 */

const isStory = (placement: Placement | string): boolean =>
  String(placement).toLowerCase() === 'story'

/** 'facebook' → 'Facebook' — display name for requirement/error copy. */
function platformLabel(platform: PlatformId | string | null | undefined): string | null {
  switch (String(platform ?? '').toLowerCase()) {
    case 'facebook': return 'Facebook'
    case 'instagram': return 'Instagram'
    case 'twitter': return 'Twitter'
    case 'linkedin': return 'LinkedIn'
    default: return null
  }
}

// Generic fallbacks for when no platform-specific rule exists (e.g. no platform yet
// selected). Mirror the backend Feed defaults so the concise hint still reads sensibly.
const DEFAULT_IMAGE_MAX_BYTES = 10 * 1024 * 1024
const DEFAULT_VIDEO_MIN_SECONDS = 3
const DEFAULT_VIDEO_MAX_SECONDS = 180

/**
 * The concise "what's supported here" line shown once above the upload area. Same
 * component/styling for every platform + placement; the image size limit and video
 * duration range are still derived from the mirrored rule table so Facebook (10 MB),
 * Instagram (8 MB), Feed (3–180 s), Facebook Story (3–90 s) and Instagram Story
 * (3–60 s) each read accurately.
 */
export function getMediaRequirementHint(
  platform: PlatformId | string | null | undefined,
  placement: Placement | string = 'Feed',
): string {
  const imageRule = platform ? getClientValidationRule(platform, placement, 'Image') : null
  const videoRule = platform ? getClientValidationRule(platform, placement, 'Video') : null

  const imageMaxMB = Math.round((imageRule?.maxBytes ?? DEFAULT_IMAGE_MAX_BYTES) / (1024 * 1024))
  const videoMinSeconds = videoRule?.durationMinSeconds ?? DEFAULT_VIDEO_MIN_SECONDS
  const videoMaxSeconds = videoRule?.durationMaxSeconds ?? DEFAULT_VIDEO_MAX_SECONDS

  // Facebook and Instagram are the user-facing upload platforms whose hints name the video
  // size cap. The no-platform fallback keeps duration-only wording until a destination exists.
  const platformKey = String(platform ?? '').toLowerCase()
  const shouldShowVideoSize = platformKey === 'facebook' || platformKey === 'instagram'
  const videoSizePrefix = shouldShowVideoSize && videoRule
    ? `≤${Math.round(videoRule.maxBytes / (1024 * 1024))} MB, `
    : ''

  return `Supported: JPG/PNG images (≤${imageMaxMB} MB) • MP4/MOV videos (${videoSizePrefix}${videoMinSeconds}–${videoMaxSeconds} s)`
}

const MIME_LABELS: Record<string, string> = {
  'image/jpeg': 'JPG',
  'image/png': 'PNG',
  'image/gif': 'GIF',
  'image/bmp': 'BMP',
  'image/tiff': 'TIFF',
  'image/webp': 'WebP',
  'video/mp4': 'MP4',
  'video/quicktime': 'MOV',
  'video/x-msvideo': 'AVI',
  'video/webm': 'WebM',
}

/** "JPG", "JPG or PNG", "JPG, PNG or GIF" — de-duplicated, human-joined. */
function formatTypeList(mimeTypes: string[]): string {
  const labels = Array.from(
    new Set(mimeTypes.map(t => MIME_LABELS[t.toLowerCase()] ?? t.toUpperCase())),
  )
  if (labels.length <= 1) return labels[0] ?? ''
  return `${labels.slice(0, -1).join(', ')} or ${labels[labels.length - 1]}`
}

/** "8MB", "50MB", "1GB" — whole GB when evenly divisible, else MB. */
function formatSizeLimit(maxBytes: number): string {
  const mb = maxBytes / (1024 * 1024)
  if (mb >= 1024 && Number.isInteger(mb / 1024)) return `${mb / 1024}GB`
  return `${Math.round(mb)}MB`
}

function getMediaTypeFromMime(type: string): MediaType | null {
  if (type.startsWith('image/')) return 'Image'
  if (type.startsWith('video/')) return 'Video'
  return null
}

/** HEIC/HEIF (iPhone default capture) — detected by MIME or extension since browsers
 * sometimes report an empty MIME type for HEIC files. */
function isHeicFile(file: { type: string; name?: string }): boolean {
  const type = file.type.toLowerCase()
  if (type.includes('heic') || type.includes('heif')) return true
  const name = (file.name ?? '').toLowerCase()
  return name.endsWith('.heic') || name.endsWith('.heif')
}

export const HEIC_NOT_SUPPORTED_MESSAGE = 'HEIC is not supported yet. Please upload a JPG or PNG image.'

/**
 * Friendly client-side type/size pre-validation message for a file, or null when it
 * passes (or no client rule exists for the combination — backend then decides).
 * Mirrors the order of checks in `preValidateFile`: type, then size.
 */
export function resolveClientMediaError(
  file: { type: string; size: number; name?: string },
  platform: PlatformId | string,
  placement: Placement | string = 'Feed',
): string | null {
  // HEIC first: it is the most common "why won't my phone photo upload" case and
  // deserves its own copy (product limitation — no auto-conversion in the MVP).
  if (isHeicFile(file)) return HEIC_NOT_SUPPORTED_MESSAGE

  const mediaType = getMediaTypeFromMime(file.type)
  if (!mediaType) return 'Unsupported file type. Upload a photo or video.'

  const rule = getClientValidationRule(platform, placement, mediaType)
  if (!rule) return null

  if (!rule.allowedMimeTypes.includes(file.type.toLowerCase())) {
    return mediaType === 'Image'
      ? `Images must be ${formatTypeList(rule.allowedMimeTypes)}.`
      : `Videos must be ${formatTypeList(rule.allowedMimeTypes)}.`
  }

  if (file.size > rule.maxBytes) {
    const label = platformLabel(platform)
    return mediaType === 'Image'
      ? `This image is too large. ${label ?? 'Supported'} images can be up to ${formatSizeLimit(rule.maxBytes)}. Large phone photos may need to be resized before upload.`
      : `This video is too large. ${label ?? 'Supported'} videos can be up to ${formatSizeLimit(rule.maxBytes)}.`
  }

  return null
}

/** 0.8 → "4:5", 0.5625 → "9:16" — familiar social labels for ratio bounds (mirrors
 * the backend's FormatRatio so client and server copy read identically). */
function formatRatio(ratio: number): string {
  if (ratio === 0.5625) return '9:16'
  if (ratio === 0.75) return '3:4'
  if (ratio === 0.8) return '4:5'
  if (ratio === 1) return '1:1'
  return `${ratio.toFixed(2)}:1`
}

/**
 * Friendly client-side image dimension/aspect pre-validation message, or null when it
 * passes. Mirrors `preValidateImageDimensions` (min, max, aspect) but with human copy
 * — the Story 9:16 rule gets a dedicated, recognizable message.
 */
export function resolveClientDimensionError(
  width: number,
  height: number,
  platform: PlatformId | string,
  placement: Placement | string = 'Feed',
): string | null {
  const rule = getClientValidationRule(platform, placement, 'Image')
  if (!rule) return null

  // Each check is skipped when the rule omits that bound, so a placement with no dimension/aspect
  // requirement (e.g. Facebook Story) returns null for any size or shape.

  if (rule.minWidth != null && rule.minHeight != null && (width < rule.minWidth || height < rule.minHeight)) {
    return `Image is too small. Use at least ${rule.minWidth}×${rule.minHeight}px.`
  }

  if (!rule.maxWidthIsAdvisory && rule.maxWidth != null && rule.maxHeight != null && (width > rule.maxWidth || height > rule.maxHeight)) {
    return `Image is too large. Maximum ${rule.maxWidth}×${rule.maxHeight}px.`
  }

  if (rule.aspectRatioMin != null && rule.aspectRatioMax != null) {
    const aspectRatio = width / height
    if (aspectRatio < rule.aspectRatioMin || aspectRatio > rule.aspectRatioMax) {
      if (isStory(placement)) {
        return 'Story media should be vertical 9:16.'
      }
      const label = platformLabel(platform)
      return `${label ? `${label} Feed` : 'Feed'} images must use an aspect ratio between ${formatRatio(rule.aspectRatioMin)} and ${formatRatio(rule.aspectRatioMax)}.`
    }
  }

  return null
}
