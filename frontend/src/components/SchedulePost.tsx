import { useState, useEffect, useCallback, useRef, useMemo } from 'react'
import { metaApi } from '../api/meta'
import { aiApi, type AiPlatform, type AiGoal, type AudienceLocationMode } from '../api/ai'
import type { MediaType, ValidationStatus, MediaValidationError, MediaValidationWarning } from '../api/media'
import type { ConnectedPage, ConnectedInstagramAccount } from '../types/meta'
import { MediaUpload } from './MediaUpload'
import { MultiMediaUpload, type UploadedMediaItem } from './MultiMediaUpload'
import type { CreatePostMediaItem, PostType, InstagramUserTag } from '../api/posts'
import { AiAssistPanel, type StickyLanguageState } from './AiAssistPanel'
import { SuggestedTimes } from './SuggestedTimes'
import { type VoiceProfileSummary } from '../api/voiceProfiles'
import { InstagramMention } from './InstagramMention'
import { InstagramMediaTags, type MediaTag } from './InstagramMediaTags'
import { canShowCarouselTags, buildCarouselMediaTags } from '../utils/instagramTagging'
import {
  getPostTextMaxChars,
  getPlatformDisplayName,
  type PlatformId,
} from '../constants/validationLimits'
import { MAX_PLATFORMS_PER_POST } from '../constants/features'
import { useComposerEnabled } from '../hooks/useComposerEnabled'
import { useAuth } from '../hooks/useAuth'
import { WorkspaceContextBadge } from './WorkspaceContextBadge'
import './SchedulePost.css'

interface SchedulePostProps {
  onSchedule: (data: {
    content: string
    scheduledDate: string
    scheduledTime: string
    platforms: string[]
    postType: PostType
    targetPageId?: string
    targetInstagramAccountId?: string
    mediaUrl?: string
    mediaType?: MediaType
    selectedThumbnailUrl?: string
    mediaItems?: CreatePostMediaItem[]
    instagramUserTags?: InstagramUserTag[]
    instagramMediaTags?: Record<number, InstagramUserTag[]>
  }) => void
  onPublishNow?: (data: {
    content: string
    platforms: string[]
    postType: PostType
    targetPageId?: string
    targetInstagramAccountId?: string
    mediaUrl?: string
    mediaType?: MediaType
    selectedThumbnailUrl?: string
    mediaItems?: CreatePostMediaItem[]
    instagramUserTags?: InstagramUserTag[]
    instagramMediaTags?: Record<number, InstagramUserTag[]>
  }) => Promise<void>
  voiceProfiles: VoiceProfileSummary[]
  onVoiceProfileModalOpen: (profileId?: string | null) => void
  /** Optional callback for navigating to other pages (e.g., Connected Accounts) */
  onNavigate?: (page: string) => void
}

const platforms = [
  { id: 'twitter', name: 'Twitter/X', icon: '𝕏' },
  { id: 'instagram', name: 'Instagram', icon: '📷' },
  { id: 'facebook', name: 'Facebook', icon: 'f' },
  { id: 'linkedin', name: 'LinkedIn', icon: 'in' },
]

// Map platform IDs to AI platform types
function getAiPlatform(platformIds: string[]): AiPlatform | null {
  // Use the first selected platform for suggestions
  const first = platformIds[0]
  if (!first) return null

  const mapping: Record<string, AiPlatform> = {
    twitter: 'X',
    instagram: 'Instagram',
    facebook: 'Facebook',
    linkedin: 'LinkedIn',
  }
  return mapping[first] || null
}

export function SchedulePost({ onSchedule, onPublishNow, voiceProfiles, onVoiceProfileModalOpen, onNavigate }: SchedulePostProps) {
  const { hasWorkspace } = useAuth()
  const [content, setContent] = useState('')
  const [postType, setPostType] = useState<PostType>('Feed')
  const [scheduledDate, setScheduledDate] = useState('')
  const [scheduledTime, setScheduledTime] = useState('')
  const [selectedPlatforms, setSelectedPlatforms] = useState<string[]>([])
  const [connectedPages, setConnectedPages] = useState<ConnectedPage[]>([])
  const [connectedInstagramAccounts, setConnectedInstagramAccounts] = useState<ConnectedInstagramAccount[]>([])
  const [isAccountConnected, setIsAccountConnected] = useState(false)
  const [selectedPageId, setSelectedPageId] = useState<string>('')
  const [selectedInstagramAccountId, setSelectedInstagramAccountId] = useState<string>('')
  const [loadingPages, setLoadingPages] = useState(false)
  const [mediaUrl, setMediaUrl] = useState<string | null>(null)
  const [mediaType, setMediaType] = useState<MediaType | null>(null)
  const [uploadError, setUploadError] = useState<string | null>(null)
  const [uploadKey, setUploadKey] = useState(0)
  const [isUploading, setIsUploading] = useState(false)
  const [isPublishingNow, setIsPublishingNow] = useState(false)
  const [aiPanelKey, setAiPanelKey] = useState(0)
  const [suggestedTimesKey, setSuggestedTimesKey] = useState(0)
  const [selectedThumbnailUrl, setSelectedThumbnailUrl] = useState<string | null>(null)
  const [mediaValidationStatus, setMediaValidationStatus] = useState<ValidationStatus | null>(null)
  const [mediaValidationErrors, setMediaValidationErrors] = useState<MediaValidationError[]>([])

  // Carousel (multi-image) state for Instagram
  const [carouselItems, setCarouselItems] = useState<UploadedMediaItem[]>([])

  // Instagram media tags (tag people on single image/video)
  const [mediaTags, setMediaTags] = useState<MediaTag[]>([])

  // Instagram per-media-item tags for carousel posts (key = media item order)
  const [carouselMediaTags, setCarouselMediaTags] = useState<Map<number, MediaTag[]>>(new Map())
  // Which carousel item is currently selected for tag editing (order index)
  const [selectedCarouselItemIndex, setSelectedCarouselItemIndex] = useState<number>(0)

  // AI state (shared between AiAssistPanel and time suggestions)
  const [goal, setGoal] = useState<AiGoal>('Engage')
  const [audienceLocation, setAudienceLocation] = useState<AudienceLocationMode>('MyLocation')
  const [audienceCountry, setAudienceCountry] = useState<string>('')

  // Sticky language state - persists across content edits until explicitly changed
  // Language is "unknown" initially, set once on first Generate, and only changes on:
  // 1. Apply translation (set to target language)
  // 2. Explicit re-detect action
  // 3. Form reset
  const [stickyLanguage, setStickyLanguage] = useState<StickyLanguageState>({
    languageCode: 'unknown',
    confidence: 0,
    isReliable: false,
  })

  // Use ref to hold latest language to avoid stale closures
  const languageRef = useRef<StickyLanguageState>(stickyLanguage)
  languageRef.current = stickyLanguage

  // Use ref to hold latest content to avoid stale closures
  const contentRef = useRef<string>(content)
  contentRef.current = content

  // Ref for caption textarea (used by InstagramMention for cursor position)
  const captionTextareaRef = useRef<HTMLTextAreaElement>(null)

  // Ensure we have a detected language - only calls API if language is unknown
  const ensureLanguageDetected = useCallback(async (): Promise<StickyLanguageState> => {
    const current = languageRef.current

    // If language is already known (sticky), reuse it - NO API call
    if (current.languageCode !== 'unknown') {
      return current
    }

    // Language unknown - detect it now
    const currentContent = contentRef.current

    try {
      const result = await aiApi.detectLanguage(currentContent)
      const newLanguage: StickyLanguageState = {
        languageCode: result.languageCode,
        confidence: result.confidence,
        isReliable: result.isReliable,
      }
      setStickyLanguage(newLanguage)
      return newLanguage
    } catch (err) {
      console.error('Language detection failed:', err)
      // Fallback to English if detection fails
      const fallback: StickyLanguageState = {
        languageCode: 'en',
        confidence: 0,
        isReliable: false,
      }
      setStickyLanguage(fallback)
      return fallback
    }
  }, []) // No dependencies - uses refs for latest values

  // Reset language to unknown (for explicit re-detect)
  const resetLanguage = useCallback(() => {
    setStickyLanguage({
      languageCode: 'unknown',
      confidence: 0,
      isReliable: false,
    })
  }, [])

  // Set language directly (used when applying translation)
  const setLanguage = useCallback((languageCode: string) => {
    setStickyLanguage({
      languageCode,
      confidence: 1.0, // Translation output language is known
      isReliable: true,
    })
  }, [])

  // Load connected pages and Instagram accounts on mount
  useEffect(() => {
    loadConnectedAccounts()
  }, [])

  const loadConnectedAccounts = async () => {
    try {
      setLoadingPages(true)
      const response = await metaApi.getConnection()
      setIsAccountConnected(response.isConnected)
      if (response.isConnected && response.connection) {
        setConnectedPages(response.connection.pages)
        setConnectedInstagramAccounts(response.connection.instagramAccounts || [])
        // Auto-select first page if only one exists
        if (response.connection.pages.length === 1) {
          setSelectedPageId(response.connection.pages[0].id)
        }
        // Auto-select first IG account if only one exists
        if (response.connection.instagramAccounts?.length === 1) {
          setSelectedInstagramAccountId(response.connection.instagramAccounts[0].id)
        }
      }
    } catch (err) {
      console.error('Failed to load connected accounts:', err)
    } finally {
      setLoadingPages(false)
    }
  }

  const isFacebookSelected = selectedPlatforms.includes('facebook')
  const isInstagramSelected = selectedPlatforms.includes('instagram')
  const isStory = postType === 'Story'

  // Stories are only supported on Facebook and Instagram
  const isStoryPlatformSelected = isFacebookSelected || isInstagramSelected

  // Determine if composer should be enabled based on platform and connection state
  const composerState = useComposerEnabled({
    hasWorkspace,
    selectedPlatforms,
    connectedPages,
    isAccountConnected,
    selectedPageId,
    loadingPages,
    connectedInstagramAccounts,
    selectedInstagramAccountId,
  })

  // Clear selected page if it's no longer in the connected pages list
  useEffect(() => {
    if (selectedPageId && connectedPages.length > 0) {
      const pageExists = connectedPages.some(page => page.id === selectedPageId)
      if (!pageExists) {
        setSelectedPageId('')
      }
    }
  }, [connectedPages, selectedPageId])

  // Clear selected IG account if it's no longer connected
  useEffect(() => {
    if (selectedInstagramAccountId && connectedInstagramAccounts.length > 0) {
      const accountExists = connectedInstagramAccounts.some(a => a.id === selectedInstagramAccountId)
      if (!accountExists) {
        setSelectedInstagramAccountId('')
      }
    }
  }, [connectedInstagramAccounts, selectedInstagramAccountId])

  const selectPlatform = (platformId: string) => {
    if (MAX_PLATFORMS_PER_POST === 1) {
      // Single selection mode: replace current selection
      if (selectedPlatforms.includes(platformId)) {
        // Clicking selected platform deselects it
        setSelectedPlatforms([])
        if (platformId === 'facebook') {
          setSelectedPageId('')
        }
        if (platformId === 'instagram') {
          setSelectedInstagramAccountId('')
        }
      } else {
        // Select new platform, replacing any previous selection
        setSelectedPlatforms([platformId])
        // Clear page selection if Facebook is deselected
        if (selectedPlatforms.includes('facebook') && platformId !== 'facebook') {
          setSelectedPageId('')
        }
        // Clear IG selection if Instagram is deselected
        if (selectedPlatforms.includes('instagram') && platformId !== 'instagram') {
          setSelectedInstagramAccountId('')
        }
        // Auto-select IG account if switching to Instagram and only one exists
        if (platformId === 'instagram' && connectedInstagramAccounts.length === 1) {
          setSelectedInstagramAccountId(connectedInstagramAccounts[0].id)
        }
        // Auto-select Facebook page if switching to Facebook and only one exists
        if (platformId === 'facebook' && connectedPages.length === 1) {
          setSelectedPageId(connectedPages[0].id)
        }
      }
    } else {
      // Multi-select mode: toggle selection
      setSelectedPlatforms(prev =>
        prev.includes(platformId)
          ? prev.filter(p => p !== platformId)
          : [...prev, platformId].slice(0, MAX_PLATFORMS_PER_POST)
      )
      // Clear page selection if Facebook is deselected
      if (platformId === 'facebook' && selectedPlatforms.includes('facebook')) {
        setSelectedPageId('')
      }
      if (platformId === 'instagram' && selectedPlatforms.includes('instagram')) {
        setSelectedInstagramAccountId('')
      }
    }
  }

  // Multi-media detection: Instagram carousel (images or videos) or Facebook multi-photo (not available for stories)
  const isInstagramCarousel = isInstagramSelected && !isStory && carouselItems.length >= 2
  const isFacebookMultiPhoto = isFacebookSelected && !isStory && carouselItems.length >= 2 && carouselItems.every(i => i.mediaType === 'Image')
  const isMultiMedia = isInstagramCarousel || isFacebookMultiPhoto

  // Instagram media validation: single image/video OR carousel (2+ images)
  const isInstagramMediaValid = !isInstagramSelected ||
    isInstagramCarousel ||
    (mediaUrl && (mediaType === 'Image' || mediaType === 'Video'))

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault()

    // Defense-in-depth: never schedule without a selected workspace, even if the
    // button somehow fired. The WorkspaceGuard modal handles the user-facing flow.
    if (!hasWorkspace) return

    const hasCarousel = !isStory && (isInstagramSelected || isFacebookSelected) && carouselItems.length >= 2
    const hasMedia = mediaUrl || hasCarousel

    // Stories require media (no text-only stories)
    if (isStory && !mediaUrl) {
      return
    }

    // Feed posts require either content or media, plus date/time/platform
    if (!isStory && (!content && !hasMedia) || !scheduledDate || !scheduledTime || selectedPlatforms.length === 0) {
      return
    }

    // Require page selection for Facebook
    if (isFacebookSelected && !selectedPageId) {
      return
    }

    // Require IG account selection for Instagram
    if (isInstagramSelected && !selectedInstagramAccountId) {
      return
    }

    // Instagram feed requires media: either carousel (2+ images) or single image/video
    if (!isStory && isInstagramSelected && !hasCarousel && (!mediaUrl || (mediaType !== 'Image' && mediaType !== 'Video'))) {
      return
    }

    // Build media items for carousel (feed posts only)
    const mediaItemsPayload: CreatePostMediaItem[] | undefined = hasCarousel
      ? carouselItems.map((item, index) => ({
          mediaUrl: item.storageKey,
          mediaType: item.mediaType,
          order: index,
        }))
      : undefined

    onSchedule({
      content: isStory ? '' : content,
      scheduledDate,
      scheduledTime,
      platforms: selectedPlatforms,
      postType,
      targetPageId: isFacebookSelected ? selectedPageId : undefined,
      targetInstagramAccountId: isInstagramSelected ? selectedInstagramAccountId : undefined,
      mediaUrl: hasCarousel ? undefined : (mediaUrl || undefined),
      mediaType: hasCarousel ? undefined : (mediaType || undefined),
      selectedThumbnailUrl: selectedThumbnailUrl || undefined,
      mediaItems: mediaItemsPayload,
      instagramUserTags: placedUserTags,
      instagramMediaTags: carouselMediaTagsPayload,
    })

    // Reset form including language
    setContent('')
    setPostType('Feed')
    setScheduledDate('')
    setScheduledTime('')
    setSelectedPlatforms([])
    setSelectedPageId('')
    setSelectedInstagramAccountId('')
    setMediaUrl(null)
    setMediaType(null)
    setUploadError(null)
    setUploadKey(k => k + 1)
    setSelectedThumbnailUrl(null)
    setMediaValidationStatus(null)
    setMediaValidationErrors([])
    setCarouselItems([])
    setMediaTags([])
    setCarouselMediaTags(new Map())
    setSelectedCarouselItemIndex(0)
    setStickyLanguage({ languageCode: 'unknown', confidence: 0, isReliable: false })
  }

  // Get the max character limit for the first selected platform
  const selectedPlatformId = selectedPlatforms[0] as PlatformId | undefined
  const maxChars = getPostTextMaxChars(selectedPlatformId ?? null)
  const isTextTooLong = content.length > maxChars
  const platformDisplayName = selectedPlatformId ? getPlatformDisplayName(selectedPlatformId) : ''

  // Media validation status check - invalid media blocks submission
  const hasInvalidMedia = mediaUrl && mediaValidationStatus === 'Invalid'
  const hasInvalidCarouselItems = carouselItems.some(item => item.validationStatus === 'Invalid')

  // Instagram media tags: show for IG Feed + single image or single video (not carousel)
  const isTaggableMedia = mediaType === 'Image' || mediaType === 'Video'
  const showMediaTags = isInstagramSelected && !isStory && isTaggableMedia && !isMultiMedia && !!mediaUrl
  // For video posts, tags are auto-placed at center (0.5, 0.5) — only images need manual placement
  const isVideoTag = mediaType === 'Video'
  const hasUnplacedTags = !isVideoTag && mediaTags.length > 0 && mediaTags.some(t => t.x === undefined || t.y === undefined)

  // Instagram carousel per-image tagging
  const showCarouselTags = canShowCarouselTags(isInstagramSelected, isStory, isMultiMedia)

  // --- Caption summary parsing (Instagram only) ---
  const captionSummary = useMemo(() => {
    const mentionRegex = /(?<![\w.])@([A-Za-z0-9._]{1,30})/g
    const hashtagRegex = /(?<![\w])#([A-Za-z0-9_]{1,50})/g
    const mentionSet = new Set<string>()
    const hashtagSet = new Set<string>()
    let m: RegExpExecArray | null
    while ((m = mentionRegex.exec(content)) !== null) mentionSet.add(m[1].toLowerCase())
    while ((m = hashtagRegex.exec(content)) !== null) hashtagSet.add(m[1].toLowerCase())
    const mediaTagCount = mediaTags.length
    const notPlacedCount = mediaTags.filter(t => t.x === undefined || t.y === undefined).length
    let mediaTagSuffix = ''
    if (mediaTagCount > 0) {
      mediaTagSuffix = notPlacedCount === 0 ? ' (placed)' : ` (${notPlacedCount} not placed)`
    }
    return {
      mentionCount: mentionSet.size,
      hashtagCount: hashtagSet.size,
      mediaTagCount,
      mediaTagSuffix,
    }
  }, [content, mediaTags])
  // Build placed tags payload for submission
  // For video: auto-place all tags at center (0.5, 0.5) since there's no image to click on
  const placedUserTags: InstagramUserTag[] | undefined = showMediaTags && mediaTags.length > 0
    ? isVideoTag
      ? mediaTags.map(t => ({ username: t.username, x: t.x ?? 0.5, y: t.y ?? 0.5 }))
      : mediaTags
          .filter(t => t.x !== undefined && t.y !== undefined)
          .map(t => ({ username: t.username, x: t.x!, y: t.y! }))
    : undefined

  // Build carousel per-media-item tags payload
  const carouselMediaTagsPayload = showCarouselTags
    ? buildCarouselMediaTags(
        carouselMediaTags,
        new Map(carouselItems.map((item, idx) => [idx, item.mediaType]))
      )
    : undefined

  // Form is valid if there's content OR media, plus date/time/platform, not uploading, text within limits, and no invalid media
  // Stories: require media, content is optional; stories only on FB/IG
  const isFormValid = isStory
    ? (mediaUrl && scheduledDate && scheduledTime &&
       selectedPlatforms.length > 0 && isStoryPlatformSelected &&
       (!isFacebookSelected || selectedPageId) &&
       (!isInstagramSelected || selectedInstagramAccountId) &&
       !isUploading && !isTextTooLong && !hasInvalidMedia)
    : ((content || mediaUrl || isMultiMedia) && scheduledDate && scheduledTime &&
       selectedPlatforms.length > 0 &&
       (!isFacebookSelected || selectedPageId) &&
       (!isInstagramSelected || selectedInstagramAccountId) &&
       isInstagramMediaValid &&
       !isUploading && !isTextTooLong && !hasInvalidMedia && !hasInvalidCarouselItems && !hasUnplacedTags)

  // Publish Now valid: same as isFormValid but without requiring date/time
  const isPublishNowValid = isStory
    ? (mediaUrl &&
       selectedPlatforms.length > 0 && isStoryPlatformSelected &&
       (!isFacebookSelected || selectedPageId) &&
       (!isInstagramSelected || selectedInstagramAccountId) &&
       !isUploading && !isPublishingNow && !isTextTooLong && !hasInvalidMedia)
    : ((content || mediaUrl || isMultiMedia) &&
       selectedPlatforms.length > 0 &&
       (!isFacebookSelected || selectedPageId) &&
       (!isInstagramSelected || selectedInstagramAccountId) &&
       isInstagramMediaValid &&
       !isUploading && !isPublishingNow && !isTextTooLong && !hasInvalidMedia && !hasInvalidCarouselItems && !hasUnplacedTags)

  const handlePublishNow = async () => {
    if (!onPublishNow || !isPublishNowValid) return

    const hasCarousel = !isStory && (isInstagramSelected || isFacebookSelected) && carouselItems.length >= 2
    const mediaItemsPayload: CreatePostMediaItem[] | undefined = hasCarousel
      ? carouselItems.map((item, index) => ({
          mediaUrl: item.storageKey,
          mediaType: item.mediaType,
          order: index,
        }))
      : undefined

    setIsPublishingNow(true)
    try {
      await onPublishNow({
        content: isStory ? '' : content,
        platforms: selectedPlatforms,
        postType,
        targetPageId: isFacebookSelected ? selectedPageId : undefined,
        targetInstagramAccountId: isInstagramSelected ? selectedInstagramAccountId : undefined,
        mediaUrl: hasCarousel ? undefined : (mediaUrl || undefined),
        mediaType: hasCarousel ? undefined : (mediaType || undefined),
        selectedThumbnailUrl: selectedThumbnailUrl || undefined,
        mediaItems: mediaItemsPayload,
        instagramUserTags: placedUserTags,
        instagramMediaTags: carouselMediaTagsPayload,
      })

      // Reset form on success
      handleReset()
    } finally {
      setIsPublishingNow(false)
    }
  }

  // Check if there's any data in the form to show reset button
  const hasFormData = content || mediaUrl || carouselItems.length > 0 || mediaTags.length > 0 || scheduledDate || scheduledTime || selectedPlatforms.length > 0 || isStory

  const handleReset = () => {
    setContent('')
    setPostType('Feed')
    setScheduledDate('')
    setScheduledTime('')
    setSelectedPlatforms([])
    setSelectedPageId('')
    setSelectedInstagramAccountId('')
    setMediaUrl(null)
    setMediaType(null)
    setUploadError(null)
    setUploadKey(k => k + 1)
    setAiPanelKey(k => k + 1)
    setSuggestedTimesKey(k => k + 1)
    setSelectedThumbnailUrl(null)
    setMediaValidationStatus(null)
    setMediaValidationErrors([])
    setCarouselItems([])
    setMediaTags([])
    setStickyLanguage({ languageCode: 'unknown', confidence: 0, isReliable: false })
  }

  // Handle media validation changes from MediaUpload
  const handleMediaValidationChange = (
    status: ValidationStatus,
    errors: MediaValidationError[],
    _warnings: MediaValidationWarning[]
  ) => {
    setMediaValidationStatus(status)
    setMediaValidationErrors(errors)
  }

  // Destructure for easier access
  const { isEnabled: isComposerEnabled, disabledMessage, disabledReason } = composerState

  // Determine if we should show the "Go to Connected Accounts" button
  // Show when no account is connected or when a page/account was disconnected
  const showConnectedAccountsLink = disabledReason === 'no_account_connected' ||
    disabledReason === 'page_not_found' ||
    disabledReason === 'no_ig_accounts_connected' ||
    disabledReason === 'ig_account_not_found'

  return (
    <div className={`schedule-post ${!isComposerEnabled ? 'composer-disabled' : ''}`}>
      <div className="schedule-post__header">
        <h2>Schedule a Post</h2>
        {/* Make it unambiguous which workspace/account this post will go to. */}
        <WorkspaceContextBadge action="Posting to" />
      </div>

      {/* Disabled Composer Banner */}
      {!isComposerEnabled && disabledMessage && (
        <div className="composer-disabled-banner">
          <div className="disabled-banner-icon">⚠️</div>
          <div className="disabled-banner-content">
            <p className="disabled-banner-message">{disabledMessage}</p>
            {onNavigate && showConnectedAccountsLink && (
              <button
                type="button"
                className="disabled-banner-link"
                onClick={() => onNavigate('accounts')}
              >
                Go to Connected Accounts →
              </button>
            )}
          </div>
        </div>
      )}

      <form onSubmit={handleSubmit}>
        <div className="form-group">
          <label>Platform</label>
          {MAX_PLATFORMS_PER_POST === 1 && (
            <span className="hint-text">Choose 1 platform</span>
          )}
          <div className="platforms">
            {platforms.map(platform => {
              const isNotImplemented = platform.id === 'twitter' || platform.id === 'linkedin'
              return (
                <button
                  key={platform.id}
                  type="button"
                  className={'platform-btn ' + (selectedPlatforms.includes(platform.id) ? 'selected' : '') + (isNotImplemented ? ' coming-soon' : '')}
                  onClick={() => !isNotImplemented && selectPlatform(platform.id)}
                  title={isNotImplemented ? `${platform.name} - Coming Soon` : platform.name}
                  disabled={isNotImplemented}
                >
                  <span className="platform-icon">{platform.icon}</span>
                  <span className="platform-name">{platform.name}</span>
                  {isNotImplemented && <span className="coming-soon-badge">Coming Soon</span>}
                </button>
              )
            })}
          </div>
        </div>

        {/* Post Type Toggle - Feed/Story (only for FB/IG) */}
        {isStoryPlatformSelected && (
          <div className="form-group">
            <label>Post Type</label>
            <div className="post-type-toggle">
              <button
                type="button"
                className={`post-type-btn ${postType === 'Feed' ? 'selected' : ''}`}
                onClick={() => {
                  setPostType('Feed')
                  // Clear single media when switching (carousel may need different setup)
                  setMediaUrl(null)
                  setMediaType(null)
                  setUploadKey(k => k + 1)
                  setCarouselItems([])
                }}
              >
                Feed Post
              </button>
              <button
                type="button"
                className={`post-type-btn ${postType === 'Story' ? 'selected' : ''}`}
                onClick={() => {
                  setPostType('Story')
                  // Clear carousel when switching to story (stories are single media)
                  setMediaUrl(null)
                  setMediaType(null)
                  setUploadKey(k => k + 1)
                  setCarouselItems([])
                }}
              >
                Story
              </button>
            </div>
          </div>
        )}

        {/* Facebook Page Selector - shown when Facebook is selected */}
        {isFacebookSelected && connectedPages.length > 0 && (
          <div className="form-group">
            <label htmlFor="facebookPage">Facebook Page</label>
            {loadingPages ? (
              <div className="loading-pages">Loading pages...</div>
            ) : (
              <select
                id="facebookPage"
                value={selectedPageId}
                onChange={(e) => setSelectedPageId(e.target.value)}
                className="page-select"
                disabled={loadingPages}
              >
                <option value="">Select a page...</option>
                {connectedPages.map(page => (
                  <option key={page.id} value={page.id}>
                    {page.name} {page.category && `(${page.category})`}
                  </option>
                ))}
              </select>
            )}
          </div>
        )}

        {/* Instagram Account Selector - shown when Instagram is selected */}
        {isInstagramSelected && connectedInstagramAccounts.length > 0 && (
          <div className="form-group">
            <label htmlFor="instagramAccount">Instagram Account</label>
            <span className="hint-text">Instagram {isStory ? 'Story' : 'Feed'}</span>
            {loadingPages ? (
              <div className="loading-pages">Loading accounts...</div>
            ) : (
              <select
                id="instagramAccount"
                value={selectedInstagramAccountId}
                onChange={(e) => setSelectedInstagramAccountId(e.target.value)}
                className="page-select"
                disabled={loadingPages}
              >
                <option value="">Select an account...</option>
                {connectedInstagramAccounts.map(account => (
                  <option key={account.id} value={account.id}>
                    @{account.username} {account.pageName && `(${account.pageName})`}
                  </option>
                ))}
              </select>
            )}
          </div>
        )}

        {/* Caption / Post Content — hidden entirely for stories */}
        {!isStory && (
          <div className="form-group">
            <label htmlFor="content">
              {isInstagramSelected
                ? <>Caption<span className="hint-text" style={{ marginLeft: '8px' }}>Include #hashtags in caption</span></>
                : 'Post Content'
              }
            </label>
            <textarea
              id="content"
              ref={captionTextareaRef}
              value={content}
              onChange={(e) => setContent(e.target.value)}
              placeholder={isInstagramSelected ? "Write your caption... #hashtags and @mentions welcome" : "What do you want to share?"}
              rows={4}
              className={isTextTooLong ? 'error' : ''}
              disabled={!isComposerEnabled}
            />
            <div className="char-counter-row">
              <span className={`char-count ${isTextTooLong ? 'error' : ''}`}>
                {content.length}/{maxChars}
              </span>
              {isTextTooLong && (
                <span className="char-error">
                  Text is too long for {platformDisplayName}. Max {maxChars} characters.
                </span>
              )}
            </div>

            {isInstagramSelected && (
              <div className="caption-summary-row">
                <span className="caption-summary">
                  Mentions: {captionSummary.mentionCount} &bull; Hashtags: {captionSummary.hashtagCount} &bull; Media tags: {captionSummary.mediaTagCount}{captionSummary.mediaTagSuffix}
                </span>
                <span className="caption-microcopy">
                  Mentions and hashtags usually become clickable if they're valid.
                </span>
              </div>
            )}

            {isInstagramSelected && (
              <InstagramMention
                caption={content}
                onCaptionChange={setContent}
                textareaRef={captionTextareaRef}
                disabled={!isComposerEnabled}
              />
            )}

            <AiAssistPanel
              key={aiPanelKey}
              text={content}
              stickyLanguage={stickyLanguage}
              ensureLanguageDetected={ensureLanguageDetected}
              resetLanguage={resetLanguage}
              platform={getAiPlatform(selectedPlatforms)}
              onApplyText={(newText, newLanguageCode) => {
                // Only update if content actually changes
                if (content !== newText) {
                  setContent(newText)
                }
                // If a new language was provided (from translation), set it
                if (newLanguageCode) {
                  setLanguage(newLanguageCode)
                }
              }}
              onAppendText={(text) => setContent((prev) => prev + text)}
              mediaUrl={mediaUrl}
              mediaType={mediaType}
              onSelectThumbnail={(url) => setSelectedThumbnailUrl(url)}
              voiceProfiles={voiceProfiles}
              onVoiceProfileModalOpen={onVoiceProfileModalOpen}
              goal={goal}
              onGoalChange={setGoal}
              disabled={!isComposerEnabled}
            />
          </div>
        )}

        <div className="form-group">
          <label>
            {isStory ? 'Media (required)' : isInstagramSelected ? 'Media (required)' : 'Media (optional)'}
          </label>
          {isStory && !mediaUrl && (
            <div className="ig-media-hint">
              <strong>Story:</strong> 1 photo (JPG/PNG) or 1 video (MP4) — vertical 9:16 recommended
            </div>
          )}
          {!isStory && isInstagramSelected && !mediaUrl && carouselItems.length === 0 && (
            <div className="ig-media-hint">
              <strong>Single:</strong> 1 photo (JPG/PNG) or 1 video (MP4, published as Reel)<br />
              <strong>Carousel:</strong> 2–10 photos, videos, or mix of both
            </div>
          )}
          {!isStory && isFacebookSelected && !mediaUrl && carouselItems.length === 0 && (
            <div className="ig-media-hint">
              <strong>Single:</strong> 1 photo (JPG/PNG) or 1 video (MP4)<br />
              <strong>Carousel:</strong> 2–10 photos only. Mixed photos + videos not supported.
            </div>
          )}
          {isStory ? (
            /* Stories: single media upload with Story placement for validation */
            <MediaUpload
              key={uploadKey}
              onUploadComplete={(storageKey, type) => {
                setMediaUrl(storageKey)
                setMediaType(type)
                setUploadError(null)
              }}
              onUploadError={(error) => setUploadError(error)}
              onClear={() => {
                setMediaUrl(null)
                setMediaType(null)
                setMediaValidationStatus(null)
                setMediaValidationErrors([])
              }}
              onUploadingChange={setIsUploading}
              onValidationChange={handleMediaValidationChange}
              selectedPlatform={selectedPlatformId}
              placement="Story"
              disabled={!isComposerEnabled}
            />
          ) : (isInstagramSelected || isFacebookSelected) ? (
            <MultiMediaUpload
              key={uploadKey}
              items={carouselItems}
              onItemsChange={(items) => {
                setCarouselItems(items)
                // If user goes from multi to single (1 item), keep it in carousel state
                // but also set legacy media for AI panel preview
                if (items.length === 1) {
                  setMediaUrl(items[0].storageKey)
                  setMediaType(items[0].mediaType)
                } else if (items.length === 0) {
                  setMediaUrl(null)
                  setMediaType(null)
                } else {
                  // Multi-media: set first item for AI preview
                  setMediaUrl(items[0].storageKey)
                  setMediaType(items[0].mediaType)
                }
              }}
              onUploadingChange={setIsUploading}
              selectedPlatform={selectedPlatformId}
              disabled={!isComposerEnabled}
            />
          ) : (
            <MediaUpload
              key={uploadKey}
              onUploadComplete={(storageKey, type) => {
                setMediaUrl(storageKey)
                setMediaType(type)
                setUploadError(null)
              }}
              onUploadError={(error) => setUploadError(error)}
              onClear={() => {
                setMediaUrl(null)
                setMediaType(null)
                setMediaValidationStatus(null)
                setMediaValidationErrors([])
              }}
              onUploadingChange={setIsUploading}
              onValidationChange={handleMediaValidationChange}
              selectedPlatform={selectedPlatformId}
              disabled={!isComposerEnabled}
            />
          )}
          {uploadError && <div className="upload-error">{uploadError}</div>}
          {/* Show validation error summary near submit button */}
          {hasInvalidMedia && mediaValidationErrors.length > 0 && (
            <div className="media-validation-summary">
              <strong>Media cannot be published:</strong>
              <ul>
                {mediaValidationErrors.map((err, i) => (
                  <li key={i}>{err.message}</li>
                ))}
              </ul>
            </div>
          )}
        </div>

        {/* Instagram Media Tags — tag people (IG Feed + single image or video) */}
        {showMediaTags && (
          <div className="form-group">
            <InstagramMediaTags
              caption={content}
              mediaTags={mediaTags}
              onMediaTagsChange={setMediaTags}
              mediaStorageKey={mediaUrl}
              disabled={!isComposerEnabled}
              isVideo={isVideoTag}
            />
            {hasUnplacedTags && (
              <div className="media-tags-validation-warning">
                Place all tags on the image (click the image to position each tag).
              </div>
            )}
          </div>
        )}

        {/* Instagram Carousel Per-Image Tags — tag people on each carousel item */}
        {showCarouselTags && (
          <div className="form-group">
            <label className="media-tags-label">Tag people on carousel media (optional)</label>
            <p className="media-tags-helper">
              Select a media item below, then add tags for that item. Image tags need to be placed; video tags are applied automatically.
            </p>

            {/* Media item selector tabs */}
            <div className="carousel-tag-tabs">
              {carouselItems.map((item, idx) => {
                const itemTags = carouselMediaTags.get(idx) ?? []
                return (
                  <button
                    key={idx}
                    type="button"
                    className={`carousel-tag-tab ${selectedCarouselItemIndex === idx ? 'active' : ''}`}
                    onClick={() => setSelectedCarouselItemIndex(idx)}
                  >
                    {item.mediaType === 'Video' ? 'Video' : 'Image'} {idx + 1}
                    {itemTags.length > 0 && <span className="carousel-tag-tab-badge">{itemTags.length}</span>}
                  </button>
                )
              })}
            </div>

            {/* Per-item tag editor */}
            {carouselItems[selectedCarouselItemIndex] && (
              <InstagramMediaTags
                caption={content}
                mediaTags={carouselMediaTags.get(selectedCarouselItemIndex) ?? []}
                onMediaTagsChange={(tags) => {
                  setCarouselMediaTags(prev => {
                    const next = new Map(prev)
                    next.set(selectedCarouselItemIndex, tags)
                    return next
                  })
                }}
                mediaStorageKey={carouselItems[selectedCarouselItemIndex].storageKey}
                disabled={!isComposerEnabled}
                isVideo={carouselItems[selectedCarouselItemIndex].mediaType === 'Video'}
              />
            )}
          </div>
        )}

        <div className="form-row">
          <div className="form-group">
            <label htmlFor="date">Date</label>
            <input
              type="date"
              id="date"
              value={scheduledDate}
              onChange={(e) => setScheduledDate(e.target.value)}
              disabled={!isComposerEnabled}
            />
          </div>

          <div className="form-group">
            <label htmlFor="time">Time</label>
            <input
              type="time"
              id="time"
              value={scheduledTime}
              onChange={(e) => {
                setScheduledTime(e.target.value)
                e.target.blur()
              }}
              disabled={!isComposerEnabled}
            />
          </div>
        </div>

        {/* AI-powered time suggestions - hidden for stories */}
        {!isStory && (
          <SuggestedTimes
            key={suggestedTimesKey}
            postText={content}
            selectedDate={scheduledDate}
            platform={getAiPlatform(selectedPlatforms)}
            goal={goal}
            audienceLocation={audienceLocation}
            country={audienceCountry || null}
            onAudienceLocationChange={setAudienceLocation}
            onCountryChange={setAudienceCountry}
            onSelectTime={(time) => setScheduledTime(time)}
            disabled={!isComposerEnabled || isUploading}
          />
        )}

        <div className="form-actions">
          <button
            type="submit"
            className="submit-btn"
            disabled={!isComposerEnabled || !isFormValid}
          >
            {isStory ? 'Schedule Story' : 'Schedule Post'}
          </button>
          {onPublishNow && (
            <button
              type="button"
              className="publish-now-btn"
              disabled={!isComposerEnabled || !isPublishNowValid || isPublishingNow}
              onClick={handlePublishNow}
            >
              {isPublishingNow ? 'Publishing…' : (isStory ? 'Publish Story Now' : 'Publish Now')}
            </button>
          )}
          {hasFormData && (
            <button
              type="button"
              className="reset-btn"
              onClick={handleReset}
            >
              Reset
            </button>
          )}
        </div>
      </form>
    </div>
  )
}
