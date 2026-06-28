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

const isReel = (placement: Placement | string): boolean =>
  String(placement).toLowerCase() === 'reel'

/**
 * The "what's allowed here" line shown before upload. Placement-driven and identical
 * wording for every platform, so Facebook Story and Instagram Story read the same.
 */
export function getMediaRequirementHint(
  _platform: PlatformId | string | null | undefined,
  placement: Placement | string = 'Feed',
): string {
  if (isStory(placement)) return '1 photo or 1 video — vertical 9:16 recommended'
  if (isReel(placement)) return '1 video — vertical 9:16 recommended'
  return 'Photo or video supported'
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

/** "8MB", "100MB", "1GB" — whole GB when evenly divisible, else MB. */
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

/**
 * Friendly client-side type/size pre-validation message for a file, or null when it
 * passes (or no client rule exists for the combination — backend then decides).
 * Mirrors the order of checks in `preValidateFile`: type, then size.
 */
export function resolveClientMediaError(
  file: { type: string; size: number },
  platform: PlatformId | string,
  placement: Placement | string = 'Feed',
): string | null {
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
    return mediaType === 'Image'
      ? `Image is larger than ${formatSizeLimit(rule.maxBytes)}.`
      : `Video is larger than ${formatSizeLimit(rule.maxBytes)}.`
  }

  return null
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

  if (width < rule.minWidth || height < rule.minHeight) {
    return `Image is too small. Use at least ${rule.minWidth}×${rule.minHeight}px.`
  }

  if (!rule.maxWidthIsAdvisory && (width > rule.maxWidth || height > rule.maxHeight)) {
    return `Image is too large. Maximum ${rule.maxWidth}×${rule.maxHeight}px.`
  }

  const aspectRatio = width / height
  if (aspectRatio < rule.aspectRatioMin || aspectRatio > rule.aspectRatioMax) {
    if (isStory(placement) || isReel(placement)) {
      return 'Story media should be vertical 9:16.'
    }
    return `Image aspect ratio is not supported (use between ${rule.aspectRatioMin.toFixed(2)} and ${rule.aspectRatioMax.toFixed(2)}).`
  }

  return null
}
