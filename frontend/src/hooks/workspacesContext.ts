import { createContext, useContext } from 'react'
import { type WorkspaceSummary } from '../api/workspaces'

/**
 * Shape of the workspaces context. Kept in a non-component module (separate from
 * the <WorkspacesProvider> component) so the provider file only exports a React
 * component — this satisfies react-refresh/only-export-components and keeps Fast
 * Refresh working.
 *
 * Note: there is intentionally no selector-modal state here (no
 * selectorOpen/openSelector/closeSelector). Workspace switching/creation lives
 * solely in the sidebar <WorkspaceSwitcher>; <WorkspaceGuard> is message-only and
 * never auto-opens a selector.
 */
export interface WorkspacesContextValue {
  workspaces: WorkspaceSummary[]
  isLoading: boolean
  error: string | null
  refresh: () => Promise<void>
  /** Switches and refreshes auth so the rest of the app sees the new workspace. */
  switchTo: (workspaceId: string) => Promise<void>
  create: (name: string) => Promise<WorkspaceSummary>
}

export const WorkspacesContext = createContext<WorkspacesContextValue | undefined>(undefined)

export function useWorkspaces(): WorkspacesContextValue {
  const ctx = useContext(WorkspacesContext)
  if (!ctx) throw new Error('useWorkspaces must be used inside <WorkspacesProvider>')
  return ctx
}
