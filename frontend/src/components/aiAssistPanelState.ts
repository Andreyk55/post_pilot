import { type MediaType } from '../api/media'

export type AiAssistTab = 'text' | 'media' | 'translate'

export interface AiAssistMediaItem {
  assetUrl?: string | null
  mediaId?: string | null
  previewUrl?: string | null
  mediaType?: MediaType | null
}

export type MediaAiUnsupportedReason = 'no-media' | 'multiple-media' | 'video' | 'unsupported'
export type AiAssistVoiceProfileAction =
  | 'text-content-generation'
  | 'media-caption-generation'
  | 'translate'
  | 'media-analysis'
  | 'unavailable'

export interface AiAssistAvailability {
  showMediaTab: boolean
  showImageActions: boolean
  unsupportedReason: MediaAiUnsupportedReason | null
}

function normalizeMediaItems(mediaItems: AiAssistMediaItem[] | null | undefined): AiAssistMediaItem[] {
  return (mediaItems ?? []).filter((item) => item != null)
}

function hasMediaReference(item: AiAssistMediaItem): boolean {
  return (typeof item.mediaId === 'string' && item.mediaId.trim().length > 0)
    || (typeof item.assetUrl === 'string' && item.assetUrl.trim().length > 0)
}

export function getMediaAiUnsupportedReason(
  mediaItems: AiAssistMediaItem[] | null | undefined
): MediaAiUnsupportedReason | null {
  const normalizedMediaItems = normalizeMediaItems(mediaItems)

  if (normalizedMediaItems.length === 0) {
    return 'no-media'
  }

  if (normalizedMediaItems.length !== 1) {
    return 'multiple-media'
  }

  const [mediaItem] = normalizedMediaItems

  if (!hasMediaReference(mediaItem)) {
    return 'unsupported'
  }

  if (mediaItem.mediaType === 'Video') {
    return 'video'
  }

  if (mediaItem.mediaType !== 'Image') {
    return 'unsupported'
  }

  return null
}

export function isMediaAiSupported(mediaItems: AiAssistMediaItem[] | null | undefined): boolean {
  return getMediaAiUnsupportedReason(mediaItems) === null
}

export function shouldShowMediaTab(mediaItems: AiAssistMediaItem[] | null | undefined): boolean {
  return isMediaAiSupported(mediaItems)
}

export function getSupportedSingleImageMediaItem(
  mediaItems: AiAssistMediaItem[] | null | undefined
): AiAssistMediaItem | null {
  if (!isMediaAiSupported(mediaItems)) {
    return null
  }

  const [mediaItem] = normalizeMediaItems(mediaItems)
  return mediaItem ?? null
}

export function getAiAssistAvailability(mediaItems: AiAssistMediaItem[] | null | undefined): AiAssistAvailability {
  const isSupported = isMediaAiSupported(mediaItems)

  return {
    showMediaTab: isSupported,
    showImageActions: isSupported,
    unsupportedReason: getMediaAiUnsupportedReason(mediaItems),
  }
}

export function normalizeActiveAiTab(
  activeTab: AiAssistTab,
  mediaItems: AiAssistMediaItem[] | null | undefined
): AiAssistTab {
  if (activeTab === 'media' && !isMediaAiSupported(mediaItems)) {
    return 'text'
  }

  return activeTab
}

export function aiActionSupportsVoiceProfile(action: AiAssistVoiceProfileAction): boolean {
  return action === 'text-content-generation' || action === 'media-caption-generation'
}

export function getVoiceProfileAction(
  activeTab: AiAssistTab,
  mediaItems: AiAssistMediaItem[] | null | undefined
): AiAssistVoiceProfileAction {
  if (activeTab === 'translate') {
    return 'translate'
  }

  if (activeTab === 'media') {
    return isMediaAiSupported(mediaItems) ? 'media-caption-generation' : 'unavailable'
  }

  return 'text-content-generation'
}

export function shouldShowVoiceProfileControl(
  activeTab: AiAssistTab,
  mediaItems: AiAssistMediaItem[] | null | undefined
): boolean {
  return aiActionSupportsVoiceProfile(getVoiceProfileAction(activeTab, mediaItems))
}

export const getSupportedAiAssistTab = normalizeActiveAiTab
