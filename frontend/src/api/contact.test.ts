import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'

/**
 * The contact module reads config.apiBaseUrl at import time, so each test re-mocks the
 * config and re-imports via vi.resetModules() (mirrors account.test.ts).
 */
async function importWithBase(apiBaseUrl: string) {
  vi.resetModules()
  vi.doMock('../config/appConfig', () => ({ config: { apiBaseUrl } }))
  return await import('./contact')
}

describe('isContactFormValid', () => {
  afterEach(() => vi.doUnmock('../config/appConfig'))

  it('requires non-whitespace subject AND message', async () => {
    const { isContactFormValid } = await importWithBase('')
    expect(isContactFormValid('Subject', 'Message')).toBe(true)
    expect(isContactFormValid('', 'Message')).toBe(false)
    expect(isContactFormValid('Subject', '')).toBe(false)
    expect(isContactFormValid('   ', 'Message')).toBe(false)
    expect(isContactFormValid('Subject', '   ')).toBe(false)
    expect(isContactFormValid('', '')).toBe(false)
  })
})

describe('supportApi.sendContactMessage', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    fetchMock.mockReset()
    vi.stubGlobal('fetch', fetchMock)
  })
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.doUnmock('../config/appConfig')
  })

  function okJson(value: unknown) {
    return { ok: true, json: () => Promise.resolve(value) }
  }

  it('POSTs to /support/contact carrying ONLY subject/message (never userId/accountId/email)', async () => {
    fetchMock.mockResolvedValue(okJson({ id: '1', status: 'New', createdAt: 'now' }))
    const { supportApi } = await importWithBase('')

    await supportApi.sendContactMessage({ subject: 'Help', message: 'Please help' })

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/support/contact')
    expect(init.method).toBe('POST')

    const body = JSON.parse(init.body)
    expect(body).toEqual({ subject: 'Help', message: 'Please help' })
    expect(body).not.toHaveProperty('userId')
    expect(body).not.toHaveProperty('accountId')
    expect(body).not.toHaveProperty('email')
    expect(body).not.toHaveProperty('workspaceId')
  })

  it('includes category only when one is chosen', async () => {
    fetchMock.mockResolvedValue(okJson({ id: '1', status: 'New', createdAt: 'now' }))
    const { supportApi } = await importWithBase('')

    await supportApi.sendContactMessage({
      category: 'DataDeletion',
      subject: 'Q',
      message: 'M',
    })

    const body = JSON.parse(fetchMock.mock.calls[0][1].body)
    expect(body).toEqual({ subject: 'Q', message: 'M', category: 'DataDeletion' })
  })

  it('omits category when null/undefined', async () => {
    fetchMock.mockResolvedValue(okJson({ id: '1', status: 'New', createdAt: 'now' }))
    const { supportApi } = await importWithBase('')

    await supportApi.sendContactMessage({ category: null, subject: 'Q', message: 'M' })

    const body = JSON.parse(fetchMock.mock.calls[0][1].body)
    expect(body).not.toHaveProperty('category')
  })

  it('trims subject and message before sending', async () => {
    fetchMock.mockResolvedValue(okJson({ id: '1', status: 'New', createdAt: 'now' }))
    const { supportApi } = await importWithBase('')

    await supportApi.sendContactMessage({ subject: '  Hi  ', message: '  body  ' })

    const body = JSON.parse(fetchMock.mock.calls[0][1].body)
    expect(body).toEqual({ subject: 'Hi', message: 'body' })
  })

  it('returns the parsed response on success', async () => {
    fetchMock.mockResolvedValue(okJson({ id: 'abc', status: 'New', createdAt: '2026-06-27' }))
    const { supportApi } = await importWithBase('')

    const result = await supportApi.sendContactMessage({ subject: 'Q', message: 'M' })
    expect(result).toEqual({ id: 'abc', status: 'New', createdAt: '2026-06-27' })
  })

  it('throws on a non-ok response', async () => {
    fetchMock.mockResolvedValue({ ok: false, status: 500 })
    const { supportApi } = await importWithBase('')

    await expect(supportApi.sendContactMessage({ subject: 'Q', message: 'M' })).rejects.toThrow()
  })
})
