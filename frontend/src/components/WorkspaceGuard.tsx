import { useCallback, useEffect, useRef, useState } from 'react'
import { useAuth } from '../hooks/useAuth'
import { useWorkspaces } from '../hooks/workspacesContext'
import { onWorkspaceGuard, type WorkspaceErrorCode } from '../api/workspaceGuard'
import { Toast } from './Toast'
import './WorkspaceGuard.css'

/** Message shown for each mid-action guard event (toast only — no auto-open). */
const MESSAGES: Record<WorkspaceErrorCode, string> = {
  WORKSPACE_NOT_SELECTED: 'Select a workspace from the sidebar before continuing.',
  WORKSPACE_FORBIDDEN: 'You no longer have access to this workspace. Select a workspace from the sidebar before continuing.',
}

/**
 * Blocking copy shown in the main content area when the signed-in user has no
 * usable workspace. Workspace switching/creation/management lives ONLY in the
 * sidebar workspace selector (<WorkspaceSwitcher>), so the guard never renders
 * its own switch/create/manage controls — it just blocks the page and points the
 * user at the sidebar.
 */
const BLOCKING_MESSAGE = 'Select a workspace from the sidebar before continuing.'

/**
 * App-shell handler for the backend's strict workspace-resolution contract.
 *
 * Responsibilities:
 *   1. Hard-block: if the signed-in user has no valid workspace
 *      (currentWorkspaceId === null), cover the page content with a blocking
 *      message so no workspace-scoped action can run. The block deliberately does
 *      NOT cover the sidebar, so the user can pick a workspace there.
 *   2. React to mid-action failures broadcast by the fetch interceptor:
 *        - WORKSPACE_NOT_SELECTED (409): toast only.
 *        - WORKSPACE_FORBIDDEN  (403): re-sync /me + workspace list, then toast.
 *          Re-syncing lets /me report the now-null workspace, which flips the
 *          hard-block on. We never silently switch to another workspace.
 *
 * It deliberately does NOT open the workspace selector for the user and does NOT
 * auto-retry the original action: the user switches workspace in the sidebar and
 * re-initiates their action themselves.
 */
export function WorkspaceGuard() {
  const { hasWorkspace, isAuthenticated, refreshUser } = useAuth()
  const { refresh } = useWorkspaces()

  const [toast, setToast] = useState<string | null>(null)
  // Guards against overlapping re-syncs when several calls fail at once.
  const resyncing = useRef(false)

  const handleGuard = useCallback(async (code: WorkspaceErrorCode) => {
    setToast(MESSAGES[code])

    if (code === 'WORKSPACE_FORBIDDEN' && !resyncing.current) {
      // Access was revoked or the selection points somewhere we can't use. Re-sync
      // server truth (/me may now report null) and the membership list, but do NOT
      // pick a workspace for the user. A null workspace then triggers the block.
      resyncing.current = true
      try {
        await Promise.all([refreshUser(), refresh()])
      } finally {
        resyncing.current = false
      }
    }
  }, [refresh, refreshUser])

  useEffect(() => {
    return onWorkspaceGuard((detail) => { void handleGuard(detail.code) })
  }, [handleGuard])

  // Hard-block: signed in but no usable workspace. Cover the page (not the sidebar)
  // so the user can't trigger workspace-scoped actions that would just fail.
  const mustSelect = isAuthenticated && !hasWorkspace

  return (
    <>
      <Toast
        message={toast ?? ''}
        type="error"
        isVisible={toast !== null}
        onClose={() => setToast(null)}
      />
      {mustSelect && (
        <div className="ws-guard-block" role="alertdialog" aria-modal="true" aria-labelledby="ws-guard-title">
          <div className="ws-guard-block__panel">
            <div className="ws-guard-block__icon" aria-hidden>🗂️</div>
            <h2 id="ws-guard-title" className="ws-guard-block__title">No workspace selected</h2>
            <p className="ws-guard-block__message">{BLOCKING_MESSAGE}</p>
            <p className="ws-guard-block__hint">
              Use the workspace selector at the top of the sidebar to choose or create a workspace.
            </p>
          </div>
        </div>
      )}
    </>
  )
}
