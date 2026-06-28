import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

async function importWithBase(apiBaseUrl: string) {
  vi.resetModules()
  vi.doMock('../config/appConfig', () => ({ config: { apiBaseUrl } }))
  return await import('./ai')
}

describe('aiApi.generateCaptions', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    fetchMock.mockReset()
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.doUnmock('../config/appConfig')
  })

  it('POSTs a translation-only payload without voice profile or brand voice fields', async () => {
    fetchMock.mockResolvedValue({
      ok: true,
      json: async () => ({
        sourceLanguage: 'en',
        sourceConfidence: 1,
        sourceIsReliable: true,
        outputLanguage: 'he',
        captions: ['שלום'],
        warnings: [],
      }),
    })
    const { aiApi } = await importWithBase('')

    await aiApi.generateCaptions({
      text: 'Hello',
      platform: 'Facebook',
      outputLanguage: 'he',
      variants: 1,
      strictMeaning: true,
      sourceLanguage: 'en',
      keepBrandVoice: true,
      brandVoice: 'Friendly',
      voiceProfileId: 'profile-1',
    } as never)

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/ai/captions/generate')
    expect(init.method).toBe('POST')

    const body = JSON.parse(init.body)
    expect(body).toEqual({
      text: 'Hello',
      platform: 'Facebook',
      outputLanguage: 'he',
      variants: 1,
      strictMeaning: true,
      sourceLanguage: 'en',
    })
    expect(body).not.toHaveProperty('voiceProfileId')
    expect(body).not.toHaveProperty('brandVoice')
    expect(body).not.toHaveProperty('keepBrandVoice')
  })
})

describe('aiMediaApi.imageCaptionIdeas', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    fetchMock.mockReset()
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.doUnmock('../config/appConfig')
  })

  it('includes voiceProfileId for media caption generation', async () => {
    fetchMock.mockResolvedValue({
      ok: true,
      json: async () => ({
        action: 'CaptionIdeas',
        variants: [{ title: 'Option 1', text: 'Caption' }],
      }),
    })
    const { aiMediaApi } = await importWithBase('')

    await aiMediaApi.imageCaptionIdeas(
      'Instagram',
      [{ assetUrl: 'media/image.jpg', assetType: 'image' }],
      'Existing text',
      'profile-1'
    )

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/ai/media')
    const body = JSON.parse(init.body)
    expect(body).toEqual({
      action: 'CaptionIdeas',
      platform: 'Instagram',
      mediaItems: [{ assetUrl: 'media/image.jpg', assetType: 'image' }],
      text: 'Existing text',
      voiceProfileId: 'profile-1',
    })
  })
})
