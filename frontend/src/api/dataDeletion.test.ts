import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'

async function importWithBase(apiBaseUrl: string) {
  vi.resetModules()
  vi.doMock('../config/appConfig', () => ({ config: { apiBaseUrl } }))
  return await import('./dataDeletion')
}

describe('dataDeletionApi.getStatus', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    fetchMock.mockReset()
    vi.stubGlobal('fetch', fetchMock)
  })
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.doUnmock('../config/appConfig')
  })

  it('GETs the status endpoint and returns the parsed body', async () => {
    const payload = {
      confirmationCode: 'ABC123',
      provider: 'Meta',
      status: 'Completed',
      requestedAt: '2026-06-27T00:00:00Z',
      completedAt: '2026-06-27T00:01:00Z',
    }
    fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => payload })
    const { dataDeletionApi } = await importWithBase('')

    const result = await dataDeletionApi.getStatus('ABC123')

    expect(fetchMock).toHaveBeenCalledWith('/data-deletion/status/ABC123')
    expect(result).toEqual(payload)
  })

  it('returns null on 404 (unknown code)', async () => {
    fetchMock.mockResolvedValue({ ok: false, status: 404 })
    const { dataDeletionApi } = await importWithBase('')

    expect(await dataDeletionApi.getStatus('nope')).toBeNull()
  })

  it('url-encodes the confirmation code', async () => {
    fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({}) })
    const { dataDeletionApi } = await importWithBase('https://api.example.com')

    await dataDeletionApi.getStatus('a/b c')

    expect(fetchMock).toHaveBeenCalledWith('https://api.example.com/data-deletion/status/a%2Fb%20c')
  })

  it('throws on a non-404 error response', async () => {
    fetchMock.mockResolvedValue({ ok: false, status: 500 })
    const { dataDeletionApi } = await importWithBase('')

    await expect(dataDeletionApi.getStatus('x')).rejects.toThrow()
  })
})
