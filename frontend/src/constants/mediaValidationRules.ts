/**
 * Media validation rules for client-side pre-checks.
 * NOTE: Backend is the authoritative source of truth for validation rules.
 * These are kept minimal for UX pre-checks before upload.
 * If rules change, update backend first, then sync here.
 */

import type { PlatformId } from './validationLimits'

export type MediaType = 'Image' | 'Video'
export type Placement = 'Feed' | 'Story' | 'Reel'

/** Validation status returned from backend */
export type ValidationStatus = 'Pending' | 'Valid' | 'Invalid' | 'Warning'

/** Validation error from backend */
export interface MediaValidationError {
  code: string
  field: string
  message: string
  expected: string | null
  actual: string | null
}

/** Validation warning from backend */
export interface MediaValidationWarning {
  code: string
  field: string
  message: string
  recommendation: string | null
}

/** Extracted metadata from backend */
export interface ExtractedMediaMetadata {
  width: number | null
  height: number | null
  durationSeconds: number | null
  aspectRatio: number | null
  mimeType: string | null
  sizeBytes: number | null
  container: string | null
  videoCodec: string | null
  audioCodec: string | null
  fps: number | null
}

/** Validation result from backend */
export interface MediaValidationResult {
  status: ValidationStatus
  errors: MediaValidationError[]
  warnings: MediaValidationWarning[]
  metadata: ExtractedMediaMetadata | null
}

/** Client-side validation rule (subset of backend rules) */
export interface ClientMediaValidationRule {
  allowedMimeTypes: string[]
  maxBytes: number
  minWidth: number
  minHeight: number
  maxWidth: number
  maxHeight: number
  aspectRatioMin: number
  aspectRatioMax: number
  durationMinSeconds?: number
  durationMaxSeconds?: number
}

/** Platform-specific rules key */
type RuleKey = `${PlatformId}:${Lowercase<Placement>}:${Lowercase<MediaType>}`

/**
 * Client-side validation rules (matches backend MediaValidationRules.cs).
 * Keep this in sync with backend - backend is authoritative.
 */
const clientValidationRules: Partial<Record<RuleKey, ClientMediaValidationRule>> = {
  // Facebook Feed Image
  'facebook:feed:image': {
    allowedMimeTypes: ['image/jpeg', 'image/png', 'image/gif', 'image/bmp', 'image/tiff', 'image/webp'],
    maxBytes: 4 * 1024 * 1024, // 4MB
    minWidth: 320,
    minHeight: 320,
    maxWidth: 2048,
    maxHeight: 2048,
    aspectRatioMin: 0.5625, // 9:16
    aspectRatioMax: 1.91, // ~1.91:1
  },

  // Facebook Feed Video
  'facebook:feed:video': {
    allowedMimeTypes: ['video/mp4', 'video/quicktime', 'video/x-msvideo', 'video/webm'],
    maxBytes: 1024 * 1024 * 1024, // 1GB
    minWidth: 120,
    minHeight: 120,
    maxWidth: 4096,
    maxHeight: 4096,
    aspectRatioMin: 0.5625,
    aspectRatioMax: 1.91,
    durationMinSeconds: 1,
    durationMaxSeconds: 240 * 60, // 4 hours
  },

  // Facebook Story Image
  'facebook:story:image': {
    allowedMimeTypes: ['image/jpeg', 'image/png', 'image/gif', 'image/bmp', 'image/tiff', 'image/webp'],
    maxBytes: 4 * 1024 * 1024, // 4MB
    minWidth: 320,
    minHeight: 320,
    maxWidth: 1080,
    maxHeight: 1920,
    aspectRatioMin: 0.5625, // 9:16
    aspectRatioMax: 0.5625,
  },

  // Facebook Story Video
  'facebook:story:video': {
    allowedMimeTypes: ['video/mp4', 'video/quicktime'],
    maxBytes: 1024 * 1024 * 1024, // 1GB
    minWidth: 320,
    minHeight: 320,
    maxWidth: 1080,
    maxHeight: 1920,
    aspectRatioMin: 0.5625,
    aspectRatioMax: 0.5625,
    durationMinSeconds: 1,
    durationMaxSeconds: 120, // 2 minutes
  },

  // Instagram Feed Image
  'instagram:feed:image': {
    allowedMimeTypes: ['image/jpeg', 'image/png'],
    maxBytes: 8 * 1024 * 1024, // 8MB
    minWidth: 320,
    minHeight: 320,
    maxWidth: 1440,
    maxHeight: 1440,
    aspectRatioMin: 0.8, // 4:5
    aspectRatioMax: 1.91,
  },

  // Instagram Feed Video
  'instagram:feed:video': {
    allowedMimeTypes: ['video/mp4', 'video/quicktime'],
    maxBytes: 100 * 1024 * 1024, // 100MB
    minWidth: 500,
    minHeight: 500,
    maxWidth: 1920,
    maxHeight: 1920,
    aspectRatioMin: 0.8,
    aspectRatioMax: 1.91,
    durationMinSeconds: 3,
    durationMaxSeconds: 60, // 60 seconds
  },

  // Instagram Story Image
  'instagram:story:image': {
    allowedMimeTypes: ['image/jpeg', 'image/png'],
    maxBytes: 8 * 1024 * 1024, // 8MB
    minWidth: 320,
    minHeight: 320,
    maxWidth: 1080,
    maxHeight: 1920,
    aspectRatioMin: 0.5625, // 9:16
    aspectRatioMax: 0.5625,
  },

  // Instagram Story Video
  'instagram:story:video': {
    allowedMimeTypes: ['video/mp4', 'video/quicktime'],
    maxBytes: 100 * 1024 * 1024, // 100MB
    minWidth: 320,
    minHeight: 320,
    maxWidth: 1080,
    maxHeight: 1920,
    aspectRatioMin: 0.5625,
    aspectRatioMax: 0.5625,
    durationMinSeconds: 3,
    durationMaxSeconds: 60, // 60 seconds
  },

  // Twitter/X Feed Image
  'twitter:feed:image': {
    allowedMimeTypes: ['image/jpeg', 'image/png', 'image/gif', 'image/webp'],
    maxBytes: 5 * 1024 * 1024, // 5MB
    minWidth: 100,
    minHeight: 100,
    maxWidth: 4096,
    maxHeight: 4096,
    aspectRatioMin: 0.5,
    aspectRatioMax: 3.0,
  },

  // Twitter/X Feed Video
  'twitter:feed:video': {
    allowedMimeTypes: ['video/mp4'],
    maxBytes: 512 * 1024 * 1024, // 512MB
    minWidth: 32,
    minHeight: 32,
    maxWidth: 1920,
    maxHeight: 1200,
    aspectRatioMin: 0.5,
    aspectRatioMax: 2.0,
    durationMinSeconds: 0.5,
    durationMaxSeconds: 140, // 2 min 20 sec
  },

  // LinkedIn Feed Image
  'linkedin:feed:image': {
    allowedMimeTypes: ['image/jpeg', 'image/png', 'image/gif'],
    maxBytes: 8 * 1024 * 1024, // 8MB
    minWidth: 276,
    minHeight: 276,
    maxWidth: 4320,
    maxHeight: 4320,
    aspectRatioMin: 0.57,
    aspectRatioMax: 3.0,
  },

  // LinkedIn Feed Video
  'linkedin:feed:video': {
    allowedMimeTypes: ['video/mp4', 'video/quicktime', 'video/x-msvideo'],
    maxBytes: 200 * 1024 * 1024, // 200MB
    minWidth: 256,
    minHeight: 144,
    maxWidth: 4096,
    maxHeight: 2304,
    aspectRatioMin: 0.5625,
    aspectRatioMax: 2.4,
    durationMinSeconds: 3,
    durationMaxSeconds: 600, // 10 minutes
  },
}

/**
 * Gets the validation rule for a specific platform, placement, and media type.
 */
export function getClientValidationRule(
  platform: PlatformId | string,
  placement: Placement | string = 'Feed',
  mediaType: MediaType | string
): ClientMediaValidationRule | null {
  const key = `${platform.toLowerCase()}:${placement.toLowerCase()}:${mediaType.toLowerCase()}` as RuleKey
  return clientValidationRules[key] ?? null
}

/**
 * Pre-validates a file before upload using client-side rules.
 * Returns an array of error messages, or empty array if valid.
 */
export function preValidateFile(
  file: File,
  platform: PlatformId | string,
  placement: Placement | string = 'Feed'
): string[] {
  const errors: string[] = []

  // Determine media type from MIME
  const isImage = file.type.startsWith('image/')
  const isVideo = file.type.startsWith('video/')
  const mediaType: MediaType | null = isImage ? 'Image' : isVideo ? 'Video' : null

  if (!mediaType) {
    errors.push('Unsupported file type. Please upload an image or video.')
    return errors
  }

  const rule = getClientValidationRule(platform, placement, mediaType)
  if (!rule) {
    // No client rules defined - let backend handle it
    return []
  }

  // Check MIME type
  if (!rule.allowedMimeTypes.includes(file.type.toLowerCase())) {
    errors.push(`File type "${file.type}" is not supported for ${platform}. Allowed: ${rule.allowedMimeTypes.join(', ')}`)
  }

  // Check file size
  if (file.size > rule.maxBytes) {
    const maxMB = (rule.maxBytes / (1024 * 1024)).toFixed(1)
    const actualMB = (file.size / (1024 * 1024)).toFixed(1)
    errors.push(`File size (${actualMB}MB) exceeds maximum allowed (${maxMB}MB)`)
  }

  return errors
}

/**
 * Pre-validates image dimensions using client-side rules.
 * Call this after loading image metadata.
 */
export function preValidateImageDimensions(
  width: number,
  height: number,
  platform: PlatformId | string,
  placement: Placement | string = 'Feed'
): string[] {
  const errors: string[] = []
  const rule = getClientValidationRule(platform, placement, 'Image')

  if (!rule) return []

  // Check minimum dimensions
  if (width < rule.minWidth || height < rule.minHeight) {
    errors.push(`Image dimensions (${width}x${height}) are too small. Minimum: ${rule.minWidth}x${rule.minHeight}`)
  }

  // Check maximum dimensions
  if (width > rule.maxWidth || height > rule.maxHeight) {
    errors.push(`Image dimensions (${width}x${height}) are too large. Maximum: ${rule.maxWidth}x${rule.maxHeight}`)
  }

  // Check aspect ratio
  const aspectRatio = width / height
  if (aspectRatio < rule.aspectRatioMin || aspectRatio > rule.aspectRatioMax) {
    errors.push(
      `Aspect ratio (${aspectRatio.toFixed(2)}) is outside allowed range (${rule.aspectRatioMin.toFixed(2)} to ${rule.aspectRatioMax.toFixed(2)})`
    )
  }

  return errors
}

/**
 * Gets image dimensions from a File object using Image API.
 * Returns null if extraction fails.
 */
export function getImageDimensions(file: File): Promise<{ width: number; height: number } | null> {
  return new Promise((resolve) => {
    if (!file.type.startsWith('image/')) {
      resolve(null)
      return
    }

    const img = new Image()
    const url = URL.createObjectURL(file)

    img.onload = () => {
      URL.revokeObjectURL(url)
      resolve({ width: img.naturalWidth, height: img.naturalHeight })
    }

    img.onerror = () => {
      URL.revokeObjectURL(url)
      resolve(null)
    }

    img.src = url
  })
}

/** Error codes matching backend */
export const MediaValidationErrorCodes = {
  FileTooLarge: 'FILE_TOO_LARGE',
  UnsupportedMimeType: 'UNSUPPORTED_MIME_TYPE',
  DimensionsTooSmall: 'DIMENSIONS_TOO_SMALL',
  DimensionsTooLarge: 'DIMENSIONS_TOO_LARGE',
  AspectRatioInvalid: 'ASPECT_RATIO_INVALID',
  DurationTooShort: 'DURATION_TOO_SHORT',
  DurationTooLong: 'DURATION_TOO_LONG',
  FpsTooLow: 'FPS_TOO_LOW',
  FpsTooHigh: 'FPS_TOO_HIGH',
  UnsupportedContainer: 'UNSUPPORTED_CONTAINER',
  UnsupportedVideoCodec: 'UNSUPPORTED_VIDEO_CODEC',
  UnsupportedAudioCodec: 'UNSUPPORTED_AUDIO_CODEC',
  MetadataExtractionFailed: 'METADATA_EXTRACTION_FAILED',
  NoRulesForCombination: 'NO_RULES_FOR_COMBINATION',
} as const
