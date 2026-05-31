import { useEffect, useState } from 'react'
import { useWorkspaces } from '../hooks/useWorkspaces'
import './WorkspaceSelectionModal.css'

interface WorkspaceSelectionModalProps {
  isOpen: boolean
  /**
   * When true the modal cannot be dismissed (no overlay-click / Cancel). Used for
   * the hard-block case where the user has no valid workspace at all and cannot
   * use the app until they select or create one.
   */
  blocking?: boolean
  /** Optional banner explaining why the modal appeared (e.g. lost access). */
  notice?: string
  onClose?: () => void
}

/**
 * Workspace select/create surface. Switching reloads the app so all
 * workspace-scoped data refetches under the new context — there is no silent
 * automatic retry of the user's previous action (per the strict workspace
 * contract, the user re-initiates it themselves after switching).
 */
export function WorkspaceSelectionModal({ isOpen, blocking = false, notice, onClose }: WorkspaceSelectionModalProps) {
  const { workspaces, isLoading, switchTo, create, error, refresh } = useWorkspaces()
  const [creating, setCreating] = useState(false)
  const [newName, setNewName] = useState('')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (isOpen) void refresh()
  }, [isOpen, refresh])

  if (!isOpen) return null

  async function onSwitch(id: string) {
    if (busy) return
    setBusy(true)
    try {
      // switchTo reloads on success; the user re-initiates their action afterwards.
      await switchTo(id)
    } catch {
      // surfaced via useWorkspaces.error
    } finally {
      setBusy(false)
    }
  }

  async function onCreate(e: React.FormEvent) {
    e.preventDefault()
    if (busy) return
    const trimmed = newName.trim()
    if (!trimmed) return
    setBusy(true)
    try {
      await create(trimmed)
      setCreating(false)
      setNewName('')
      // Reload so the freshly-created (now current) workspace context applies.
      window.location.reload()
    } catch {
      // surfaced via useWorkspaces.error
    } finally {
      setBusy(false)
    }
  }

  const dismiss = blocking ? undefined : onClose

  return (
    <div
      className="ws-select-overlay"
      onClick={() => dismiss?.()}
      role="dialog"
      aria-modal="true"
      aria-labelledby="ws-select-title"
    >
      <div className="ws-select-modal" onClick={(e) => e.stopPropagation()}>
        <div className="ws-select-header">
          <h3 id="ws-select-title">Select a workspace</h3>
          {!blocking && (
            <button type="button" className="ws-select-close" onClick={() => dismiss?.()} aria-label="Close">
              ✕
            </button>
          )}
        </div>

        {notice && <div className="ws-select-notice" role="alert">{notice}</div>}

        <p className="ws-select-subtitle">
          Each workspace can have its own connected account. Choose where your posts,
          uploads and connections should go.
        </p>

        <div className="ws-select-list">
          {isLoading && <div className="ws-select-loading">Loading workspaces…</div>}
          {!isLoading && workspaces.length === 0 && (
            <div className="ws-select-empty">You don’t have any workspaces yet. Create one to get started.</div>
          )}
          {!isLoading && workspaces.map((ws) => (
            <button
              key={ws.id}
              type="button"
              className={`ws-select-item ${ws.isCurrent ? 'is-current' : ''}`}
              onClick={() => { if (!ws.isCurrent) void onSwitch(ws.id) }}
              disabled={busy}
            >
              <span className="ws-select-item-name">{ws.name}</span>
              <span className="ws-select-item-role">{ws.role}</span>
              {ws.isCurrent && <span className="ws-select-item-current">Current</span>}
            </button>
          ))}
        </div>

        <div className="ws-select-divider" />

        {!creating ? (
          <button
            type="button"
            className="ws-select-create-btn"
            onClick={() => setCreating(true)}
            disabled={busy}
          >
            + Create new workspace
          </button>
        ) : (
          <form className="ws-select-create-form" onSubmit={onCreate}>
            <input
              autoFocus
              type="text"
              className="ws-select-create-input"
              placeholder="Workspace name"
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              maxLength={200}
              disabled={busy}
            />
            <div className="ws-select-create-actions">
              <button type="submit" disabled={busy || !newName.trim()}>Create</button>
              <button type="button" onClick={() => { setCreating(false); setNewName('') }} disabled={busy}>
                Cancel
              </button>
            </div>
          </form>
        )}

        {error && <div className="ws-select-error">{describeError(error)}</div>}
      </div>
    </div>
  )
}

function describeError(err: string): string {
  if (err.includes('workspaces_switch_forbidden')) {
    return 'You are not a member of that workspace. Pick another one.'
  }
  if (err.includes('workspaces_create_failed_400')) {
    return 'Could not create workspace. Check the name and try again.'
  }
  return 'Something went wrong. Please try again.'
}
