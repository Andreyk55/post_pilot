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

    const url = getMediaUrl('users/abc/workspaces/def/providers/meta-facebook/media/ghi/photo.png')

    expect(url).toBe(
      'https://post-pilot.cloud-ip.cc/api/media/files/users/abc/workspaces/def/providers/meta-facebook/media/ghi/photo.png',
    )
  })

  it('matches the exact expected production preview URL for a simple key', async () => {
    const { getMediaUrl } = await importWithApiBaseUrl('https://post-pilot.cloud-ip.cc/api')
    expect(getMediaUrl('media/xyz.jpg')).toBe(
      'https://post-pilot.cloud-ip.cc/api/media/files/media/xyz.jpg',
    )
  })

  it('does not produce a double slash when the base has a trailing slash', async () => {
    const { getMediaUrl } = await importWithApiBaseUrl('https://post-pilot.cloud-ip.cc/api/')
    expect(getMediaUrl('media/xyz.jpg')).toBe(
      'https://post-pilot.cloud-ip.cc/api/media/files/media/xyz.jpg',
    )
  })

  it('keeps a relative path when apiBaseUrl is empty (dev/proxy)', async () => {
    const { getMediaUrl } = await importWithApiBaseUrl('')
    expect(getMediaUrl('media/xyz.jpg')).toBe('/api/media/files/media/xyz.jpg')
  })

  it('keeps a relative path when apiBaseUrl is itself relative', async () => {
    const { getMediaUrl } = await importWithApiBaseUrl('/api')
    expect(getMediaUrl('media/xyz.jpg')).toBe('/api/media/files/media/xyz.jpg')
  })

  it('encodes each path segment but preserves the slashes', async () => {
    const { getMediaUrl } = await importWithApiBaseUrl('')
    // Spaces and unicode in the (already sanitized server-side) filename are still encoded.
    expect(getMediaUrl('media/a b/c.png')).toBe('/api/media/files/media/a%20b/c.png')
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
