import type {
  MetaOAuthStartResponse,
  MetaOAuthCallbackResponse,
  MetaOAuthCompleteResponse,
  MetaDiscoverInstagramRequest,
  MetaDiscoverInstagramResponse,
  MetaSaveConnectionRequest,
  MetaSaveConnectionResponse,
  MetaConnectionResponse,
  MetaUpdatePagesRequest,
  FacebookPage,
  ValidationLimitsResponse,
  InstagramDiscoveryResponse,
} from '../types/meta'
import { config } from '../config/appConfig'

const API_URL = config.apiBaseUrl

/**
 * Error carrying the HTTP status so callers can distinguish 409
 * (workspace already has an active provider connection) from generic failures.
 */
export class MetaApiError extends Error {
  status: number
  constructor(message: string, status: number) {
    super(message)
    this.name = 'MetaApiError'
    this.status = status
  }
}

async function readErrorBody(response: Response): Promise<{ error?: string; provider?: string }> {
  try {
    const text = await response.text()
    if (!text) return {}
    try {
      return JSON.parse(text)
    } catch {
      return { error: text }
    }
  } catch {
    return {}
  }
}

export const metaApi = {
  /**
   * Start the Meta OAuth flow - returns the authorization URL
   */
  async startOAuth(): Promise<MetaOAuthStartResponse> {
    const response = await fetch(`${API_URL}/meta/oauth/start`, {
      method: 'POST',
    })
    if (!response.ok) throw new Error('Failed to start Meta OAuth')
    return response.json()
  },

  /**
   * Handle OAuth callback - exchange code for tokens and get available pages
   */
  async handleCallback(code: string, state: string): Promise<MetaOAuthCallbackResponse> {
    const response = await fetch(`${API_URL}/meta/oauth/callback`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ code, state }),
    })
    if (!response.ok) throw new Error('Failed to complete Meta OAuth')
    return response.json()
  },

  /**
   * Complete OAuth and save connection immediately (identity-level only, no page selection).
   *
   * On 409 (workspace already has an active Meta connection) the server returns
   * { error, provider }. We surface that exact error message — the spec wants:
   *   "This workspace already has a connected Meta account. Disconnect it
   *    before connecting another one."
   * Never silently retry or replace.
   */
  async completeOAuth(code: string, state: string): Promise<MetaOAuthCompleteResponse> {
    const response = await fetch(`${API_URL}/meta/oauth/complete`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ code, state }),
    })
    if (!response.ok) {
      const errorBody = await readErrorBody(response)
      console.error('Meta OAuth complete failed:', response.status, errorBody)
      const err = new MetaApiError(
        errorBody.error ?? `Failed to complete Meta OAuth (${response.status})`,
        response.status
      )
      throw err
    }
    return response.json()
  },

  /**
   * Discover Instagram Business accounts linked to selected pages
   */
  async discoverInstagram(request: MetaDiscoverInstagramRequest): Promise<MetaDiscoverInstagramResponse> {
    const response = await fetch(`${API_URL}/meta/instagram/discover`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })
    if (!response.ok) throw new Error('Failed to discover Instagram accounts')
    return response.json()
  },

  /**
   * Save the final Meta connection with selected pages and Instagram accounts.
   *
   * On 409 the server returns { error, provider } when the account/page is owned
   * by ANOTHER workspace ("This social account is already connected to another
   * workspace. Disconnect it there before connecting it here.") or the workspace
   * already has an active connection. Surface the exact message via MetaApiError
   * so the UI doesn't show a generic failure.
   */
  async saveConnection(request: MetaSaveConnectionRequest): Promise<MetaSaveConnectionResponse> {
    const response = await fetch(`${API_URL}/meta/connection`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })
    if (!response.ok) {
      const errorBody = await readErrorBody(response)
      console.error('Meta save connection failed:', response.status, errorBody)
      throw new MetaApiError(
        errorBody.error ?? `Failed to save Meta connection (${response.status})`,
        response.status
      )
    }
    return response.json()
  },

  /**
   * Get current Meta connection status
   */
  async getConnection(): Promise<MetaConnectionResponse> {
    const response = await fetch(`${API_URL}/meta/connection`)
    if (!response.ok) throw new Error('Failed to get Meta connection')
    return response.json()
  },

  /**
   * Get available pages (for manage flow - uses stored tokens)
   */
  async getAvailablePages(): Promise<{ pages: FacebookPage[] }> {
    const response = await fetch(`${API_URL}/meta/pages`)
    if (!response.ok) throw new Error('Failed to get available pages')
    return response.json()
  },

  /**
   * Update selected pages and Instagram accounts (for manage flow)
   */
  async updateConnection(request: MetaUpdatePagesRequest): Promise<MetaSaveConnectionResponse> {
    const response = await fetch(`${API_URL}/meta/connection`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })
    if (!response.ok) throw new Error('Failed to update Meta connection')
    return response.json()
  },

  /**
   * Get Instagram eligibility for all connected Facebook Pages
   */
  async getInstagramEligibility(): Promise<InstagramDiscoveryResponse> {
    const response = await fetch(`${API_URL}/meta/instagram/eligibility`)
    if (!response.ok) throw new Error('Failed to get Instagram eligibility')
    return response.json()
  },

  /**
   * Disconnect Meta - revokes tokens and removes connection
   */
  async disconnect(): Promise<void> {
    const response = await fetch(`${API_URL}/meta/connection`, {
      method: 'DELETE',
    })
    if (!response.ok) throw new Error('Failed to disconnect Meta')
  },

  /**
   * Get validation limits for the application
   */
  async getLimits(): Promise<ValidationLimitsResponse> {
    const response = await fetch(`${API_URL}/meta/limits`)
    if (!response.ok) throw new Error('Failed to get validation limits')
    return response.json()
  },
}
