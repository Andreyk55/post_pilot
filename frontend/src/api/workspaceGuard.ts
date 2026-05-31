/**
 * Cross-cutting handling for the backend's strict workspace-resolution contract.
 *
 * Workspace-scoped API calls may fail with:
 *   - 409 + { code: "WORKSPACE_NOT_SELECTED" } — no valid current workspace is
 *     selected (none chosen, or the selected one was deleted). The user must pick
 *     a workspace before the action can proceed.
 *   - 403 + { code: "WORKSPACE_FORBIDDEN" }   — the selected workspace exists but
 *     the user is no longer a member. We must re-sync and force a re-selection.
 *
 * The backend NEVER silently falls back to another workspace, so the frontend must
 * not retry automatically with a different one either. The fetch interceptor
 * (see httpClient.ts) detects these responses and dispatches a DOM CustomEvent;
 * a single top-level handler (<WorkspaceGuard>) reacts (toast + open selector),
 * while the originating call still rejects so the in-flight action stops.
 */

export type WorkspaceErrorCode = 'WORKSPACE_NOT_SELECTED' | 'WORKSPACE_FORBIDDEN'

export const WORKSPACE_GUARD_EVENT = 'postpilot:workspace-guard'

export interface WorkspaceGuardDetail {
  code: WorkspaceErrorCode
  /** HTTP status that carried the error (409 or 403). */
  status: number
  /** Best-effort human-readable message from the backend, if any. */
  message?: string
}

/** Error thrown by API modules when a workspace-scoped call is blocked. */
export class WorkspaceGuardError extends Error {
  readonly code: WorkspaceErrorCode
  readonly status: number

  constructor(detail: WorkspaceGuardDetail) {
    super(detail.message ?? detail.code)
    this.name = 'WorkspaceGuardError'
    this.code = detail.code
    this.status = detail.status
  }
}

/**
 * Inspects a (possibly already-read) response body for the workspace error codes.
 * Returns the typed detail when matched, otherwise null. Tolerant of non-JSON
 * bodies and missing fields.
 */
export function parseWorkspaceGuard(status: number, body: unknown): WorkspaceGuardDetail | null {
  if (status !== 409 && status !== 403) return null
  if (typeof body !== 'object' || body === null) return null

  const code = (body as { code?: unknown }).code
  const message = (body as { error?: unknown }).error

  if (code === 'WORKSPACE_NOT_SELECTED' || code === 'WORKSPACE_FORBIDDEN') {
    return {
      code,
      status,
      message: typeof message === 'string' ? message : undefined,
    }
  }
  return null
}

/** Fire-and-forget broadcast so the app shell can react regardless of caller. */
export function emitWorkspaceGuard(detail: WorkspaceGuardDetail): void {
  if (typeof window === 'undefined') return
  window.dispatchEvent(new CustomEvent<WorkspaceGuardDetail>(WORKSPACE_GUARD_EVENT, { detail }))
}

export function onWorkspaceGuard(handler: (detail: WorkspaceGuardDetail) => void): () => void {
  const listener = (e: Event) => handler((e as CustomEvent<WorkspaceGuardDetail>).detail)
  window.addEventListener(WORKSPACE_GUARD_EVENT, listener)
  return () => window.removeEventListener(WORKSPACE_GUARD_EVENT, listener)
}

/** User-facing message shown when a workspace-scoped action is attempted with no workspace. */
export const NO_WORKSPACE_ACTION_MESSAGE = 'Select a workspace before continuing.'

interface GuardWorkspaceActionHandlers {
  /** Show the user-facing "select a workspace" message (e.g. a toast). */
  notify: (message: string) => void
  /** Open the workspace selector so the user can pick/create one. */
  openSelector: () => void
}

/**
 * Pre-flight guard for a workspace-scoped UI action (e.g. connect/disconnect a
 * provider). Returns true when the action is allowed to proceed. When no workspace
 * is selected it notifies, opens the selector, and returns false — it never
 * auto-selects a workspace and never retries the action.
 *
 * Extracted as a pure function so the gate is unit-testable without a DOM.
 */
export function guardWorkspaceAction(
  hasWorkspace: boolean,
  handlers: GuardWorkspaceActionHandlers,
): boolean {
  if (hasWorkspace) return true
  handlers.notify(NO_WORKSPACE_ACTION_MESSAGE)
  handlers.openSelector()
  return false
}
