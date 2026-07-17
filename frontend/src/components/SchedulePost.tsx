import { useState, useEffect, useCallback, useRef, useMemo } from 'react'
import { metaApi } from '../api/meta'
import { aiApi, type AiPlatform, type AiGoal, type AudienceLocationMode } from '../api/ai'
import type { MediaType, ValidationStatus, MediaValidationError, MediaValidationWarning } from '../api/media'
import type { ConnectedPage, ConnectedInstagramAccount } from '../types/meta'
import { MediaUpload } from './MediaUpload'
import { MultiMediaUpload, type UploadedMediaItem } from './MultiMediaUpload'
import { MediaRequirementHint } from './MediaValidationStatus'
import type { CreatePostMediaItem, PostType, InstagramUserTag } from '../api/posts'
import { AiAssistPanel, type StickyLanguageState } from './AiAssistPanel'
import { type AiAssistMediaItem } from './aiAssistPanelState'
import { SuggestedTimes } from './SuggestedTimes'
import { type VoiceProfileSummary } from '../api/voiceProfiles'
import { InstagramMention } from './InstagramMention'
import { InstagramMediaTags, type MediaTag } from './InstagramMediaTags'
import { canShowCarouselTags, buildCarouselMediaTags } from '../utils/instagramTagging'
import { hasUnpromotedLinkedInstagram } from '../utils/instagramPromotion'
import {
  applySchedulePostMediaValidationUpdate,
  clearSchedulePostMediaValidation,
  hasBlockingSchedulePostMediaValidation,
  type SchedulePostMediaValidationState,
} from '../utils/schedulePostMediaValidation'
import { isMetaChannelSwitch, isPostTypeSwitch, isComposerDraftDirty } from '../utils/schedulePostChannelSwitch'
import { ConfirmDialog } from './ConfirmDialog'
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
    mediaId?: string
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
    mediaId?: string
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

interface ResetComposerDraftOptions {
  nextPostType?: PostType
  clearTargets?: boolean
}

const platforms = [
  { id: 'facebook', name: 'Facebook', icon: 'f' },
  { id: 'instagram', name: 'Instagram', icon: '📷' },
]

// Map platform IDs to AI platform types
function getAiPlatform(platformIds: string[]): AiPlatform | null {
  // Use the first selected platform for suggestions
  const first = platformIds[0]
  if (!first) return null

  const mapping: Record<string, AiPlatform> = {
    facebook: 'Facebook',
    instagram: 'Instagram',
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
  // Pending Meta-channel switch awaiting confirmation. Holds the target platform id
  // while the "this will clear your draft" dialog is open; null when no switch is
  // pending. See handlePlatformClick / applyChannelSwitch.
  const [pendingChannelSwitch, setPendingChannelSwitch] = useState<string | null>(null)
  // Pending in-platform post type switch awaiting confirmation. Holds the target
  // post type while the dialog is open; null when no switch is pending.
  const [pendingPostTypeSwitch, setPendingPostTypeSwitch] = useState<PostType | null>(null)
  const [connectedPages, setConnectedPages] = useState<ConnectedPage[]>([])
  const [connectedInstagramAccounts, setConnectedInstagramAccounts] = useState<ConnectedInstagramAccount[]>([])
  // Identity-level Meta account display name, shown in the destination confirmation
  // so the user can see which Meta connection a selected Page/IG publishes through.
  const [connectedMetaAccountName, setConnectedMetaAccountName] = useState<string>('')
  const [isAccountConnected, setIsAccountConnected] = useState(false)
  const [selectedPageId, setSelectedPageId] = useState<string>('')
  const [selectedInstagramAccountId, setSelectedInstagramAccountId] = useState<string>('')
  const [loadingPages, setLoadingPages] = useState(false)
  const [mediaUrl, setMediaUrl] = useState<string | null>(null)
  const [mediaId, setMediaId] = useState<string | null>(null)
  const [mediaType, setMediaType] = useState<MediaType | null>(null)
  const [uploadError, setUploadError] = useState<string | null>(null)
  const [uploadKey, setUploadKey] = useState(0)
  const [isUploading, setIsUploading] = useState(false)
  const [isPublishingNow, setIsPublishingNow] = useState(false)
  const [aiPanelKey, setAiPanelKey] = useState(0)
  const [suggestedTimesKey, setSuggestedTimesKey] = useState(0)
  const [selectedThumbnailUrl, setSelectedThumbnailUrl] = useState<string | null>(null)
  // Single-media validation, stored together with the upload-session owner key so a
  // stale validation can never re-render an old error over the current media. See
  // schedulePostMediaValidation.ts for the owner-key semantics.
  const [mediaValidation, setMediaValidation] = useState<SchedulePostMediaValidationState>(
    clearSchedulePostMediaValidation(),
  )

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

  const aiAssistMediaItems: AiAssistMediaItem[] = carouselItems.length > 0
    ? carouselItems.map((item) => ({
        mediaId: item.mediaId,
        previewUrl: item.previewUrl,
        mediaType: item.mediaType,
      }))
    : ((mediaId || mediaUrl) && mediaType && mediaType !== 'None'
        ? [{ mediaId, previewUrl: mediaUrl, mediaType }]
        : [])

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

  const loadConnectedAccounts = async (allowRepair = true) => {
    try {
      setLoadingPages(true)
      const response = await metaApi.getConnection()
      setIsAccountConnected(response.isConnected)
      if (response.isConnected && response.connection) {
        const igAccounts = response.connection.instagramAccounts || []

        // Self-heal the production bug where a connected Page has a linked IG that was
        // never promoted to a connected asset: with pages connected but no connected IG,
        // check eligibility and run the idempotent backend repair once, then reload so
        // the composer no longer shows "No Instagram Business Account connected".
        if (allowRepair && response.connection.pages.length > 0 && igAccounts.length === 0) {
          try {
            const eligibility = await metaApi.getInstagramEligibility()
            const needsRepair = hasUnpromotedLinkedInstagram(
              response.connection.pages,
              igAccounts,
              eligibility.pages,
            )
            if (needsRepair) {
              await metaApi.refreshAssets()
              await loadConnectedAccounts(false) // reload once; don't recurse into repair again
              return
            }
          } catch (repairErr) {
            // Non-critical: fall through to the (possibly empty) IG list.
            console.error('Instagram auto-repair check failed:', repairErr)
          }
        }

        setConnectedPages(response.connection.pages)
        setConnectedInstagramAccounts(igAccounts)
        setConnectedMetaAccountName(response.connection.providerAccountName ?? '')
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
  const showFacebookPageSelector = isFacebookSelected && connectedPages.length > 0
  const showInstagramAccountSelector = isInstagramSelected && connectedInstagramAccounts.length > 0

  // Resolve the currently selected destination for the compact confirmation shown
  // below each selector. Display-only: this does not affect validation, the submitted
  // target ids, upload behavior, or AI gating.
  const selectedPage = selectedPageId
    ? connectedPages.find(page => page.id === selectedPageId)
    : undefined
  const selectedInstagramAccount = selectedInstagramAccountId
    ? connectedInstagramAccounts.find(account => account.id === selectedInstagramAccountId)
    : undefined

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

  const clearSingleMediaValidationState = () => {
    setMediaValidation(clearSchedulePostMediaValidation())
  }

  const handleSingleMediaUploadingChange = (uploading: boolean) => {
    setIsUploading(uploading)
  }

  const getComposerDraftSnapshot = () => {
    const carouselMediaTagCount = Array.from(carouselMediaTags.values())
      .reduce((count, tags) => count + tags.length, 0)
    const carouselValidationIssueCount = carouselItems.filter(item =>
      item.validationStatus !== 'Valid' ||
      item.validationErrors.length > 0 ||
      item.validationWarnings.length > 0
    ).length

    return {
      content,
      mediaUrl,
      mediaId,
      carouselItemCount: carouselItems.length,
      mediaTagCount: mediaTags.length + carouselMediaTagCount,
      scheduledDate,
      scheduledTime,
      postType,
      selectedThumbnailUrl,
      hasUploadError: uploadError !== null,
      hasSingleMediaValidationState: mediaValidation.status !== null || mediaValidation.errors.length > 0,
      carouselValidationIssueCount,
    }
  }

  // Reset every field that belongs to the composer *draft* while leaving the
  // connected accounts/pages/IG assets, the workspace, and the published posts list
  // untouched. Bumping the child keys remounts MediaUpload / MultiMediaUpload /
  // AiAssistPanel / SuggestedTimes, which invalidates their in-flight
  // upload/validation ownership tokens — so a late response from the previous draft
  // can't re-populate the cleared state after the remount. Does NOT change
  // selectedPlatforms; callers decide what the new selection should be. Target
  // selections are cleared for channel switches, but preserved for post-type
  // switches inside the same platform.
  const resetComposerDraft = ({
    nextPostType = 'Feed',
    clearTargets = true,
  }: ResetComposerDraftOptions = {}) => {
    setContent('')
    setPostType(nextPostType)
    setScheduledDate('')
    setScheduledTime('')
    if (clearTargets) {
      setSelectedPageId('')
      setSelectedInstagramAccountId('')
    }
    setMediaUrl(null)
    setMediaId(null)
    setMediaType(null)
    setUploadError(null)
    setIsUploading(false)
    setUploadKey(k => k + 1)
    setAiPanelKey(k => k + 1)
    setSuggestedTimesKey(k => k + 1)
    setSelectedThumbnailUrl(null)
    clearSingleMediaValidationState()
    setCarouselItems([])
    setMediaTags([])
    setCarouselMediaTags(new Map())
    setSelectedCarouselItemIndex(0)
    setStickyLanguage({ languageCode: 'unknown', confidence: 0, isReliable: false })
  }

  // Apply a (confirmed or clean) Meta-channel switch: clear the previous channel's
  // draft, then make the newly chosen channel the sole selection.
  const applyChannelSwitch = (platformId: string) => {
    resetComposerDraft()
    setSelectedPlatforms([platformId])
  }

  const applyPostTypeSwitch = (nextPostType: PostType) => {
    resetComposerDraft({ nextPostType, clearTargets: false })
  }

  // Platform button entrypoint. Switching the Meta channel (Facebook <-> Instagram)
  // clears the draft so media/text never carries over to the other channel; if the
  // draft has unsaved work we confirm first. First selection and deselect keep the
  // existing selectPlatform behavior (no draft reset).
  const handlePlatformClick = (platformId: string) => {
    if (isMetaChannelSwitch(selectedPlatforms, platformId)) {
      const isDirty = isComposerDraftDirty(getComposerDraftSnapshot())
      if (isDirty) {
        setPendingChannelSwitch(platformId)
        return
      }
      applyChannelSwitch(platformId)
      return
    }
    selectPlatform(platformId)
  }

  const handlePostTypeChange = (nextPostType: PostType) => {
    if (!isPostTypeSwitch(postType, nextPostType)) return

    const isDirty = isComposerDraftDirty(getComposerDraftSnapshot(), { includePostType: false })
    if (isDirty) {
      setPendingPostTypeSwitch(nextPostType)
      return
    }

    setPostType(nextPostType)
  }

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
          mediaId: item.mediaId,
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
      mediaId: hasCarousel ? undefined : (mediaId || undefined),
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
    clearSingleMediaValidationState()
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

  // Media validation status check - invalid media blocks submission. The error/warning
  // detail itself is rendered by the uploader's shared MediaValidationCard (single
  // source of truth); here we only need the blocking flags to gate the buttons.
  const hasBlockingMediaValidation = hasBlockingSchedulePostMediaValidation(mediaUrl, mediaValidation.status)
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
       !isUploading && !isTextTooLong && !hasBlockingMediaValidation)
    : ((content || mediaUrl || isMultiMedia) && scheduledDate && scheduledTime &&
       selectedPlatforms.length > 0 &&
       (!isFacebookSelected || selectedPageId) &&
       (!isInstagramSelected || selectedInstagramAccountId) &&
       isInstagramMediaValid &&
       !isUploading && !isTextTooLong && !hasBlockingMediaValidation && !hasInvalidCarouselItems && !hasUnplacedTags)

  // Publish Now valid: same as isFormValid but without requiring date/time
  const isPublishNowValid = isStory
    ? (mediaUrl &&
       selectedPlatforms.length > 0 && isStoryPlatformSelected &&
       (!isFacebookSelected || selectedPageId) &&
       (!isInstagramSelected || selectedInstagramAccountId) &&
       !isUploading && !isPublishingNow && !isTextTooLong && !hasBlockingMediaValidation)
    : ((content || mediaUrl || isMultiMedia) &&
       selectedPlatforms.length > 0 &&
       (!isFacebookSelected || selectedPageId) &&
       (!isInstagramSelected || selectedInstagramAccountId) &&
       isInstagramMediaValid &&
       !isUploading && !isPublishingNow && !isTextTooLong && !hasBlockingMediaValidation && !hasInvalidCarouselItems && !hasUnplacedTags)

  const handlePublishNow = async () => {
    if (!onPublishNow || !isPublishNowValid) return

    const hasCarousel = !isStory && (isInstagramSelected || isFacebookSelected) && carouselItems.length >= 2
    const mediaItemsPayload: CreatePostMediaItem[] | undefined = hasCarousel
      ? carouselItems.map((item, index) => ({
          mediaId: item.mediaId,
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
        mediaId: hasCarousel ? undefined : (mediaId || undefined),
        mediaType: hasCarousel ? undefined : (mediaType || undefined),
        selectedThumbnailUrl: selectedThumbnailUrl || undefined,
        mediaItems: mediaItemsPayload,
        instagramUserTags: placedUserTags,
        instagramMediaTags: carouselMediaTagsPayload,
      })

      // Reset form on success
      handleReset()
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to publish post'
      setUploadError(message)
      console.error('Publish now failed:', err)
    } finally {
      setIsPublishingNow(false)
    }
  }

  // Check if there's any data in the form to show reset button
  const hasFormData = content || mediaUrl || carouselItems.length > 0 || mediaTags.length > 0 || scheduledDate || scheduledTime || selectedPlatforms.length > 0 || isStory

  const handleReset = () => {
    resetComposerDraft()
    setSelectedPlatforms([])
  }

  // Handle media validation changes from MediaUpload. The owner key ties each result
  // to the upload session that produced it; the reducer drops results from a
  // superseded upload so a late "Invalid" can't reappear over newer media.
  const handleMediaValidationChange = (
    status: ValidationStatus,
    errors: MediaValidationError[],
    _warnings: MediaValidationWarning[],
    ownerKey: string,
  ) => {
    // A `Pending` update marks the (re)start of a single-media upload session.
    // Besides resetting the server-validation state (below), clear the transient
    // upload-error banner too — it can hold a client-side pre-validation message
    // (e.g. an Instagram Story aspect-ratio message, or an unsupported-type/too-large
    // message, which never reaches server validation and so has no owner key of its
    // own). Clearing it here, synchronously as the new upload begins, stops the
    // previous media's error from lingering under the new, still-pending upload.
    if (status === 'Pending') {
      setUploadError(null)
    }
    setMediaValidation(prev => applySchedulePostMediaValidationUpdate(prev, status, errors, ownerKey))
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
        {/* Read-only context: makes it unambiguous which workspace/account this
            post will go to. Workspace switching lives only in the sidebar
            selector — the badge is a non-interactive label. */}
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
        <div className="form-group form-group--platform-select">
          <label>Meta Channel</label>
          {MAX_PLATFORMS_PER_POST === 1 && (
            <span className="hint-text">Choose 1 platform</span>
          )}
          <div className={`platforms ${platforms.length === 2 ? 'platforms--meta-only' : ''}`}>
            {platforms.map(platform => {
              return (
                <button
                  key={platform.id}
                  type="button"
                  className={'platform-btn ' + (selectedPlatforms.includes(platform.id) ? 'selected' : '')}
                  onClick={() => handlePlatformClick(platform.id)}
                  title={platform.name}
                >
                  <span className="platform-icon">{platform.icon}</span>
                  <span className="platform-name">{platform.name}</span>
                </button>
              )
            })}
          </div>
        </div>

        {/* Facebook Page Selector - shown when Facebook is selected */}
        {showFacebookPageSelector && (
          <div className="form-group form-group--target-select">
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
                <option value="">Select a Facebook Page</option>
                {connectedPages.map(page => (
                  <option key={page.id} value={page.id}>
                    {page.name} {page.category && `(${page.category})`}
                  </option>
                ))}
              </select>
            )}
            {/* Destination confirmation — only after a Page is selected. Works for
                both Feed and Story; display-only (no effect on validation/publishing). */}
            {selectedPage && (
              <div className="destination-confirmation" role="status">
                <span className="destination-confirmation__primary">
                  Posting to Facebook Page: <strong>{selectedPage.name}</strong>
                </span>
                {connectedMetaAccountName && (
                  <span className="destination-confirmation__meta">
                    Connected via Meta account: <strong>{connectedMetaAccountName}</strong>
                  </span>
                )}
              </div>
            )}
          </div>
        )}

        {/* Instagram Account Selector - shown when Instagram is selected */}
        {showInstagramAccountSelector && (
          <div className="form-group form-group--target-select">
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
                <option value="">Select an Instagram Account</option>
                {connectedInstagramAccounts.map(account => (
                  <option key={account.id} value={account.id}>
                    @{account.username} {account.pageName && `(${account.pageName})`}
                  </option>
                ))}
              </select>
            )}
            {/* Destination confirmation — only after an account is selected. Works for
                both Feed and Story; display-only (no effect on validation/publishing). */}
            {selectedInstagramAccount && (
              <div className="destination-confirmation" role="status">
                <span className="destination-confirmation__primary">
                  Posting to Instagram account: <strong>@{selectedInstagramAccount.username}</strong>
                </span>
                {selectedInstagramAccount.pageName && (
                  <span className="destination-confirmation__meta">
                    Linked Facebook Page: <strong>{selectedInstagramAccount.pageName}</strong>
                  </span>
                )}
                {connectedMetaAccountName && (
                  <span className="destination-confirmation__meta">
                    Connected via Meta account: <strong>{connectedMetaAccountName}</strong>
                  </span>
                )}
              </div>
            )}
          </div>
        )}

        {/* Post Type Toggle - Feed/Story (only for FB/IG) */}
        {isStoryPlatformSelected && (
          <div className="form-group">
            <label>Post Type</label>
            <div className="post-type-toggle">
              <button
                type="button"
                className={`post-type-btn ${postType === 'Feed' ? 'selected' : ''}`}
                onClick={() => handlePostTypeChange('Feed')}
              >
                Feed Post
              </button>
              <button
                type="button"
                className={`post-type-btn ${postType === 'Story' ? 'selected' : ''}`}
                onClick={() => handlePostTypeChange('Story')}
              >
                Story
              </button>
            </div>
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
              mediaId={mediaId}
              mediaType={mediaType}
              mediaItems={aiAssistMediaItems}
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
          {/* Shared pre-upload requirement hint — same component/styling for every
              platform + placement (see MediaRequirementHint). */}
          {!mediaUrl && carouselItems.length === 0 && (isStory || isInstagramSelected || isFacebookSelected) && (
            <MediaRequirementHint platform={selectedPlatformId} placement={isStory ? 'Story' : 'Feed'} />
          )}
          {isStory ? (
            /* Stories: single media upload with Story placement for validation */
            <MediaUpload
              key={uploadKey}
              onUploadComplete={(nextMediaId, previewUrl, type) => {
                setMediaId(nextMediaId)
                setMediaUrl(previewUrl)
                setMediaType(type)
                setUploadError(null)
              }}
              onUploadError={(error) => setUploadError(error)}
              onClear={() => {
                setMediaUrl(null)
                setMediaId(null)
                setMediaType(null)
                clearSingleMediaValidationState()
              }}
              onUploadingChange={handleSingleMediaUploadingChange}
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
                clearSingleMediaValidationState()
                // If user goes from multi to single (1 item), keep it in carousel state
                // but also set legacy media for AI panel preview
                if (items.length === 1) {
                  setMediaId(items[0].mediaId)
                  setMediaUrl(items[0].previewUrl)
                  setMediaType(items[0].mediaType)
                } else if (items.length === 0) {
                  setMediaUrl(null)
                  setMediaId(null)
                  setMediaType(null)
                } else {
                  // Multi-media: set first item for AI preview
                  setMediaId(items[0].mediaId)
                  setMediaUrl(items[0].previewUrl)
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
              onUploadComplete={(nextMediaId, previewUrl, type) => {
                setMediaId(nextMediaId)
                setMediaUrl(previewUrl)
                setMediaType(type)
                setUploadError(null)
              }}
              onUploadError={(error) => setUploadError(error)}
              onClear={() => {
                setMediaUrl(null)
                setMediaId(null)
                setMediaType(null)
                clearSingleMediaValidationState()
              }}
              onUploadingChange={handleSingleMediaUploadingChange}
              onValidationChange={handleMediaValidationChange}
              selectedPlatform={selectedPlatformId}
              disabled={!isComposerEnabled}
            />
          )}
          {/* Client-side pre-validation messages (e.g. an invalid Story aspect ratio
              that never reaches server validation) surface here. Server validation
              error/warning detail is rendered by the uploader's shared
              MediaValidationCard, so it is not duplicated here. */}
          {uploadError && <div className="upload-error">{uploadError}</div>}
        </div>

        {/* Instagram Media Tags — tag people (IG Feed + single image or video) */}
        {showMediaTags && (
          <div className="form-group">
            <InstagramMediaTags
              caption={content}
              mediaTags={mediaTags}
              onMediaTagsChange={setMediaTags}
              mediaUrl={mediaUrl}
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
                mediaUrl={carouselItems[selectedCarouselItemIndex].previewUrl}
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

      {/* Confirm before discarding a dirty draft on a Meta-channel switch. Canceling
          keeps the current channel and draft untouched; confirming clears the draft
          and moves to the chosen channel. */}
      <ConfirmDialog
        isOpen={pendingChannelSwitch !== null}
        title="Switch channel?"
        message="Switching channels will clear your current draft. Continue?"
        confirmText="Switch & clear"
        cancelText="Keep draft"
        confirmVariant="danger"
        onConfirm={() => {
          if (pendingChannelSwitch) {
            applyChannelSwitch(pendingChannelSwitch)
          }
          setPendingChannelSwitch(null)
        }}
        onCancel={() => setPendingChannelSwitch(null)}
      />

      <ConfirmDialog
        isOpen={pendingPostTypeSwitch !== null}
        title="Change post type?"
        message="Changing the post type will clear your current draft details, uploaded media, and validation results. Continue?"
        confirmText="Change & clear"
        cancelText="Keep draft"
        confirmVariant="danger"
        onConfirm={() => {
          if (pendingPostTypeSwitch) {
            applyPostTypeSwitch(pendingPostTypeSwitch)
          }
          setPendingPostTypeSwitch(null)
        }}
        onCancel={() => setPendingPostTypeSwitch(null)}
      />
    </div>
  )
}
