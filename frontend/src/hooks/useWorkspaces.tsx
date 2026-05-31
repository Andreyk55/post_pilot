import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { workspacesApi, type WorkspaceSummary } from '../api/workspaces'
import { useAuth } from './useAuth'
import { WorkspacesContext, type WorkspacesContextValue } from './workspacesContext'

export function WorkspacesProvider({ children }: { children: ReactNode }) {
  const { isAuthenticated, refreshUser } = useAuth()
  const [workspaces, setWorkspaces] = useState<WorkspaceSummary[]>([])
  const [isLoading, setIsLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    if (!isAuthenticated) {
      setWorkspaces([])
      return
    }
    setIsLoading(true)
    setError(null)
    try {
      const list = await workspacesApi.list()
      setWorkspaces(list)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setIsLoading(false)
    }
  }, [isAuthenticated])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const switchTo = useCallback(async (workspaceId: string) => {
    setError(null)
    try {
      await workspacesApi.switchTo(workspaceId)
      // Pull fresh /me so the workspace name in the sidebar updates, then refresh list.
      await refreshUser()
      await refresh()
      // Force any cached workspace-scoped data to reload by hard-reloading.
      // Simpler than threading per-feature reload hooks for MVP.
      window.location.reload()
    } catch (e) {
      setError((e as Error).message)
      throw e
    }
  }, [refresh, refreshUser])

  const create = useCallback(async (name: string) => {
    setError(null)
    try {
      const ws = await workspacesApi.create(name)
      await refreshUser()
      await refresh()
      return ws
    } catch (e) {
      setError((e as Error).message)
      throw e
    }
  }, [refresh, refreshUser])

  const value = useMemo<WorkspacesContextValue>(() => ({
    workspaces,
    isLoading,
    error,
    refresh,
    switchTo,
    create,
  }), [workspaces, isLoading, error, refresh, switchTo, create])

  return <WorkspacesContext.Provider value={value}>{children}</WorkspacesContext.Provider>
}
