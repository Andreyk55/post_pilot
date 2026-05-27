import { config } from '../config/appConfig'

const API_URL = config.apiBaseUrl

export interface AuthUser {
  id: string
  email: string
  displayName: string
  avatarUrl: string | null
  currentWorkspaceId: string
  workspaceName: string
}

export const authApi = {
  /** Backend URL the "Continue with Google" button should redirect to. */
  googleStartUrl(returnUrl?: string): string {
    const url = new URL(`${API_URL}/auth/google/start`, window.location.origin)
    if (returnUrl) {
      url.searchParams.set('returnUrl', returnUrl)
    }
    return url.toString()
  },

  /** Returns the logged-in user or null when the session cookie is absent/expired. */
  async me(): Promise<AuthUser | null> {
    const response = await fetch(`${API_URL}/auth/me`, {
      method: 'GET',
      credentials: 'include',
    })
    if (response.status === 401) return null
    if (!response.ok) throw new Error(`auth_me_failed_${response.status}`)
    return (await response.json()) as AuthUser
  },

  async logout(): Promise<void> {
    await fetch(`${API_URL}/auth/logout`, {
      method: 'POST',
      credentials: 'include',
    })
  },
}
