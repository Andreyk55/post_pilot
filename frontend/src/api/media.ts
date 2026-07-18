import { config } from '../config/appConfig'

const API_URL = config.apiBaseUrl

// Media type enum matching backend
export type MediaType = 'None' | 'Image' | 'Video'

/**
 * Generates the authenticated app media URL for a mediaId.
 */
export function getMediaUrl(mediaId: string | null | undefined, variant?: 'thumbnail'): string | null {
  if (!mediaId) return null
  const encodedMediaId = encodeURIComponent(mediaId)
  const query = variant ? `?variant=${encodeURIComponent(variant)}` : ''

  const base = (API_URL ?? '').trim()
  if (isAbsoluteUrl(base)) {
    return `${base.replace(/\/+$/, '')}/media/${encodedMediaId}/file${query}`
  }

  return `/api/media/${encodedMediaId}/file${query}`
}

/** True for an http(s) absolute URL — i.e. a cross-origin API base we must anchor to. */
function isAbsoluteUrl(value: string): boolean {
  return /^https?:\/\//i.test(value)
}

/**
 * Determines the media type from a filename extension.
 */
export function getMediaTypeFromFile(filename: string): MediaType {
  const ext = filename.toLowerCase().split('.').pop()
  if (['jpg', 'jpeg', 'png', 'gif'].includes(ext || '')) return 'Image'
  if (['mp4'].includes(ext || '')) return 'Video'
  return 'None'
}

export interface InitUploadRequest {
  fileName: string
  contentType: string
  sizeBytes: number
  platform: 'Facebook' | 'Instagram'
}

export interface InitUploadResponse {
  mediaId: string
  uploadUrl: string
  method: 'PUT'
  contentType: string
  expiresAt: string
  mediaType: MediaType
  previewUrl: string
}

type MediaApiProblemDetails = {
  error?: string
  detail?: string
  title?: string
  code?: string
}

export interface CompleteUploadRequest {
  mediaId: string
}

export interface CompleteUploadResponse {
  mediaId: string
  sizeBytes: number
  contentType: string
  uploadedAt: string
  previewUrl: string
}

export interface MediaConstraintsResponse {
  allowedImageTypes: string[]
  allowedVideoTypes: string[]
  maxImageFileSizeBytes: number
  maxVideoFileSizeBytes: number
}

// Types for media validation
export type ValidationStatus = 'Pending' | 'Valid' | 'Invalid' | 'Warning'
export type Platform = 'Facebook' | 'Instagram' | 'Twitter' | 'LinkedIn'
// Feed and Story only — PostPilot has no Reel post type. (An Instagram single video is
// published by Meta as a Reel but is still created/validated as a Feed video.)
export type Placement = 'Feed' | 'Story'

export interface MediaValidationError {
  code: string
  field: string
  message: string
  expected: string | null
  actual: string | null
}

export interface MediaValidationWarning {
  code: string
  field: string
  message: string
  recommendation: string | null
}

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
  hasVideoStream?: boolean | null
}

export interface MediaValidationResult {
  status: ValidationStatus
  errors: MediaValidationError[]
  warnings: MediaValidationWarning[]
  metadata: ExtractedMediaMetadata | null
}

export interface ValidateMediaRequest {
  mediaId: string
  mimeType: string
  platform: Platform
  placement: Placement
  /**
   * True when validating this item as part of a multi-item carousel, so the advisory status
   * reflects the carousel per-item rules (currently the Instagram Feed video 60s cap vs the 180s
   * single-video cap). Optional; the backend defaults it to false.
   */
  carousel?: boolean
}

export interface ExtractMetadataRequest {
  mediaId: string
  mimeType: string
}

export interface MediaValidationRuleDto {
  allowedMimeTypes: string[]
  maxBytes: number
  // Dimension/aspect fields are null when the rule has no such constraint (e.g. Facebook Story
  // has no dimension or aspect-ratio validation at all).
  minWidth: number | null
  minHeight: number | null
  maxWidth: number | null
  maxHeight: number | null
  aspectRatioMin: number | null
  aspectRatioMax: number | null
  durationMinSeconds: number | null
  durationMaxSeconds: number | null
  recommendedWidth: number | null
  recommendedHeight: number | null
}

export const mediaApi = {
  async getConstraints(): Promise<MediaConstraintsResponse> {
    const response = await fetch(`${API_URL}/media/constraints`)
    if (!response.ok) {
      throw new Error('Failed to get media constraints')
    }
    return response.json()
  },

  async uploadFile(
    uploadUrl: string,
    file: File,
    onProgress?: (percent: number) => void
  ): Promise<void> {
    // Use XMLHttpRequest for progress tracking (fetch doesn't support upload progress)
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest()

      xhr.upload.addEventListener('progress', (event) => {
        if (event.lengthComputable && onProgress) {
          const percent = (event.loaded / event.total) * 100
          onProgress(percent)
        }
      })

      xhr.addEventListener('load', () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          resolve()
        } else {
          // Surface the storage server's response when it carries a usable message,
          // instead of a generic string — so callers can show *why* the upload failed.
          // Skip HTML bodies (error pages) and anything too long to be a message.
          const detail = (xhr.responseText || '').trim()
          const snippet = detail && detail.length <= 300 && !detail.startsWith('<') ? `: ${detail}` : ''
          reject(new Error(`Upload failed (HTTP ${xhr.status})${snippet}`))
        }
      })

      xhr.addEventListener('error', () => {
        reject(new Error('Upload failed. Check your connection and try again.'))
      })

      xhr.addEventListener('abort', () => {
        reject(new Error('Upload cancelled'))
      })

      xhr.open('PUT', uploadUrl)
      xhr.setRequestHeader('Content-Type', file.type)

      // The ngrok-skip-browser-warning header is only meaningful for requests
      // tunneled through ngrok (i.e. requests to the API origin in some dev
      // setups). When uploading directly to MinIO via a presigned URL, this
      // header is not part of the signed headers and would invalidate the
      // signature on stricter S3 servers — and CORS preflight would fail
      // unless MinIO is configured to allow it.
      try {
        const target = new URL(uploadUrl)
        const isDirectToObjectStorage =
          target.host === 'localhost:9000' || target.host.startsWith('minio:')
        if (!isDirectToObjectStorage) {
          xhr.setRequestHeader('ngrok-skip-browser-warning', 'true')
        }
      } catch {
        // If URL parsing fails for some reason, fall back to the safer default.
      }

      xhr.send(file)
    })
  },

  /**
   * Step 1 of the direct-upload flow. Server creates a Media row in PendingUpload
   * status and returns a presigned PUT URL the client should upload the bytes to.
   */
  async initUpload(request: InitUploadRequest): Promise<InitUploadResponse> {
    const response = await fetch(`${API_URL}/media/uploads/init`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })
    if (!response.ok) {
      const error = await response.json().catch(() => ({ error: 'Failed to initiate upload' }))
      throw new Error(getMediaApiErrorMessage(error, 'Failed to initiate upload'))
    }
    return response.json()
  },

  /**
   * Step 2 of the direct-upload flow. Server verifies the object exists in storage
   * and flips the Media row to Uploaded. Idempotent.
   */
  async completeUpload(request: CompleteUploadRequest): Promise<CompleteUploadResponse> {
    const response = await fetch(`${API_URL}/media/uploads/complete`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })
    if (!response.ok) {
      const error = await response.json().catch(() => ({ error: 'Failed to complete upload' }))
      throw new Error(error.error || 'Failed to complete upload')
    }
    return response.json()
  },

  // ============================================
  // STATELESS MEDIA VALIDATION
  // ============================================

  /**
   * Validates a media file by its mediaId for a specific platform and placement.
   * This is a stateless operation - no database record is created.
   */
  async validateMedia(request: ValidateMediaRequest): Promise<MediaValidationResult> {
    const response = await fetch(`${API_URL}/media/validate`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })
    if (!response.ok) {
      const error = await response.json().catch(() => ({ error: 'Validation failed' }))
      throw new Error(error.error || 'Validation failed')
    }
    return response.json()
  },

  /**
   * Extracts metadata from a media file by its mediaId.
   * This is a stateless operation - no database record is created.
   */
  async extractMetadata(request: ExtractMetadataRequest): Promise<ExtractedMediaMetadata> {
    const response = await fetch(`${API_URL}/media/extract-metadata`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })
    if (!response.ok) {
      throw new Error('Failed to extract metadata')
    }
    return response.json()
  },

  /**
   * Gets validation rules for a specific platform/placement/mediaType combo.
   */
  async getValidationRules(
    platform: Platform,
    placement: Placement,
    mediaType: MediaType
  ): Promise<MediaValidationRuleDto> {
    const params = new URLSearchParams({
      platform,
      placement,
      mediaType,
    })
    const response = await fetch(`${API_URL}/media/validation-rules?${params}`)
    if (!response.ok) {
      throw new Error('No validation rules found')
    }
    return response.json()
  },
}

export function getMediaApiErrorMessage(
  error: MediaApiProblemDetails | null | undefined,
  fallback: string,
): string {
  if (!error || typeof error !== 'object') return fallback
  if (typeof error.error === 'string' && error.error.trim()) return error.error.trim()
  if (typeof error.detail === 'string' && error.detail.trim()) return error.detail.trim()
  if (typeof error.title === 'string' && error.title.trim()) return error.title.trim()
  return fallback
}
