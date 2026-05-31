import { useAuth } from '../hooks/useAuth'
import { useWorkspaces } from '../hooks/useWorkspaces'
import './WorkspaceContextBadge.css'

interface WorkspaceContextBadgeProps {
  /** Verb describing the action this badge is attached to, e.g. "Posting to". */
  action?: string
}

/**
 * Inline badge that makes it visually unambiguous which workspace (and therefore
 * which connected account) an action will apply to. Shown next to create/upload/
 * schedule/publish/connect surfaces so a user with multiple workspaces can never
 * accidentally act in the wrong one. Clicking opens the workspace selector.
 */
export function WorkspaceContextBadge({ action = 'Workspace' }: WorkspaceContextBadgeProps) {
  const { user } = useAuth()
  const { openSelector } = useWorkspaces()

  const name = user?.workspaceName?.trim()

  return (
    <button
      type="button"
      className={`ws-badge ${name ? '' : 'ws-badge--unset'}`}
      onClick={openSelector}
      title="Switch workspace"
    >
      <span className="ws-badge__icon" aria-hidden>🗂️</span>
      <span className="ws-badge__label">{action}:</span>
      <span className="ws-badge__name">{name || 'No workspace selected'}</span>
      <span className="ws-badge__switch">Switch</span>
    </button>
  )
}
