/**
 * Instagram media selection validation.
 *
 * Rules:
 * - Single photo (JPG/PNG): allowed
 * - Single video (MP4): allowed (published as Reel)
 * - Carousel: 2–10 images only (JPG/PNG), NO videos, NO mixed media
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
 */
export function validateInstagramSelection(
  existingFiles: MediaFileInfo[],
  newFiles: MediaFileInfo[]
): InstagramSelectionResult {
  if (newFiles.length === 0) {
    return { ok: true, errorMessage: null, nextFiles: [...existingFiles] }
  }

  const newHasVideo = newFiles.some(f => isVideoFile(f))
  const newHasImage = newFiles.some(f => isImageFile(f))
  const existingHasVideo = existingFiles.some(f => isVideoFile(f))
  const existingHasImage = existingFiles.some(f => isImageFile(f))

  // Check for unsupported file types
  const unsupported = newFiles.filter(f => !isImageFile(f) && !isVideoFile(f))
  if (unsupported.length > 0) {
    return {
      ok: false,
      errorMessage: `Unsupported file type: ${unsupported[0].name}. Instagram accepts JPG, PNG, or MP4.`,
      nextFiles: [...existingFiles],
    }
  }

  // Rule: video cannot be mixed with anything
  if (newHasVideo) {
    // Can't select multiple files if any is video
    if (newFiles.length > 1) {
      return {
        ok: false,
        errorMessage: 'Instagram carousel supports images only. For video, upload a single MP4 (published as Reel).',
        nextFiles: [...existingFiles],
      }
    }

    // Can't add video when images already exist
    if (existingHasImage) {
      return {
        ok: false,
        errorMessage: 'Cannot add video to existing images. Remove images first, then upload a single video (published as Reel).',
        nextFiles: [...existingFiles],
      }
    }

    // Can't add video when a video already exists
    if (existingHasVideo) {
      return {
        ok: false,
        errorMessage: 'Only one video allowed. Remove the existing video first.',
        nextFiles: [...existingFiles],
      }
    }

    // Single video, no existing files - ok
    return { ok: true, errorMessage: null, nextFiles: [newFiles[0]] }
  }

  // Rule: can't add images when a video exists
  if (existingHasVideo) {
    return {
      ok: false,
      errorMessage: 'Cannot mix video with images. Remove the video first to create a carousel.',
      nextFiles: [...existingFiles],
    }
  }

  // Rule: mixed media in new files (shouldn't happen after video check above, but defensive)
  if (newHasVideo && newHasImage) {
    return {
      ok: false,
      errorMessage: 'Instagram carousel supports images only. For video, upload a single MP4 (published as Reel).',
      nextFiles: [...existingFiles],
    }
  }

  // Images only from here on
  const totalCount = existingFiles.length + newFiles.length
  if (totalCount > 10) {
    const remaining = 10 - existingFiles.length
    if (remaining <= 0) {
      return {
        ok: false,
        errorMessage: 'Maximum 10 images for carousel. Remove some images first.',
        nextFiles: [...existingFiles],
      }
    }
    // Accept only what fits
    return {
      ok: true,
      errorMessage: `Only ${remaining} more image(s) can be added. Max 10 total.`,
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
export type InstagramMediaMode = 'empty' | 'single_image' | 'single_video' | 'carousel'

export function getInstagramMediaMode(files: MediaFileInfo[]): InstagramMediaMode {
  if (files.length === 0) return 'empty'
  if (files.length === 1 && isVideoFile(files[0])) return 'single_video'
  if (files.length === 1 && isImageFile(files[0])) return 'single_image'
  return 'carousel'
}

/** Dynamic uploader label text */
export function getInstagramUploaderLabel(mode: InstagramMediaMode, count: number): string {
  switch (mode) {
    case 'empty': return 'Add photo or video'
    case 'single_video': return 'Video selected (will publish as Reel)'
    case 'single_image': return '1 photo selected'
    case 'carousel': return `${count} photos selected (carousel)`
  }
}

/** Dynamic format hint text */
export function getInstagramFormatHint(mode: InstagramMediaMode): string {
  switch (mode) {
    case 'empty': return 'JPG, PNG, or MP4'
    case 'single_video': return 'MP4 (single video / Reel)'
    case 'single_image': return 'JPG, PNG'
    case 'carousel': return 'JPG, PNG only (carousel images)'
  }
}
