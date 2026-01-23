const API_URL = 'http://localhost:5122/api'

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
      },
      body: file,
    })
    if (!response.ok) {
      throw new Error('Failed to upload file')
    }
  },
}
