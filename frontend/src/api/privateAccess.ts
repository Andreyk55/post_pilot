import { config } from '../config/appConfig'

const API_URL = config.apiBaseUrl

export interface AccessStatus {
  hasAccess: boolean
}

export const privateAccessApi = {
  async me(): Promise<AccessStatus> {
    const response = await fetch(`${API_URL}/private-access/me`, {
      method: 'GET',
      credentials: 'include',
    })
    if (!response.ok) {
      return { hasAccess: false }
    }
    return response.json()
  },

  async login(password: string): Promise<AccessStatus> {
    const response = await fetch(`${API_URL}/private-access/login`, {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password }),
    })
    if (response.status === 401) {
      return { hasAccess: false }
    }
    if (!response.ok) {
      throw new Error('login_failed')
    }
    return response.json()
  },

  async logout(): Promise<void> {
    await fetch(`${API_URL}/private-access/logout`, {
      method: 'POST',
      credentials: 'include',
    })
  },
}
