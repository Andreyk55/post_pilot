import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'

/**
 * getMediaUrl must anchor media file URLs to the configured API origin in
 * production (where the SPA and API are on different origins) while keeping the
 * relative "/api/..." form in local/dev so the Vite proxy still works.
 *
 * The module reads config.apiBaseUrl at import time, so each test re-mocks the
 * config and re-imports the module via vi.resetModules().
 */
async function importWithApiBaseUrl(apiBaseUrl: string) {
  vi.resetModules()
  vi.doMock('../config/appConfig', () => ({
    config: { apiBaseUrl },
  }))
  return await import('./media')
}

describe('getMediaUrl', () => {
  beforeEach(() => {
    vi.resetModules()
  })
  afterEach(() => {
    vi.doUnmock('../config/appConfig')
  })

  it('anchors to the absolute API base in production (no double /api)', async () => {
    const { getMediaUrl } = await importWithApiBaseUrl('https://post-pilot.cloud-ip.cc/api')

    const url = getMediaUrl('11111111-1111-1111-1111-111111111111')

    expect(url).toBe(
      'https://post-pilot.cloud-ip.cc/api/media/11111111-1111-1111-1111-111111111111/file',
    )
  })

  it('builds the thumbnail variant URL', async () => {
    const { getMediaUrl } = await importWithApiBaseUrl('https://post-pilot.cloud-ip.cc/api')
    expect(getMediaUrl('11111111-1111-1111-1111-111111111111', 'thumbnail')).toBe(
      'https://post-pilot.cloud-ip.cc/api/media/11111111-1111-1111-1111-111111111111/file?variant=thumbnail',
    )
  })

  it('does not produce a double slash when the base has a trailing slash', async () => {
    const { getMediaUrl } = await importWithApiBaseUrl('https://post-pilot.cloud-ip.cc/api/')
    expect(getMediaUrl('11111111-1111-1111-1111-111111111111')).toBe(
      'https://post-pilot.cloud-ip.cc/api/media/11111111-1111-1111-1111-111111111111/file',
    )
  })

  it('keeps a relative path when apiBaseUrl is empty (dev/proxy)', async () => {
    const { getMediaUrl } = await importWithApiBaseUrl('')
    expect(getMediaUrl('11111111-1111-1111-1111-111111111111')).toBe('/api/media/11111111-1111-1111-1111-111111111111/file')
  })

  it('keeps a relative path when apiBaseUrl is itself relative', async () => {
    const { getMediaUrl } = await importWithApiBaseUrl('/api')
    expect(getMediaUrl('11111111-1111-1111-1111-111111111111')).toBe('/api/media/11111111-1111-1111-1111-111111111111/file')
  })

  it('encodes the mediaId and variant safely', async () => {
    const { getMediaUrl } = await importWithApiBaseUrl('')
    expect(getMediaUrl('abc def', 'thumbnail')).toBe('/api/media/abc%20def/file?variant=thumbnail')
  })

  it('returns null for null/undefined/empty keys', async () => {
    const { getMediaUrl } = await importWithApiBaseUrl('https://post-pilot.cloud-ip.cc/api')
    expect(getMediaUrl(null)).toBeNull()
    expect(getMediaUrl(undefined)).toBeNull()
    expect(getMediaUrl('')).toBeNull()
  })
})

describe('mediaApi.initUpload', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    fetchMock.mockReset()
    vi.stubGlobal('fetch', fetchMock)
    vi.resetModules()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.doUnmock('../config/appConfig')
  })

  it('surfaces the backend quota detail message from a 429 problem-details response', async () => {
    fetchMock.mockResolvedValue({
      ok: false,
      json: async () => ({
        title: 'Media upload quota exceeded',
        detail: 'Daily media upload limit reached. You can upload more media when your quota resets.',
        code: 'MEDIA_UPLOAD_QUOTA_EXCEEDED',
        remaining: 0,
      }),
    })
    const { mediaApi } = await importWithApiBaseUrl('')

    await expect(mediaApi.initUpload({
      fileName: 'photo.png',
      contentType: 'image/png',
      sizeBytes: 123,
      platform: 'Facebook',
    })).rejects.toThrow('Daily media upload limit reached. You can upload more media when your quota resets.')
  })

  it('falls back cleanly when the backend response has no usable detail', async () => {
    fetchMock.mockResolvedValue({
      ok: false,
      json: async () => ({}),
    })
    const { mediaApi } = await importWithApiBaseUrl('')

    await expect(mediaApi.initUpload({
      fileName: 'photo.png',
      contentType: 'image/png',
      sizeBytes: 123,
      platform: 'Instagram',
    })).rejects.toThrow('Failed to initiate upload')
  })
})
