import { config } from '../config/appConfig'

const API_URL = config.apiBaseUrl

// Media type enum matching backend
export type MediaType = 'None' | 'Image' | 'Video'

/**
 * Generates a URL for viewing/downloading media from its storage key.
 * In local development, this uses a relative URL (proxied by Vite) to avoid CORS issues.
 * In server mode, the storage provider generates a download URL.
 */
export function getMediaUrl(storageKey: string | null | undefined): string | null {
  if (!storageKey) return null

  // Extract filename from storageKey (e.g., "media/guid.jpg" -> "guid.jpg")
  const filename = storageKey.startsWith('media/') ? storageKey.slice(6) : storageKey

  // Use relative URL so Vite proxy can handle it (same-origin for canvas operations)
  return `/api/media/files/${filename}`
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

export interface GenerateUploadUrlRequest {
  fileName: string
  contentType: string
}

export interface GenerateUploadUrlResponse {
  uploadUrl: string
  storageKey: string
  mediaType: MediaType
  allowedImageTypes: string[]
  allowedVideoTypes: string[]
  maxImageFileSizeBytes: number
  maxVideoFileSizeBytes: number
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
export type Placement = 'Feed' | 'Story' | 'Reel'

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
}

export interface MediaValidationResult {
  status: ValidationStatus
  errors: MediaValidationError[]
  warnings: MediaValidationWarning[]
  metadata: ExtractedMediaMetadata | null
}

export interface ValidateMediaRequest {
  storageKey: string
  mimeType: string
  platform: Platform
  placement: Placement
}

export interface ExtractMetadataRequest {
  storageKey: string
  mimeType: string
}

export interface MediaValidationRuleDto {
  allowedMimeTypes: string[]
  maxBytes: number
  minWidth: number
  minHeight: number
  maxWidth: number
  maxHeight: number
  aspectRatioMin: number
  aspectRatioMax: number
  durationMinSeconds: number | null
  durationMaxSeconds: number | null
  recommendedWidth: number | null
  recommendedHeight: number | null
}

export const mediaApi = {
  async generateUploadUrl(request: GenerateUploadUrlRequest): Promise<GenerateUploadUrlResponse> {
    const response = await fetch(`${API_URL}/media/upload-url`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })
    if (!response.ok) {
      const error = await response.json().catch(() => ({ error: 'Failed to generate upload URL' }))
      throw new Error(error.error || 'Failed to generate upload URL')
    }
    return response.json()
  },

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
          reject(new Error('Failed to upload file'))
        }
      })

      xhr.addEventListener('error', () => {
        reject(new Error('Failed to upload file'))
      })

      xhr.addEventListener('abort', () => {
        reject(new Error('Upload cancelled'))
      })

      xhr.open('PUT', uploadUrl)
      xhr.setRequestHeader('Content-Type', file.type)
      // Skip ngrok's browser warning page for tunneled requests
      xhr.setRequestHeader('ngrok-skip-browser-warning', 'true')
      xhr.send(file)
    })
  },

  // ============================================
  // STATELESS MEDIA VALIDATION
  // ============================================

  /**
   * Validates a media file by its storage key for a specific platform and placement.
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
   * Extracts metadata from a media file by its storage key.
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
