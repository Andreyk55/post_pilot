import { describe, expect, it } from 'vitest'
import aiAssistPanelSource from './AiAssistPanel.tsx?raw'
import {
  getAiAssistAvailability,
  getMediaAiUnsupportedReason,
  getSupportedSingleImageMediaItem,
  normalizeActiveAiTab,
  shouldShowMediaTab,
} from './aiAssistPanelState'

const singleImage = [{ assetUrl: 'media/photo.jpg', mediaType: 'Image' as const }]
const multipleImages = [
  { assetUrl: 'media/photo-1.jpg', mediaType: 'Image' as const },
  { assetUrl: 'media/photo-2.jpg', mediaType: 'Image' as const },
]
const singleVideo = [{ assetUrl: 'media/video.mp4', mediaType: 'Video' as const }]
const mixedMedia = [
  { assetUrl: 'media/photo.jpg', mediaType: 'Image' as const },
  { assetUrl: 'media/video.mp4', mediaType: 'Video' as const },
]

describe('AiAssistPanel video media behavior', () => {
  it('hides the Media tab when no media is uploaded', () => {
    expect(getAiAssistAvailability([])).toEqual({
      showMediaTab: false,
      showImageActions: false,
      unsupportedReason: 'no-media',
    })
  })

  it('shows the Media tab and image actions for one uploaded image', () => {
    expect(getAiAssistAvailability(singleImage)).toEqual({
      showMediaTab: true,
      showImageActions: true,
      unsupportedReason: null,
    })
    expect(shouldShowMediaTab(singleImage)).toBe(true)
    expect(getSupportedSingleImageMediaItem(singleImage)).toEqual(singleImage[0])
  })

  it('hides the Media tab and media actions for multiple images', () => {
    expect(getAiAssistAvailability(multipleImages)).toEqual({
      showMediaTab: false,
      showImageActions: false,
      unsupportedReason: 'multiple-media',
    })
    expect(getMediaAiUnsupportedReason(multipleImages)).toBe('multiple-media')
  })

  it('hides the Media tab and media actions for a single video upload', () => {
    expect(getAiAssistAvailability(singleVideo)).toEqual({
      showMediaTab: false,
      showImageActions: false,
      unsupportedReason: 'video',
    })
    expect(getMediaAiUnsupportedReason(singleVideo)).toBe('video')
  })

  it('moves the active tab back to Text when media becomes unsupported', () => {
    expect(normalizeActiveAiTab('media', multipleImages)).toBe('text')
    expect(normalizeActiveAiTab('media', singleVideo)).toBe('text')
    expect(normalizeActiveAiTab('translate', singleVideo)).toBe('translate')
    expect(normalizeActiveAiTab('media', mixedMedia)).toBe('text')
  })

  it('keeps unsupported media helper copy and action guards in the panel source', () => {
    expect(aiAssistPanelSource).toMatch(/Media AI supports a single image only\. Video and multi-photo posts are not supported yet\./)
    expect(aiAssistPanelSource).toMatch(/if \(isDisabled \|\| !supportedMediaItem \|\| !showImageActions\) return/)
    expect(aiAssistPanelSource).toMatch(/setMediaResult\(null\)/)
  })

  it('does not retain frontend video AI processing paths or video media errors', () => {
    expect(aiAssistPanelSource).not.toMatch(/videoCaptionIdeasWithFrame/)
    expect(aiAssistPanelSource).not.toMatch(/submitThumbnailFrames/)
    expect(aiAssistPanelSource).not.toMatch(/extractSingleFrame/)
    expect(aiAssistPanelSource).not.toMatch(/extractVideoFrames/)
    expect(aiAssistPanelSource).not.toMatch(/Pick thumbnail/)
    expect(aiAssistPanelSource).not.toMatch(/Failed to process video due to cross-origin restrictions\./)
  })
})