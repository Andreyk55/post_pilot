const API_URL = 'http://localhost:5122/api'

export type AiTextAction = 'Polish' | 'RewriteTone' | 'Shorten' | 'Expand' | 'Hashtags' | 'PreFlight' | 'GenerateVariants'
export type AiTone = 'Professional' | 'Casual' | 'Funny' | 'Sales' | 'Humorous' | 'Urgent' | 'Inspirational'
export type AiPlatform = 'Facebook' | 'Instagram' | 'LinkedIn' | 'X'
export type AiIssueSeverity = 'Info' | 'Warning' | 'Error'
export type AiGoal = 'Engage' | 'Promote' | 'Announce' | 'Educate' | 'Story'
export type AiLength = 'Short' | 'Medium' | 'Long'

export interface AiTextRequest {
  action: AiTextAction
  platform: AiPlatform
  text: string
  tone?: AiTone
  language?: string
  voiceProfileId?: string | null
}

export interface AiTextVariant {
  title: string
  text: string
}

export interface AiTextVariantsResponse {
  action: AiTextAction
  variants: AiTextVariant[]
}

export interface AiHashtagsResponse {
  action: 'Hashtags'
  hashtags: string[]
}

export interface AiPreFlightIssue {
  severity: AiIssueSeverity
  message: string
  suggestedFix: string | null
}

export interface AiPreFlightResponse {
  action: 'PreFlight'
  score: number
  issues: AiPreFlightIssue[]
}

export type AiTextResponse = AiTextVariantsResponse | AiHashtagsResponse | AiPreFlightResponse

// New Generate Variants types
export interface AiGenerateVariantsRequest {
  platform: AiPlatform
  inputText: string
  goal: AiGoal
  tone: AiTone
  length: AiLength
  includeEmojis?: boolean
  includeHashtags?: boolean
  includeCta?: boolean
  includeQuestion?: boolean
  numVariants?: number
  language?: string
  regenerateIndex?: number
  voiceProfileId?: string | null
}

export interface AiGeneratedVariant {
  id: string
  text: string
}

export interface AiGenerateVariantsResponse {
  variants: AiGeneratedVariant[]
}

export interface AiApiError {
  title: string
  detail: string
  status: number
}

export const aiApi = {
  async processText(request: AiTextRequest): Promise<AiTextResponse> {
    const response = await fetch(`${API_URL}/ai/text`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })

    if (!response.ok) {
      const error: AiApiError = await response.json().catch(() => ({
        title: 'Error',
        detail: 'An unexpected error occurred',
        status: response.status,
      }))
      throw new AiError(error.detail, response.status, error.title)
    }

    return response.json()
  },

  async polish(platform: AiPlatform, text: string, voiceProfileId?: string | null): Promise<AiTextVariantsResponse> {
    const response = await this.processText({
      action: 'Polish',
      platform,
      text,
      voiceProfileId,
    })
    return response as AiTextVariantsResponse
  },

  async rewriteTone(platform: AiPlatform, text: string, tone: AiTone, voiceProfileId?: string | null): Promise<AiTextVariantsResponse> {
    const response = await this.processText({
      action: 'RewriteTone',
      platform,
      text,
      tone,
      voiceProfileId,
    })
    return response as AiTextVariantsResponse
  },

  async shorten(platform: AiPlatform, text: string, voiceProfileId?: string | null): Promise<AiTextVariantsResponse> {
    const response = await this.processText({
      action: 'Shorten',
      platform,
      text,
      voiceProfileId,
    })
    return response as AiTextVariantsResponse
  },

  async expand(platform: AiPlatform, text: string, voiceProfileId?: string | null): Promise<AiTextVariantsResponse> {
    const response = await this.processText({
      action: 'Expand',
      platform,
      text,
      voiceProfileId,
    })
    return response as AiTextVariantsResponse
  },

  async hashtags(platform: AiPlatform, text: string, voiceProfileId?: string | null): Promise<AiHashtagsResponse> {
    const response = await this.processText({
      action: 'Hashtags',
      platform,
      text,
      voiceProfileId,
    })
    return response as AiHashtagsResponse
  },

  async preFlight(platform: AiPlatform, text: string, voiceProfileId?: string | null): Promise<AiPreFlightResponse> {
    const response = await this.processText({
      action: 'PreFlight',
      platform,
      text,
      voiceProfileId,
    })
    return response as AiPreFlightResponse
  },

  /**
   * Generate text variants with full control options (goal, tone, length, include flags).
   */
  async generateVariants(request: AiGenerateVariantsRequest): Promise<AiGenerateVariantsResponse> {
    const response = await fetch(`${API_URL}/ai/text/generate`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })

    if (!response.ok) {
      const error: AiApiError = await response.json().catch(() => ({
        title: 'Error',
        detail: 'An unexpected error occurred',
        status: response.status,
      }))
      throw new AiError(error.detail, response.status, error.title)
    }

    return response.json()
  },
}

export class AiError extends Error {
  status: number
  title: string

  constructor(message: string, status: number, title: string = 'Error') {
    super(message)
    this.name = 'AiError'
    this.status = status
    this.title = title
  }

  get isRateLimited(): boolean {
    return this.status === 429
  }

  get isUnavailable(): boolean {
    return this.status === 503 || this.status === 504
  }

  get isMediaTooLarge(): boolean {
    return this.status === 413 || this.status === 400
  }
}

// Media AI Types
export type AiMediaAction =
  | 'CaptionIdeas'
  | 'ImageQualityCheck'
  | 'AltText'
  | 'VideoCaptionIdeas'
  | 'ThumbnailSuggest'

export type AiAssetType = 'image' | 'video'

export interface AiMediaRequest {
  action: AiMediaAction
  platform: AiPlatform
  assetUrl: string
  assetType: AiAssetType
  text?: string
  language?: string
}

export interface AiMediaCaptionVariant {
  title: string
  text: string
}

export interface AiMediaCaptionIdeasResponse {
  action: 'CaptionIdeas' | 'VideoCaptionIdeas'
  variants: AiMediaCaptionVariant[]
}

export interface AiImageQualityIssue {
  severity: AiIssueSeverity
  message: string
  suggestedFix: string | null
}

export interface AiImageQualityCheckResponse {
  action: 'ImageQualityCheck'
  score: number
  issues: AiImageQualityIssue[]
}

export interface AiAltTextResponse {
  action: 'AltText'
  altText: string
}

export interface AiVideoFrame {
  timestampSeconds: number
  imageUrl: string
}

export interface AiThumbnailSuggestResponse {
  action: 'ThumbnailSuggest'
  frames: AiVideoFrame[]
}

// Request with pre-extracted frames (for thumbnail selection)
export interface AiThumbnailSelectRequest {
  frames: {
    timestampSeconds: number
    imageData: string // base64 data URL
  }[]
}

export type AiMediaResponse =
  | AiMediaCaptionIdeasResponse
  | AiImageQualityCheckResponse
  | AiAltTextResponse
  | AiThumbnailSuggestResponse

// Media AI API
export const aiMediaApi = {
  async processMedia(request: AiMediaRequest): Promise<AiMediaResponse> {
    const response = await fetch(`${API_URL}/ai/media`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })

    if (!response.ok) {
      const error: AiApiError = await response.json().catch(() => ({
        title: 'Error',
        detail: 'An unexpected error occurred',
        status: response.status,
      }))
      throw new AiError(error.detail, response.status, error.title)
    }

    return response.json()
  },

  async imageCaptionIdeas(
    platform: AiPlatform,
    assetUrl: string,
    text?: string
  ): Promise<AiMediaCaptionIdeasResponse> {
    const response = await this.processMedia({
      action: 'CaptionIdeas',
      platform,
      assetUrl,
      assetType: 'image',
      text,
    })
    return response as AiMediaCaptionIdeasResponse
  },

  async imageQualityCheck(assetUrl: string): Promise<AiImageQualityCheckResponse> {
    const response = await this.processMedia({
      action: 'ImageQualityCheck',
      platform: 'Facebook', // Platform doesn't matter for quality check
      assetUrl,
      assetType: 'image',
    })
    return response as AiImageQualityCheckResponse
  },

  async altText(assetUrl: string): Promise<AiAltTextResponse> {
    const response = await this.processMedia({
      action: 'AltText',
      platform: 'Facebook', // Platform doesn't matter for alt text
      assetUrl,
      assetType: 'image',
    })
    return response as AiAltTextResponse
  },

  async videoCaptionIdeas(
    platform: AiPlatform,
    assetUrl: string,
    text?: string
  ): Promise<AiMediaCaptionIdeasResponse> {
    const response = await this.processMedia({
      action: 'VideoCaptionIdeas',
      platform,
      assetUrl,
      assetType: 'video',
      text,
    })
    return response as AiMediaCaptionIdeasResponse
  },

  async thumbnailSuggest(assetUrl: string): Promise<AiThumbnailSuggestResponse> {
    const response = await this.processMedia({
      action: 'ThumbnailSuggest',
      platform: 'Facebook', // Platform doesn't matter for thumbnails
      assetUrl,
      assetType: 'video',
    })
    return response as AiThumbnailSuggestResponse
  },

  /**
   * Submit pre-extracted frames for thumbnail selection.
   * Frames are extracted client-side and sent as base64.
   * This approach works in Lambda (no FFmpeg required).
   */
  async submitThumbnailFrames(
    frames: { timestampSeconds: number; imageData: string }[]
  ): Promise<AiThumbnailSuggestResponse> {
    const response = await fetch(`${API_URL}/ai/media/thumbnails`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ frames }),
    })

    if (!response.ok) {
      const error: AiApiError = await response.json().catch(() => ({
        title: 'Error',
        detail: 'An unexpected error occurred',
        status: response.status,
      }))
      throw new AiError(error.detail, response.status, error.title)
    }

    return response.json()
  },

  /**
   * Generate caption ideas for a video using a pre-extracted frame.
   * Frame is extracted client-side and sent as base64.
   * This approach works in Lambda (no FFmpeg required).
   */
  async videoCaptionIdeasWithFrame(
    platform: AiPlatform,
    frameData: string, // base64 data URL
    text?: string
  ): Promise<AiMediaCaptionIdeasResponse> {
    const response = await fetch(`${API_URL}/ai/media/video-captions`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ platform, frameData, text }),
    })

    if (!response.ok) {
      const error: AiApiError = await response.json().catch(() => ({
        title: 'Error',
        detail: 'An unexpected error occurred',
        status: response.status,
      }))
      throw new AiError(error.detail, response.status, error.title)
    }

    return response.json()
  },
}
