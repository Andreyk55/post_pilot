import { afterEach, describe, expect, it, vi } from 'vitest'
import { createUploadClientId } from './uploadClientId'

describe('createUploadClientId', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('uses crypto.randomUUID when available', () => {
    const randomUUID = vi.fn(() => '11111111-2222-3333-4444-555555555555')
    vi.stubGlobal('crypto', { randomUUID })

    expect(createUploadClientId()).toBe('11111111-2222-3333-4444-555555555555')
    expect(randomUUID).toHaveBeenCalledTimes(1)
  })

  it('falls back to a string id when crypto.randomUUID is unavailable', () => {
    // crypto exists but without randomUUID (older browser / limited runtime).
    vi.stubGlobal('crypto', {})

    const id = createUploadClientId()
    expect(typeof id).toBe('string')
    expect(id).toMatch(/^\d+-[a-z0-9]+$/)
  })

  it('falls back when crypto itself is undefined', () => {
    vi.stubGlobal('crypto', undefined)

    const id = createUploadClientId()
    expect(id).toMatch(/^\d+-[a-z0-9]+$/)
  })

  it('returns non-empty, distinct ids', () => {
    const a = createUploadClientId()
    const b = createUploadClientId()

    expect(a).toBeTruthy()
    expect(b).toBeTruthy()
    expect(a.length).toBeGreaterThan(0)
    expect(a).not.toBe(b)
  })
})
