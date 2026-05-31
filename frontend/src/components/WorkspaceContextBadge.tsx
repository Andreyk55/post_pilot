import { useAuth } from '../hooks/useAuth'
import './WorkspaceContextBadge.css'

interface WorkspaceContextBadgeProps {
  /** Verb describing the action this badge is attached to, e.g. "Posting to". */
  action?: string
}

/**
 * Read-only inline indicator that makes it visually unambiguous which workspace
 * (and therefore which connected account) an action will apply to. Shown next to
 * create/upload/schedule/publish/connect surfaces so a user with multiple
 * workspaces can see — at a glance — which one they're acting in.
 *
 * Workspace switching/creation is centralized in the sidebar workspace selector
 * (<WorkspaceSwitcher>) ONLY. This badge is therefore a non-interactive label:
 * it is a <span>, has no click handler, no "Switch" affordance, and never opens
 * the workspace selector.
 */
export function WorkspaceContextBadge({ action = 'Workspace' }: WorkspaceContextBadgeProps) {
  const { user } = useAuth()

  const name = user?.workspaceName?.trim()

  return (
    <span className={`ws-badge ${name ? '' : 'ws-badge--unset'}`}>
      <span className="ws-badge__icon" aria-hidden>🗂️</span>
      <span className="ws-badge__label">{action}:</span>
      <span className="ws-badge__name">{name || 'No workspace selected'}</span>
    </span>
  )
}
