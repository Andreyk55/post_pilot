import { type MediaType } from '../api/media'

export type AiAssistTab = 'text' | 'media' | 'translate'

export interface AiAssistAvailability {
  showMediaTab: boolean
  showImageActions: boolean
}

export function getAiAssistAvailability(mediaType: MediaType | null | undefined): AiAssistAvailability {
  return {
    showMediaTab: mediaType !== 'Video',
    showImageActions: mediaType === 'Image',
  }
}

export function getSupportedAiAssistTab(
  activeTab: AiAssistTab,
  mediaType: MediaType | null | undefined
): AiAssistTab {
  if (activeTab === 'media' && mediaType === 'Video') {
    return 'text'
  }

  return activeTab
}