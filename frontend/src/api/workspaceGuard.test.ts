import { describe, it, expect, vi } from 'vitest'
import {
  parseWorkspaceGuard,
  WorkspaceGuardError,
  guardWorkspaceAction,
  NO_WORKSPACE_ACTION_MESSAGE,
} from './workspaceGuard'

describe('parseWorkspaceGuard', () => {
  it('matches 409 WORKSPACE_NOT_SELECTED', () => {
    const detail = parseWorkspaceGuard(409, { code: 'WORKSPACE_NOT_SELECTED', error: 'pick one' })
    expect(detail).toEqual({ code: 'WORKSPACE_NOT_SELECTED', status: 409, message: 'pick one' })
  })

  it('matches 403 WORKSPACE_FORBIDDEN', () => {
    const detail = parseWorkspaceGuard(403, { code: 'WORKSPACE_FORBIDDEN', error: 'no access' })
    expect(detail).toEqual({ code: 'WORKSPACE_FORBIDDEN', status: 403, message: 'no access' })
  })

  it('tolerates a missing error message', () => {
    const detail = parseWorkspaceGuard(409, { code: 'WORKSPACE_NOT_SELECTED' })
    expect(detail).toEqual({ code: 'WORKSPACE_NOT_SELECTED', status: 409, message: undefined })
  })

  it('ignores non-workspace error codes even on 409/403', () => {
    expect(parseWorkspaceGuard(409, { code: 'INTEGRATION_DISCONNECTED' })).toBeNull()
    expect(parseWorkspaceGuard(403, { error: 'untrusted_origin' })).toBeNull()
  })

  it('ignores unrelated status codes', () => {
    expect(parseWorkspaceGuard(200, { code: 'WORKSPACE_NOT_SELECTED' })).toBeNull()
    expect(parseWorkspaceGuard(500, { code: 'WORKSPACE_FORBIDDEN' })).toBeNull()
    // Importantly, a 404 (cross-workspace resource hidden) must NOT be treated as a
    // workspace-guard event — that path stays a normal not-found.
    expect(parseWorkspaceGuard(404, { code: 'WORKSPACE_FORBIDDEN' })).toBeNull()
  })

  it('tolerates non-object / null bodies', () => {
    expect(parseWorkspaceGuard(409, null)).toBeNull()
    expect(parseWorkspaceGuard(409, 'plain text')).toBeNull()
    expect(parseWorkspaceGuard(409, undefined)).toBeNull()
  })
})

describe('WorkspaceGuardError', () => {
  it('carries the code and status, and uses message when present', () => {
    const err = new WorkspaceGuardError({ code: 'WORKSPACE_FORBIDDEN', status: 403, message: 'gone' })
    expect(err).toBeInstanceOf(Error)
    expect(err.code).toBe('WORKSPACE_FORBIDDEN')
    expect(err.status).toBe(403)
    expect(err.message).toBe('gone')
  })

  it('falls back to the code as the message when none is given', () => {
    const err = new WorkspaceGuardError({ code: 'WORKSPACE_NOT_SELECTED', status: 409 })
    expect(err.message).toBe('WORKSPACE_NOT_SELECTED')
  })
})

describe('guardWorkspaceAction', () => {
  it('allows the action and stays silent when a workspace is selected', () => {
    const notify = vi.fn()
    const openSelector = vi.fn()

    const allowed = guardWorkspaceAction(true, { notify, openSelector })

    expect(allowed).toBe(true)
    expect(notify).not.toHaveBeenCalled()
    expect(openSelector).not.toHaveBeenCalled()
  })

  it('blocks the action, notifies, and opens the selector when no workspace', () => {
    const notify = vi.fn()
    const openSelector = vi.fn()

    const allowed = guardWorkspaceAction(false, { notify, openSelector })

    expect(allowed).toBe(false)
    expect(notify).toHaveBeenCalledWith(NO_WORKSPACE_ACTION_MESSAGE)
    expect(notify).toHaveBeenCalledWith('Select a workspace before continuing.')
    expect(openSelector).toHaveBeenCalledTimes(1)
  })

  it('does not auto-select a workspace (only opens the selector for the user)', () => {
    // There is no "switch"/"select" side effect available to the guard — it can
    // only prompt. This pins the no-auto-select requirement.
    const openSelector = vi.fn()
    guardWorkspaceAction(false, { notify: vi.fn(), openSelector })
    // openSelector merely shows the picker; it carries no target workspace id.
    expect(openSelector).toHaveBeenCalledWith()
  })
})
