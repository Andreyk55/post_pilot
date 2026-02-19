/**
 * Facebook media selection validation.
 *
 * Rules:
 * - Single photo (JPG/PNG): allowed
 * - Single video (MP4): allowed
 * - Multi-photo: 2–10 images only (JPG/PNG), NO videos, NO mixed media
 */

export interface MediaFileInfo {
  name: string
  type: string // MIME type
}

export interface FacebookSelectionResult {
  ok: boolean
  errorMessage: string | null
  /** The files to keep after validation (existing + accepted new files) */
  nextFiles: MediaFileInfo[]
}

const IMAGE_TYPES = ['image/jpeg', 'image/png']
const VIDEO_TYPES = ['video/mp4', 'video/quicktime', 'video/x-msvideo']

export function isImageFile(file: MediaFileInfo): boolean {
  return IMAGE_TYPES.includes(file.type.toLowerCase())
}

export function isVideoFile(file: MediaFileInfo): boolean {
  return VIDEO_TYPES.includes(file.type.toLowerCase())
}

/**
 * Validates a new file selection against existing files for Facebook.
 * Returns whether the selection is valid, an error message if not,
 * and the resulting file list.
 */
export function validateFacebookSelection(
  existingFiles: MediaFileInfo[],
  newFiles: MediaFileInfo[]
): FacebookSelectionResult {
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
      errorMessage: `Unsupported file type: ${unsupported[0].name}. Facebook accepts JPG, PNG, or MP4.`,
      nextFiles: [...existingFiles],
    }
  }

  // Rule: video cannot be mixed with anything
  if (newHasVideo) {
    // Can't select multiple files if any is video
    if (newFiles.length > 1) {
      return {
        ok: false,
        errorMessage: 'Multi-photo supports photos only. For video, upload a single MP4.',
        nextFiles: [...existingFiles],
      }
    }

    // Can't add video when images already exist
    if (existingHasImage) {
      return {
        ok: false,
        errorMessage: 'Multi-photo supports photos only. For video, upload a single MP4.',
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
      errorMessage: 'Cannot mix video with photos. Remove the video first to create a multi-photo post.',
      nextFiles: [...existingFiles],
    }
  }

  // Rule: mixed media in new files (defensive)
  if (newHasVideo && newHasImage) {
    return {
      ok: false,
      errorMessage: 'Multi-photo supports photos only. For video, upload a single MP4.',
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
        errorMessage: 'Maximum 10 photos for multi-photo. Remove some photos first.',
        nextFiles: [...existingFiles],
      }
    }
    // Accept only what fits
    return {
      ok: true,
      errorMessage: `Only ${remaining} more photo(s) can be added. Max 10 total.`,
      nextFiles: [...existingFiles, ...newFiles.slice(0, remaining)],
    }
  }

  return {
    ok: true,
    errorMessage: null,
    nextFiles: [...existingFiles, ...newFiles],
  }
}

/** Describes the current Facebook media state for UI labeling */
export type FacebookMediaMode = 'empty' | 'single_image' | 'single_video' | 'multi_photo'

export function getFacebookMediaMode(files: MediaFileInfo[]): FacebookMediaMode {
  if (files.length === 0) return 'empty'
  if (files.length === 1 && isVideoFile(files[0])) return 'single_video'
  if (files.length === 1 && isImageFile(files[0])) return 'single_image'
  return 'multi_photo'
}

/** Dynamic uploader label text */
export function getFacebookUploaderLabel(mode: FacebookMediaMode, count: number): string {
  switch (mode) {
    case 'empty': return 'Add photo or video'
    case 'single_video': return 'Video selected'
    case 'single_image': return '1 photo selected'
    case 'multi_photo': return `${count} photos selected (multi-photo)`
  }
}

/** Dynamic format hint text */
export function getFacebookFormatHint(mode: FacebookMediaMode): string {
  switch (mode) {
    case 'empty': return 'JPG, PNG, or MP4'
    case 'single_video': return 'MP4 only'
    case 'single_image': return 'JPG/PNG'
    case 'multi_photo': return 'JPG/PNG only (multi-photo)'
  }
}
