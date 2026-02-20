/**
 * Instagram media selection validation.
 *
 * Rules:
 * - Single photo (JPG/PNG): allowed
 * - Single video (MP4): allowed (published as Reel)
 * - Carousel (images): 2–10 images only (JPG/PNG)
 * - Carousel (videos): 2–10 videos only (MP4)
 * - Carousel (mixed): 2–10 items mixing images + videos (IG only)
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

const IMAGE_TYPES = ['image/jpeg', 'image/png']
const VIDEO_TYPES = ['video/mp4']

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
      errorMessage: `Unsupported file type: ${unsupported[0].name}. Instagram accepts JPG, PNG, or MP4.`,
      nextFiles: [...existingFiles],
    }
  }

  // Mixed media is now allowed for Instagram carousels — just enforce max 10 total
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
  switch (mode) {
    case 'empty': return 'Photos (JPG/PNG) or Reel (MP4)'
    case 'single_video': return 'Reel (MP4) — add more for carousel'
    case 'single_image': return 'Photo (JPG/PNG) — add more for carousel'
    case 'carousel': return 'Carousel photos (JPG/PNG) — videos also accepted'
    case 'carousel_videos': return 'Carousel videos (MP4) — photos also accepted'
    case 'carousel_mixed': return 'Mixed carousel (photos + videos)'
  }
}
