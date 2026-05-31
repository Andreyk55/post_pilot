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

    const allowed = guardWorkspaceAction(true, { notify })

    expect(allowed).toBe(true)
    expect(notify).not.toHaveBeenCalled()
  })

  it('blocks the action and notifies (steering to the sidebar) when no workspace', () => {
    const notify = vi.fn()

    const allowed = guardWorkspaceAction(false, { notify })

    expect(allowed).toBe(false)
    expect(notify).toHaveBeenCalledWith(NO_WORKSPACE_ACTION_MESSAGE)
    // The message must point the user at the sidebar selector — the only place
    // workspace switching/creation lives.
    expect(notify).toHaveBeenCalledWith('Select a workspace from the sidebar before continuing.')
    expect(NO_WORKSPACE_ACTION_MESSAGE).toMatch(/sidebar/i)
  })

  it('does not open the workspace selector for the user (no auto-open)', () => {
    // The guard has no selector-opening side effect at all: switching/creation is
    // the sidebar selector's job, and the blocking <WorkspaceGuard> modal already
    // covers the genuinely-no-workspace case. The guard only notifies.
    const notify = vi.fn()
    const handlers = { notify }

    guardWorkspaceAction(false, handlers)

    // The handlers object carries no openSelector — there is nothing to call.
    expect(handlers).not.toHaveProperty('openSelector')
    expect(notify).toHaveBeenCalledTimes(1)
  })
})
