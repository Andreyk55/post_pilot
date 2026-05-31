import { useCallback, useEffect, useRef, useState } from 'react'
import { useAuth } from '../hooks/useAuth'
import { useWorkspaces } from '../hooks/useWorkspaces'
import { onWorkspaceGuard, type WorkspaceErrorCode } from '../api/workspaceGuard'
import { Toast } from './Toast'
import { WorkspaceSelectionModal } from './WorkspaceSelectionModal'

const MESSAGES: Record<WorkspaceErrorCode, string> = {
  WORKSPACE_NOT_SELECTED: 'Please select a workspace before continuing.',
  WORKSPACE_FORBIDDEN: 'You no longer have access to this workspace. Please select another workspace.',
}

/**
 * App-shell handler for the backend's strict workspace-resolution contract.
 *
 * Responsibilities:
 *   1. Hard-block: if the signed-in user has no valid workspace
 *      (currentWorkspaceId === null), force the (non-dismissable) workspace
 *      selector so no workspace-scoped action can run.
 *   2. React to mid-action failures broadcast by the fetch interceptor:
 *        - WORKSPACE_NOT_SELECTED (409): toast + open selector.
 *        - WORKSPACE_FORBIDDEN  (403): re-sync /me + workspace list, toast,
 *          open selector. Never silently switch to another workspace.
 *
 * It deliberately does NOT auto-retry the user's original action; after the
 * user selects/switches a workspace they re-initiate it themselves.
 */
export function WorkspaceGuard() {
  const { hasWorkspace, isAuthenticated, refreshUser } = useAuth()
  const { selectorOpen, openSelector, closeSelector, refresh } = useWorkspaces()

  const [toast, setToast] = useState<string | null>(null)
  // The most recent guard reason, used to tailor the modal notice.
  const [notice, setNotice] = useState<string | undefined>(undefined)
  // Guards against overlapping re-syncs when several calls fail at once.
  const resyncing = useRef(false)

  const handleGuard = useCallback(async (code: WorkspaceErrorCode) => {
    setToast(MESSAGES[code])
    setNotice(MESSAGES[code])

    if (code === 'WORKSPACE_FORBIDDEN' && !resyncing.current) {
      // Access was revoked or the selection points somewhere we can't use. Re-sync
      // server truth (/me may now report null) and the membership list, but do NOT
      // pick a workspace for the user.
      resyncing.current = true
      try {
        await Promise.all([refreshUser(), refresh()])
      } finally {
        resyncing.current = false
      }
    }

    openSelector()
  }, [openSelector, refresh, refreshUser])

  useEffect(() => {
    return onWorkspaceGuard((detail) => { void handleGuard(detail.code) })
  }, [handleGuard])

  // Hard-block: signed in but no usable workspace. Force selection up front so the
  // user can't trigger workspace-scoped actions that would just fail.
  const mustSelect = isAuthenticated && !hasWorkspace

  const modalOpen = mustSelect || selectorOpen

  return (
    <>
      <Toast
        message={toast ?? ''}
        type="error"
        isVisible={toast !== null}
        onClose={() => setToast(null)}
      />
      <WorkspaceSelectionModal
        isOpen={modalOpen}
        blocking={mustSelect}
        notice={mustSelect && !notice
          ? 'Select or create a workspace to start posting, uploading and connecting accounts.'
          : notice}
        onClose={() => { closeSelector(); setNotice(undefined) }}
      />
    </>
  )
}
