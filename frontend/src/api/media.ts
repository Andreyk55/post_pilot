const API_URL = 'http://localhost:5122/api'

/**
 * Generates a URL for viewing/downloading an image from its S3 key.
 * In local development, this points to the local media server.
 * In production, this would generate a pre-signed S3 URL.
 */
export function getMediaUrl(s3Key: string | null | undefined): string | null {
  if (!s3Key) return null

  // Extract filename from s3Key (e.g., "media/guid.jpg" -> "guid.jpg")
  const filename = s3Key.startsWith('media/') ? s3Key.slice(6) : s3Key

  return `${API_URL}/media/files/${filename}`
}

export interface GenerateUploadUrlRequest {
  fileName: string
  contentType: string
}

export interface GenerateUploadUrlResponse {
  uploadUrl: string
  s3Key: string
  allowedContentTypes: string[]
  maxFileSizeBytes: number
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

  async uploadFile(uploadUrl: string, file: File): Promise<void> {
    const response = await fetch(uploadUrl, {
      method: 'PUT',
      headers: {
        'Content-Type': file.type,
        // Skip ngrok's browser warning page for tunneled requests
        'ngrok-skip-browser-warning': 'true',
      },
      body: file,
    })
    if (!response.ok) {
      throw new Error('Failed to upload file')
    }
  },
}
