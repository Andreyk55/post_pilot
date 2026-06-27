import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'

/**
 * The account module reads config.apiBaseUrl at import time, so each test re-mocks
 * the config and re-imports via vi.resetModules() (mirrors media.test.ts).
 */
async function importWithBase(apiBaseUrl: string) {
  vi.resetModules()
  vi.doMock('../config/appConfig', () => ({ config: { apiBaseUrl } }))
  return await import('./account')
}

describe('isDeleteAccountConfirmed', () => {
  afterEach(() => vi.doUnmock('../config/appConfig'))

  it('is true only for the exact phrase', async () => {
    const { isDeleteAccountConfirmed } = await importWithBase('')
    expect(isDeleteAccountConfirmed('DELETE MY ACCOUNT')).toBe(true)
    expect(isDeleteAccountConfirmed('delete my account')).toBe(false)
    expect(isDeleteAccountConfirmed('DELETE MY ACCOUNT ')).toBe(false)
    expect(isDeleteAccountConfirmed(' DELETE MY ACCOUNT')).toBe(false)
    expect(isDeleteAccountConfirmed('DELETE ACCOUNT')).toBe(false)
    expect(isDeleteAccountConfirmed('')).toBe(false)
  })
})

describe('accountApi.deleteAccount', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    fetchMock.mockReset()
    vi.stubGlobal('fetch', fetchMock)
  })
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.doUnmock('../config/appConfig')
  })

  it('sends DELETE /account carrying ONLY the confirmation text (never a userId/accountId)', async () => {
    fetchMock.mockResolvedValue({ ok: true })
    const { accountApi } = await importWithBase('')

    await accountApi.deleteAccount('DELETE MY ACCOUNT')

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/account')
    expect(init.method).toBe('DELETE')

    const body = JSON.parse(init.body)
    expect(body).toEqual({ confirmationText: 'DELETE MY ACCOUNT' })
    expect(body).not.toHaveProperty('userId')
    expect(body).not.toHaveProperty('accountId')
    expect(body).not.toHaveProperty('id')
  })

  it('throws on a non-ok response', async () => {
    fetchMock.mockResolvedValue({ ok: false, status: 400 })
    const { accountApi } = await importWithBase('')

    await expect(accountApi.deleteAccount('DELETE MY ACCOUNT')).rejects.toThrow()
  })
})
