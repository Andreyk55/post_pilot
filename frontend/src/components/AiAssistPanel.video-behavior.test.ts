import { describe, expect, it } from 'vitest'
import aiAssistPanelSource from './AiAssistPanel.tsx?raw'
import { getAiAssistAvailability, getSupportedAiAssistTab } from './aiAssistPanelState'

describe('AiAssistPanel video media behavior', () => {
  it('shows the Media tab and image actions for image uploads', () => {
    expect(getAiAssistAvailability('Image')).toEqual({
      showMediaTab: true,
      showImageActions: true,
    })
  })

  it('hides the Media tab and media actions for video uploads', () => {
    expect(getAiAssistAvailability('Video')).toEqual({
      showMediaTab: false,
      showImageActions: false,
    })
  })

  it('moves the active tab back to Text when video replaces image media', () => {
    expect(getSupportedAiAssistTab('media', 'Video')).toBe('text')
    expect(getSupportedAiAssistTab('translate', 'Video')).toBe('translate')
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