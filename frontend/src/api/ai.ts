const API_URL = 'http://localhost:5122/api'

export type AiTextAction = 'Polish' | 'RewriteTone' | 'Shorten' | 'Expand' | 'Hashtags' | 'PreFlight'
export type AiTone = 'Professional' | 'Casual' | 'Funny' | 'Sales'
export type AiPlatform = 'Facebook' | 'Instagram' | 'LinkedIn' | 'X'
export type AiIssueSeverity = 'Info' | 'Warning' | 'Error'

export interface AiTextRequest {
  action: AiTextAction
  platform: AiPlatform
  text: string
  tone?: AiTone
  language?: string
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

  async polish(platform: AiPlatform, text: string): Promise<AiTextVariantsResponse> {
    const response = await this.processText({
      action: 'Polish',
      platform,
      text,
    })
    return response as AiTextVariantsResponse
  },

  async rewriteTone(platform: AiPlatform, text: string, tone: AiTone): Promise<AiTextVariantsResponse> {
    const response = await this.processText({
      action: 'RewriteTone',
      platform,
      text,
      tone,
    })
    return response as AiTextVariantsResponse
  },

  async shorten(platform: AiPlatform, text: string): Promise<AiTextVariantsResponse> {
    const response = await this.processText({
      action: 'Shorten',
      platform,
      text,
    })
    return response as AiTextVariantsResponse
  },

  async expand(platform: AiPlatform, text: string): Promise<AiTextVariantsResponse> {
    const response = await this.processText({
      action: 'Expand',
      platform,
      text,
    })
    return response as AiTextVariantsResponse
  },

  async hashtags(platform: AiPlatform, text: string): Promise<AiHashtagsResponse> {
    const response = await this.processText({
      action: 'Hashtags',
      platform,
      text,
    })
    return response as AiHashtagsResponse
  },

  async preFlight(platform: AiPlatform, text: string): Promise<AiPreFlightResponse> {
    const response = await this.processText({
      action: 'PreFlight',
      platform,
      text,
    })
    return response as AiPreFlightResponse
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
}
