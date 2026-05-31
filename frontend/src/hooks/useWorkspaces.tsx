import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { workspacesApi, type WorkspaceSummary } from '../api/workspaces'
import { useAuth } from './useAuth'

interface WorkspacesContextValue {
  workspaces: WorkspaceSummary[]
  isLoading: boolean
  error: string | null
  refresh: () => Promise<void>
  /** Switches and refreshes auth so the rest of the app sees the new workspace. */
  switchTo: (workspaceId: string) => Promise<void>
  create: (name: string) => Promise<WorkspaceSummary>
  /** Whether the workspace selector modal is currently open. */
  selectorOpen: boolean
  /** Opens the workspace selector modal (e.g. after a WORKSPACE_NOT_SELECTED error). */
  openSelector: () => void
  /** Closes the workspace selector modal. */
  closeSelector: () => void
}

const WorkspacesContext = createContext<WorkspacesContextValue | undefined>(undefined)

export function WorkspacesProvider({ children }: { children: ReactNode }) {
  const { isAuthenticated, refreshUser } = useAuth()
  const [workspaces, setWorkspaces] = useState<WorkspaceSummary[]>([])
  const [isLoading, setIsLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [selectorOpen, setSelectorOpen] = useState(false)

  const openSelector = useCallback(() => setSelectorOpen(true), [])
  const closeSelector = useCallback(() => setSelectorOpen(false), [])

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
    selectorOpen,
    openSelector,
    closeSelector,
  }), [workspaces, isLoading, error, refresh, switchTo, create, selectorOpen, openSelector, closeSelector])

  return <WorkspacesContext.Provider value={value}>{children}</WorkspacesContext.Provider>
}

export function useWorkspaces(): WorkspacesContextValue {
  const ctx = useContext(WorkspacesContext)
  if (!ctx) throw new Error('useWorkspaces must be used inside <WorkspacesProvider>')
  return ctx
}
