/**
 * Instagram media selection validation.
 *
 * Rules:
 * - Single photo (JPG only): allowed
 * - Single video (MP4): allowed (published as Reel)
 * - Carousel (images): 2–10 images only (JPG only)
 * - Carousel (videos): 2–10 videos only (MP4)
 * - Carousel (mixed): 2–10 items mixing images + videos (IG only)
 *
 * NOTE: Meta accepts JPEG ONLY for Instagram images. As of Phase 3 the backend
 * auto-converts PNG uploads to an Instagram-safe JPEG derivative at upload time, so
 * PNG is allowed here. WebP is still NOT supported for Instagram and is rejected up
 * front. (Original PNG is preserved for Facebook/preview; Instagram uses the JPEG.)
 */

import {
  getClientValidationRule,
  MediaValidationErrorCodes,
  type MediaValidationError,
  type MediaValidationWarning,
  type ValidationStatus,
} from '../constants/mediaValidationRules'

export interface MediaFileInfo {
  name: string
  type: string // MIME type
}

export interface InstagramSelectionResult {
  ok: boolean
  errorMessage: string | null
  /** The files to keep after validation (existing + accepted new files) */
  nextFiles: MediaFileInfo[]
}

// JPEG and PNG are accepted for Instagram. PNG is auto-converted to an
// Instagram-safe JPEG by the backend at upload time. WebP remains unsupported.
const IMAGE_TYPES = ['image/jpeg', 'image/png']
// Final product policy: MP4 + MOV only (MOV for iPhone compatibility).
const VIDEO_TYPES = ['video/mp4', 'video/quicktime']

/**
 * User-facing copy explaining Instagram image handling. Shown as a hint near the
 * uploader so users know PNG is converted and WebP is not yet supported.
 */
export const INSTAGRAM_IMAGE_FORMAT_HINT =
  'Instagram requires JPEG. PNG images will be converted automatically. WebP and HEIC are not supported yet.'

export function isImageFile(file: MediaFileInfo): boolean {
  return IMAGE_TYPES.includes(file.type.toLowerCase())
}

export function isVideoFile(file: MediaFileInfo): boolean {
  return VIDEO_TYPES.includes(file.type.toLowerCase())
}

/**
 * Validates a new file selection against existing files for Instagram.
 * Returns whether the selection is valid, an error message if not,
 * and the resulting file list.
 *
 * Mixed image+video carousels are now allowed (2-10 items).
 */
export function validateInstagramSelection(
  existingFiles: MediaFileInfo[],
  newFiles: MediaFileInfo[]
): InstagramSelectionResult {
  if (newFiles.length === 0) {
    return { ok: true, errorMessage: null, nextFiles: [...existingFiles] }
  }

  // Check for unsupported file types (HEIC gets dedicated copy — it is the most
  // common phone-photo format and a known product limitation, not a corrupt file).
  const unsupported = newFiles.filter(f => !isImageFile(f) && !isVideoFile(f))
  if (unsupported.length > 0) {
    const first = unsupported[0]
    const isHeic = /heic|heif/.test(first.type.toLowerCase()) || /\.heic$|\.heif$/.test(first.name.toLowerCase())
    return {
      ok: false,
      errorMessage: isHeic
        ? 'HEIC is not supported yet. Please upload a JPG or PNG image.'
        : `Unsupported file type: ${first.name}. Instagram accepts JPG, PNG (auto-converted to JPEG), MP4, or MOV. WebP is not supported.`,
      nextFiles: [...existingFiles],
    }
  }

  // Mixed media is now allowed for Instagram carousels; just enforce max 10 total
  const totalCount = existingFiles.length + newFiles.length
  if (totalCount > 10) {
    const remaining = 10 - existingFiles.length
    if (remaining <= 0) {
      return {
        ok: false,
        errorMessage: 'Maximum 10 items for carousel. Remove some items first.',
        nextFiles: [...existingFiles],
      }
    }
    return {
      ok: true,
      errorMessage: `Only ${remaining} more item(s) can be added. Max 10 total.`,
      nextFiles: [...existingFiles, ...newFiles.slice(0, remaining)],
    }
  }

  return {
    ok: true,
    errorMessage: null,
    nextFiles: [...existingFiles, ...newFiles],
  }
}

/** Describes the current IG media state for UI labeling */
export type InstagramMediaMode = 'empty' | 'single_image' | 'single_video' | 'carousel' | 'carousel_videos' | 'carousel_mixed'

export function getInstagramMediaMode(files: MediaFileInfo[]): InstagramMediaMode {
  if (files.length === 0) return 'empty'
  if (files.length === 1 && isVideoFile(files[0])) return 'single_video'
  if (files.length === 1 && isImageFile(files[0])) return 'single_image'
  const hasImages = files.some(f => isImageFile(f))
  const hasVideos = files.some(f => isVideoFile(f))
  if (hasImages && hasVideos) return 'carousel_mixed'
  if (files.every(f => isVideoFile(f))) return 'carousel_videos'
  return 'carousel'
}

/** Dynamic uploader label text */
export function getInstagramUploaderLabel(mode: InstagramMediaMode, count: number): string {
  switch (mode) {
    case 'empty': return 'Add photo or video'
    case 'single_video': return 'Reel selected'
    case 'single_image': return '1 photo selected'
    case 'carousel': return `${count} photos selected (carousel)`
    case 'carousel_videos': return `${count} videos selected (carousel)`
    case 'carousel_mixed': return `${count} items selected (mixed carousel)`
  }
}

/** Dynamic format hint text */
export function getInstagramFormatHint(mode: InstagramMediaMode): string {
  // PNG is auto-converted to JPEG by the backend; WebP/HEIC are not supported yet.
  switch (mode) {
    case 'empty': return 'Photos: JPG/PNG up to 8MB. Videos: MP4/MOV, 3–180 seconds (3–60 seconds in a carousel). PNG is converted to JPEG; WebP and HEIC are not supported yet.'
    case 'single_video': return 'Video (MP4/MOV, 3–180 seconds) - add more for carousel'
    case 'single_image': return 'Photo (JPG/PNG, PNG auto-converted) - add more for carousel'
    case 'carousel': return 'Carousel photos (JPG/PNG, PNG auto-converted) - videos also accepted'
    case 'carousel_videos': return 'Carousel videos (MP4/MOV, 3–60 seconds) - photos also accepted'
    case 'carousel_mixed': return 'Mixed carousel (photos + videos, videos 3–60 seconds). PNG photos auto-converted to JPEG.'
  }
}

// ────────────────────────────────────────────────────────────────────────────
//  Count-dependent Instagram Feed video duration revalidation
//
//  Instagram Feed video duration limits depend on the TOTAL media count:
//    • 1 item      → single Feed post → video allowed 3–180 s
//    • 2+ items    → Feed carousel    → every video item allowed 3–60 s
//  The backend enforces this authoritatively (deriving carousel state from the item count).
//  These helpers mirror that rule on the client so the per-item validation state stays correct
//  the instant the collection changes — without re-uploading or re-extracting metadata. The
//  numbers (3 / 60 / 180) come from the shared rule mirror, never hardcoded here.
// ────────────────────────────────────────────────────────────────────────────

export interface InstagramFeedVideoDurationResult {
  valid: boolean
  /** DURATION_TOO_SHORT / DURATION_TOO_LONG when invalid, else null. Matches backend codes. */
  code: string | null
  message: string | null
}

/**
 * Validates one Instagram Feed video's duration against the rule that applies for the resulting
 * media count. Single (isCarousel === false) uses 3–180 s; a carousel (isCarousel === true) caps
 * every video item at 3–60 s. Bounds are read from the shared rule mirror via
 * getClientValidationRule so the two never drift and no duration number is duplicated here.
 * Boundary behavior mirrors the backend exactly: reject only `duration < min` or `duration > max`
 * (so 3 s, 60 s and 180 s are inclusive-valid).
 */
export function validateInstagramFeedVideoDuration(
  durationSeconds: number,
  isCarousel: boolean,
): InstagramFeedVideoDurationResult {
  const rule = getClientValidationRule('instagram', 'Feed', 'Video', { carousel: isCarousel })
  const min = rule?.durationMinSeconds
  const max = rule?.durationMaxSeconds
  if (min == null || max == null) return { valid: true, code: null, message: null }

  // The same range copy covers too-short and too-long (the fix is identical: pick a video inside
  // the range). Carousel copy matches the backend's carousel-specific message verbatim.
  const message = isCarousel
    ? `Videos in an Instagram Feed carousel must be between ${min} and ${max} seconds.`
    : `Instagram Feed videos must be between ${min} and ${max} seconds.`

  if (durationSeconds < min) return { valid: false, code: MediaValidationErrorCodes.DurationTooShort, message }
  if (durationSeconds > max) return { valid: false, code: MediaValidationErrorCodes.DurationTooLong, message }
  return { valid: true, code: null, message: null }
}

/** The two duration error codes this revalidation owns; everything else is preserved untouched. */
const DURATION_ERROR_CODES: readonly string[] = [
  MediaValidationErrorCodes.DurationTooShort,
  MediaValidationErrorCodes.DurationTooLong,
]

/**
 * Minimal media-item shape the revalidation reads/updates. The uploader's UploadedMediaItem
 * satisfies this, and the generic signature preserves every other field (id, previews, upload
 * state, mediaId) untouched.
 */
export interface RevalidatableMediaItem {
  mediaType: string
  /** Extracted at upload time from the validation response; null when unknown (not revalidated). */
  durationSeconds?: number | null
  validationStatus: ValidationStatus
  validationErrors: MediaValidationError[]
  validationWarnings: MediaValidationWarning[]
}

function deriveStatus(errors: MediaValidationError[], warnings: MediaValidationWarning[]): ValidationStatus {
  if (errors.length > 0) return 'Invalid'
  if (warnings.length > 0) return 'Warning'
  return 'Valid'
}

function sameErrorList(a: MediaValidationError[], b: MediaValidationError[]): boolean {
  if (a.length !== b.length) return false
  return a.every((e, i) => e.code === b[i]?.code && e.message === b[i]?.message)
}

/**
 * Re-applies the count-dependent Instagram Feed video duration rule to a WHOLE media collection
 * after it changes (add / remove / reorder / restore from props). Single vs carousel is derived
 * from the resulting count (>= 2 === carousel), so the passed array must already be the FINAL
 * collection — never stale React state.
 *
 * For each Instagram Feed video item with a known duration it recomputes ONLY the duration error:
 *   • any stale duration error from the previous count is removed,
 *   • the currently-applicable duration error (if any) is added (never duplicated),
 *   • every other error/warning is preserved verbatim — an unrelated failure (file size, corrupt,
 *     aspect) is never erased,
 *   • the per-item status is recomputed from the merged errors/warnings.
 *
 * Non-Instagram-Feed collections, non-video items, and items with no known duration are returned
 * unchanged. Returns the SAME array reference when nothing changed (idempotent), so it is safe to
 * call from a React effect without causing an update loop.
 */
export function revalidateInstagramFeedCollection<T extends RevalidatableMediaItem>(
  items: T[],
  platform: string | null | undefined,
  placement: string,
): T[] {
  const applies =
    String(platform ?? '').toLowerCase() === 'instagram' &&
    String(placement).toLowerCase() === 'feed'
  if (!applies) return items

  const isCarousel = items.length >= 2
  let changed = false

  const next = items.map((item): T => {
    if (String(item.mediaType).toLowerCase() !== 'video') return item
    if (item.durationSeconds == null) return item

    const result = validateInstagramFeedVideoDuration(item.durationSeconds, isCarousel)

    // Drop any previous duration error, keep all non-duration errors, then add the current one.
    const withoutDuration = item.validationErrors.filter(e => !DURATION_ERROR_CODES.includes(e.code))
    const nextErrors: MediaValidationError[] = result.valid
      ? withoutDuration
      : [
          ...withoutDuration,
          { code: result.code!, field: 'durationSeconds', message: result.message!, expected: null, actual: null },
        ]

    if (sameErrorList(nextErrors, item.validationErrors)) return item

    changed = true
    return {
      ...item,
      validationErrors: nextErrors,
      validationStatus: deriveStatus(nextErrors, item.validationWarnings),
    }
  })

  return changed ? next : items
}
