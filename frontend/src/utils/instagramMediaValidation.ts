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
  'Instagram requires JPEG. PNG images will be converted automatically. WebP is not supported.'

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

  // Check for unsupported file types
  const unsupported = newFiles.filter(f => !isImageFile(f) && !isVideoFile(f))
  if (unsupported.length > 0) {
    return {
      ok: false,
      errorMessage: `Unsupported file type: ${unsupported[0].name}. Instagram accepts JPG, PNG (auto-converted to JPEG), MP4, or MOV. WebP is not supported.`,
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
  // PNG is auto-converted to JPEG by the backend; WebP is not supported yet.
  switch (mode) {
    case 'empty': return 'Photos (JPG/PNG) or video (MP4/MOV). PNG is converted to JPEG; WebP not supported.'
    case 'single_video': return 'Video (MP4/MOV) - add more for carousel'
    case 'single_image': return 'Photo (JPG/PNG, PNG auto-converted) - add more for carousel'
    case 'carousel': return 'Carousel photos (JPG/PNG, PNG auto-converted) - videos also accepted'
    case 'carousel_videos': return 'Carousel videos (MP4/MOV) - photos also accepted'
    case 'carousel_mixed': return 'Mixed carousel (photos + videos). PNG photos auto-converted to JPEG.'
  }
}
