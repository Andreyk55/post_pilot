import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { authApi, type AuthUser } from '../api/auth'

interface AuthContextValue {
  user: AuthUser | null
  isLoading: boolean
  isAuthenticated: boolean
  /**
   * True only when the user is authenticated AND has a valid selected workspace.
   * Workspace-scoped actions (posting, uploading, provider connection) must be
   * blocked unless this is true.
   */
  hasWorkspace: boolean
  /** Re-fetches /api/auth/me; call after the OAuth round-trip lands on /auth/callback. */
  refreshUser: () => Promise<AuthUser | null>
  /** Clears the session cookie and resets local auth state. */
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null)
  const [isLoading, setIsLoading] = useState(true)

  const refreshUser = useCallback(async () => {
    const me = await authApi.me().catch(() => null)
    setUser(me)
    setIsLoading(false)
    return me
  }, [])

  useEffect(() => {
    let cancelled = false
    authApi
      .me()
      .then((me) => {
        if (cancelled) return
        setUser(me)
      })
      .catch(() => {
        if (!cancelled) setUser(null)
      })
      .finally(() => {
        if (!cancelled) setIsLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [])

  const logout = useCallback(async () => {
    await authApi.logout().catch(() => undefined)
    setUser(null)
  }, [])

  const value = useMemo<AuthContextValue>(
    () => ({
      user,
      isLoading,
      isAuthenticated: user !== null,
      hasWorkspace: user !== null && user.currentWorkspaceId !== null,
      refreshUser,
      logout,
    }),
    [user, isLoading, refreshUser, logout],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used inside <AuthProvider>')
  return ctx
}
