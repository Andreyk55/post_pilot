import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { emitWorkspaceGuard, onWorkspaceGuard, type WorkspaceGuardDetail } from './workspaceGuard'

/**
 * The guard event layer talks to `window` (a DOM EventTarget). The project has no
 * jsdom test env, so we stand up a minimal `window` backed by Node's global
 * EventTarget for the duration of these tests. Node 20+/CustomEvent is global.
 */
describe('workspace guard events', () => {
  let originalWindow: typeof globalThis.window | undefined

  beforeEach(() => {
    originalWindow = (globalThis as { window?: typeof globalThis.window }).window
    const target = new EventTarget() as unknown as typeof globalThis.window
    ;(globalThis as { window?: unknown }).window = target
  })

  afterEach(() => {
    ;(globalThis as { window?: unknown }).window = originalWindow
  })

  it('delivers an emitted detail to a subscriber', () => {
    const received: WorkspaceGuardDetail[] = []
    const off = onWorkspaceGuard((d) => received.push(d))

    emitWorkspaceGuard({ code: 'WORKSPACE_NOT_SELECTED', status: 409, message: 'pick one' })

    expect(received).toHaveLength(1)
    expect(received[0]).toEqual({ code: 'WORKSPACE_NOT_SELECTED', status: 409, message: 'pick one' })
    off()
  })

  it('stops delivering after unsubscribe', () => {
    const handler = vi.fn()
    const off = onWorkspaceGuard(handler)
    off()

    emitWorkspaceGuard({ code: 'WORKSPACE_FORBIDDEN', status: 403 })

    expect(handler).not.toHaveBeenCalled()
  })

  it('fans out to multiple subscribers', () => {
    const a = vi.fn()
    const b = vi.fn()
    const offA = onWorkspaceGuard(a)
    const offB = onWorkspaceGuard(b)

    emitWorkspaceGuard({ code: 'WORKSPACE_FORBIDDEN', status: 403 })

    expect(a).toHaveBeenCalledTimes(1)
    expect(b).toHaveBeenCalledTimes(1)
    offA()
    offB()
  })
})
