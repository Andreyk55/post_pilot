const API_URL = 'http://localhost:5122/api'

// Media type enum matching backend
export type MediaType = 'None' | 'Image' | 'Video'

/**
 * Generates a URL for viewing/downloading media from its S3 key.
 * In local development, this points to the local media server.
 * In production, this would generate a pre-signed S3 URL.
 */
export function getMediaUrl(s3Key: string | null | undefined): string | null {
  if (!s3Key) return null

  // Extract filename from s3Key (e.g., "media/guid.jpg" -> "guid.jpg")
  const filename = s3Key.startsWith('media/') ? s3Key.slice(6) : s3Key

  return `${API_URL}/media/files/${filename}`
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
  s3Key: string
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
}
