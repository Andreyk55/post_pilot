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

const API_URL = 'http://localhost:5122/api'

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
   * Complete OAuth and save connection immediately (identity-level only, no page selection)
   */
  async completeOAuth(code: string, state: string): Promise<MetaOAuthCompleteResponse> {
    const response = await fetch(`${API_URL}/meta/oauth/complete`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ code, state }),
    })
    if (!response.ok) {
      const errorText = await response.text()
      console.error('Meta OAuth complete failed:', response.status, errorText)
      throw new Error(`Failed to complete Meta OAuth: ${response.status} ${errorText}`)
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
   * Save the final Meta connection with selected pages and Instagram accounts
   */
  async saveConnection(request: MetaSaveConnectionRequest): Promise<MetaSaveConnectionResponse> {
    const response = await fetch(`${API_URL}/meta/connection`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })
    if (!response.ok) throw new Error('Failed to save Meta connection')
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
