import { useState, useEffect } from 'react'
import {
  aiApi,
  aiMediaApi,
  AiError,
  type AiPlatform,
  type AiTone,
  type AiGoal,
  type AiLength,
  type AiTextVariant,
  type AiPreFlightIssue,
  type AiIssueSeverity,
  type AiMediaCaptionVariant,
  type AiImageQualityIssue,
  type AiVideoFrame,
  type AiGeneratedVariant,
  type AiGenerateVariantsRequest,
} from '../api/ai'
import { type VoiceProfileSummary } from '../api/voiceProfiles'
import { type MediaType } from '../api/media'
import {
  getAiAssistAvailability,
  getMediaAiUnsupportedReason,
  getSupportedSingleImageMediaItem,
  normalizeActiveAiTab,
  shouldShowVoiceProfileControl,
  type AiAssistTab,
  type AiAssistMediaItem,
} from './aiAssistPanelState'
import { stripHashtags } from '../utils/textUtils'
import './AiAssistPanel.css'

type TabType = AiAssistTab

// Sticky language state - persists across content edits until explicitly changed
export interface StickyLanguageState {
  languageCode: string // 'unknown' if not yet detected
  confidence: number
  isReliable: boolean
}

interface AiAssistPanelProps {
  text: string
  onApplyText: (text: string, newLanguage?: string) => void
  onAppendText: (text: string) => void
  // Sticky language state (managed by parent)
  stickyLanguage: StickyLanguageState
  // Function to ensure language is detected (returns cached if known, else detects)
  ensureLanguageDetected: () => Promise<StickyLanguageState>
  // Function to reset language to unknown (triggers re-detect on next Generate)
  resetLanguage: () => void
  // Platform (from main form platform selection)
  platform: AiPlatform | null
  // Media props
  mediaUrl?: string | null
  mediaType?: MediaType | null
  mediaItems?: AiAssistMediaItem[]
  onSelectThumbnail?: (thumbnailUrl: string) => void
  // Voice Profile props
  voiceProfiles: VoiceProfileSummary[]
  onVoiceProfileModalOpen: (profileId?: string | null) => void
  // Goal state (shared with time suggestions)
  goal: AiGoal
  onGoalChange: (goal: AiGoal) => void
  // Disabled state - when true, all AI features are disabled (no connected account/page)
  disabled?: boolean
}

// Text Results
interface VariantsResult {
  type: 'variants'
  variants: AiTextVariant[]
}

interface HashtagsResult {
  type: 'hashtags'
  hashtags: string[]
}

interface PreFlightResult {
  type: 'preflight'
  score: number
  issues: AiPreFlightIssue[]
}

interface GeneratedVariantsResult {
  type: 'generated'
  variants: AiGeneratedVariant[]
}

interface CaptionsResult {
  type: 'captions'
  sourceLanguage: string
  sourceConfidence: number
  sourceIsReliable: boolean
  outputLanguage: string
  captions: string[]
  warnings: string[]
}

type TextResult = VariantsResult | HashtagsResult | PreFlightResult | GeneratedVariantsResult | CaptionsResult

// Media Results
interface MediaCaptionsResult {
  type: 'captions'
  variants: AiMediaCaptionVariant[]
}

interface QualityResult {
  type: 'quality'
  score: number
  issues: AiImageQualityIssue[]
}

interface AltTextResult {
  type: 'alttext'
  altText: string
}

interface ThumbnailsResult {
  type: 'thumbnails'
  frames: AiVideoFrame[]
  selectedIndex: number | null
}

type MediaResult = MediaCaptionsResult | QualityResult | AltTextResult | ThumbnailsResult

const MEDIA_AI_UNSUPPORTED_MESSAGE = 'Media AI supports a single image only. Video and multi-photo posts are not supported yet.'

const toneOptions: { value: AiTone; label: string }[] = [
  { value: 'Professional', label: 'Professional' },
  { value: 'Casual', label: 'Casual' },
  { value: 'Funny', label: 'Funny' },
  { value: 'Humorous', label: 'Humorous' },
  { value: 'Urgent', label: 'Urgent' },
  { value: 'Inspirational', label: 'Inspirational' },
  { value: 'Sales', label: 'Sales' },
]

const goalOptions: { value: AiGoal; label: string; description: string }[] = [
  { value: 'Engage', label: 'Engage', description: 'Encourage interaction and comments' },
  { value: 'Promote', label: 'Promote', description: 'Highlight value and drive action' },
  { value: 'Announce', label: 'Announce', description: 'Share news or updates' },
  { value: 'Educate', label: 'Educate', description: 'Provide tips and insights' },
  { value: 'Story', label: 'Story', description: 'Tell a mini narrative' },
]

const lengthOptions: { value: AiLength; label: string }[] = [
  { value: 'Short', label: 'Short' },
  { value: 'Medium', label: 'Medium' },
  { value: 'Long', label: 'Long' },
]

const languageOptions: { value: string; label: string }[] = [
  { value: 'auto', label: 'Same as detected' },
  { value: 'en', label: 'English' },
  { value: 'he', label: 'Hebrew' },
  { value: 'ru', label: 'Russian' },
  { value: 'ar', label: 'Arabic' },
  { value: 'es', label: 'Spanish' },
  { value: 'fr', label: 'French' },
  { value: 'de', label: 'German' },
]

const RTL_LANGUAGES = ['he', 'ar']

export function AiAssistPanel({
  text,
  onApplyText,
  onAppendText,
  stickyLanguage,
  ensureLanguageDetected,
  resetLanguage,
  platform: platformProp,
  mediaUrl,
  mediaType,
  mediaItems,
  onSelectThumbnail,
  voiceProfiles,
  onVoiceProfileModalOpen,
  goal,
  onGoalChange,
  disabled = false,
}: AiAssistPanelProps) {
  const [expanded, setExpanded] = useState(false)
  const noPlatform = !platformProp
  const platform = platformProp

  // When disabled (no connected account/page), all AI features should be blocked
  const isDisabled = disabled || noPlatform
  const [activeTab, setActiveTab] = useState<TabType>('text')
  const [tone, setTone] = useState<AiTone>('Professional')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Voice Profile state
  const [selectedVoiceProfileId, setSelectedVoiceProfileId] = useState<string | null>(null)

  // Text tab state - new generator controls
  const [length, setLength] = useState<AiLength>('Medium')
  const [includeEmojis, setIncludeEmojis] = useState(false)
  const [includeHashtags, setIncludeHashtags] = useState(false)
  const [includeCta, setIncludeCta] = useState(false)
  const [includeQuestion, setIncludeQuestion] = useState(false)

  // Text tab state - results
  const [textResult, setTextResult] = useState<TextResult | null>(null)
  const [copiedIndex, setCopiedIndex] = useState<number | null>(null)
  const [regeneratingIndex, setRegeneratingIndex] = useState<number | null>(null)
  const [selectedHashtags, setSelectedHashtags] = useState<Set<string>>(new Set())

  // Translate tab state - only output language selection lives here
  const [outputLanguage, setOutputLanguage] = useState<string>('en')
  const [captionVariants, setCaptionVariants] = useState<number>(1)
  const [strictMeaning, setStrictMeaning] = useState<boolean>(true)

  // Media tab state
  const [mediaResult, setMediaResult] = useState<MediaResult | null>(null)
  const [altTextCopied, setAltTextCopied] = useState(false)

  // Computed: is language known (not 'unknown')?
  const isLanguageKnown = stickyLanguage.languageCode !== 'unknown'

  // Clear selected voice profile if it's no longer available (e.g., deleted)
  useEffect(() => {
    if (selectedVoiceProfileId && !voiceProfiles.some(p => p.id === selectedVoiceProfileId)) {
      setSelectedVoiceProfileId(null)
    }
  }, [voiceProfiles, selectedVoiceProfileId])

  const handleVoiceProfileChange = (value: string) => {
    if (value === 'create') {
      onVoiceProfileModalOpen(null)
    } else if (value === 'none') {
      setSelectedVoiceProfileId(null)
    } else {
      setSelectedVoiceProfileId(value)
    }
  }

  const handleEditProfile = () => {
    if (selectedVoiceProfileId) {
      onVoiceProfileModalOpen(selectedVoiceProfileId)
    }
  }

  const isTextEmpty = !text.trim()
  const resolvedMediaItems: AiAssistMediaItem[] = mediaItems && mediaItems.length > 0
    ? mediaItems
    : (mediaUrl && mediaType && mediaType !== 'None'
        ? [{ assetUrl: mediaUrl, mediaType }]
        : [])
  const mediaSelectionKey = resolvedMediaItems
    .map((item) => `${item.assetUrl ?? ''}:${item.mediaType ?? 'unknown'}`)
    .join('|')
  const mediaAiUnsupportedReason = getMediaAiUnsupportedReason(resolvedMediaItems)
  const supportedMediaItem = getSupportedSingleImageMediaItem(resolvedMediaItems)
  const hasUploadedMedia = resolvedMediaItems.length > 0
  const { showMediaTab, showImageActions } = getAiAssistAvailability(resolvedMediaItems)
  const showVoiceProfileControl = shouldShowVoiceProfileControl(activeTab, resolvedMediaItems)

  const generatedVariantCount =
    textResult?.type === 'generated'
      ? textResult.variants.length
      : 0

  useEffect(() => {
    const nextActiveTab = normalizeActiveAiTab(activeTab, resolvedMediaItems)
    if (nextActiveTab !== activeTab) {
      setActiveTab(nextActiveTab)
      setMediaResult(null)
      setError(null)
    }
  }, [activeTab, mediaSelectionKey])

  useEffect(() => {
    setMediaResult(null)

    if (mediaAiUnsupportedReason !== null || activeTab === 'media') {
      setError(null)
    }
  }, [activeTab, mediaSelectionKey, mediaAiUnsupportedReason])

  // Text tab handlers
  const handleTextAction = async (action: () => Promise<void>) => {
    // Block all AI actions when disabled (no connected account/page)
    if (isDisabled || isTextEmpty) return

    setLoading(true)
    setError(null)
    setTextResult(null)

    try {
      await action()
    } catch (err) {
      handleError(err)
    } finally {
      setLoading(false)
    }
  }

  // Media tab handlers
  const handleMediaAction = async (action: () => Promise<void>) => {
    // Block all AI actions when disabled (no connected account/page)
    if (isDisabled || !supportedMediaItem || !showImageActions) return

    setLoading(true)
    setError(null)
    setMediaResult(null)

    try {
      await action()
    } catch (err) {
      handleError(err)
    } finally {
      setLoading(false)
    }
  }

  const handleError = (err: unknown) => {
    if (err instanceof AiError) {
      if (err.isRateLimited) {
        setError('AI quota reached (free tier). Try again tomorrow or enable billing.')
      } else if (err.isMediaTooLarge) {
        setError('Media too large or unsupported format.')
      } else if (err.isUnavailable) {
        setError('AI service is temporarily unavailable. Please try again later.')
      } else {
        setError(err.message)
      }
    } else if (err instanceof Error) {
      if (err.message.includes('Failed to load video')) {
        setError('Failed to load video. Please try again.')
      } else {
        setError(err.message || 'An unexpected error occurred. Please try again.')
      }
    } else {
      setError('An unexpected error occurred. Please try again.')
    }
  }

  // Text actions
  const handleHashtags = () =>
    handleTextAction(async () => {
      if (!platform) return // TypeScript guard - platform is required
      // Get sticky language (detects if unknown, uses cached if known)
      const langState = await ensureLanguageDetected()
      const response = await aiApi.hashtags(platform, text, langState.languageCode, selectedVoiceProfileId)
      setTextResult({ type: 'hashtags', hashtags: response.hashtags })
      // Select all hashtags by default
      setSelectedHashtags(new Set(response.hashtags))
    })

  const toggleHashtag = (hashtag: string) => {
    setSelectedHashtags((prev) => {
      const next = new Set(prev)
      if (next.has(hashtag)) {
        next.delete(hashtag)
      } else {
        next.add(hashtag)
      }
      return next
    })
  }

  const selectAllHashtags = () => {
    if (textResult?.type === 'hashtags') {
      setSelectedHashtags(new Set(textResult.hashtags))
    }
  }

  const deselectAllHashtags = () => {
    setSelectedHashtags(new Set())
  }

  const handlePreFlight = () =>
    handleTextAction(async () => {
      if (!platform) return // TypeScript guard - platform is required
      const response = await aiApi.preFlight(platform, text, selectedVoiceProfileId)
      setTextResult({ type: 'preflight', score: response.score, issues: response.issues })
    })

  // New generate variants handler - Text tab (same-language rewrite)
  const handleGenerateVariants = () =>
    handleTextAction(async () => {
      if (!platform) return // TypeScript guard - platform is required
      // Get sticky language (detects if unknown, uses cached if known)
      const langState = await ensureLanguageDetected()

      const request: AiGenerateVariantsRequest = {
        platform,
        inputText: text,
        goal,
        tone,
        length,
        includeEmojis,
        includeHashtags,
        includeCta,
        includeQuestion,
        numVariants: 3,
        voiceProfileId: selectedVoiceProfileId,
        language: langState.languageCode, // Always use sticky language for Text tab
      }
      const response = await aiApi.generateVariants(request)
      setTextResult({ type: 'generated', variants: response.variants })
    })

  // Regenerate a single variant
  const handleRegenerateVariant = async (index: number) => {
    // Block when disabled (no connected account/page)
    if (isDisabled || isTextEmpty || loading || !platform) return

    setRegeneratingIndex(index)
    setError(null)

    try {
      // Use current sticky language (should already be known from initial generate)
      const langState = await ensureLanguageDetected()

      const request: AiGenerateVariantsRequest = {
        platform,
        inputText: text,
        goal,
        tone,
        length,
        includeEmojis,
        includeHashtags,
        includeCta,
        includeQuestion,
        numVariants: 1,
        regenerateIndex: index,
        voiceProfileId: selectedVoiceProfileId,
        language: langState.languageCode,
      }
      const response = await aiApi.generateVariants(request)

      // Replace the single variant in the existing results
      if (textResult?.type === 'generated' && response.variants.length > 0) {
        const newVariants = [...textResult.variants]
        newVariants[index] = response.variants[0]
        setTextResult({ type: 'generated', variants: newVariants })
      }
    } catch (err) {
      handleError(err)
    } finally {
      setRegeneratingIndex(null)
    }
  }

  // Translate/Caption generation handler - Translate tab
  const handleGenerateCaptions = () =>
    handleTextAction(async () => {
      if (!platform) return // TypeScript guard - platform is required
      // Get sticky language (detects if unknown, uses cached if known)
      const langState = await ensureLanguageDetected()

      // Strip hashtags before translating - they will be removed on Apply
      // User can regenerate hashtags in target language after applying translation
      const { cleanedText } = stripHashtags(text)

      const response = await aiApi.generateCaptions({
        text: cleanedText, // Send text without hashtags
        platform,
        outputLanguage: outputLanguage, // Use selected target language
        variants: captionVariants,
        strictMeaning,
        // Pass sticky language to backend so it skips detection
        sourceLanguage: langState.languageCode,
      })
      setTextResult({
        type: 'captions',
        sourceLanguage: response.sourceLanguage,
        sourceConfidence: response.sourceConfidence,
        sourceIsReliable: response.sourceIsReliable,
        outputLanguage: response.outputLanguage,
        captions: response.captions,
        warnings: response.warnings,
      })
    })

  // Media actions
  const handleImageCaptionIdeas = () =>
    handleMediaAction(async () => {
      if (!platform) return // TypeScript guard - platform is required
      const mediaAssetUrl = supportedMediaItem?.assetUrl
      if (!mediaAssetUrl) return
      const response = await aiMediaApi.imageCaptionIdeas(
        platform,
        [{ assetUrl: mediaAssetUrl, assetType: 'image' }],
        text || undefined,
        selectedVoiceProfileId
      )
      setMediaResult({ type: 'captions', variants: response.variants })
    })

  const handleImageQualityCheck = () =>
    handleMediaAction(async () => {
      const mediaAssetUrl = supportedMediaItem?.assetUrl
      if (!mediaAssetUrl) return
      const response = await aiMediaApi.imageQualityCheck([
        { assetUrl: mediaAssetUrl, assetType: 'image' },
      ])
      setMediaResult({ type: 'quality', score: response.score, issues: response.issues })
    })

  const handleAltText = () =>
    handleMediaAction(async () => {
      const mediaAssetUrl = supportedMediaItem?.assetUrl
      if (!mediaAssetUrl) return
      const response = await aiMediaApi.altText([
        { assetUrl: mediaAssetUrl, assetType: 'image' },
      ])
      setMediaResult({ type: 'alttext', altText: response.altText })
    })

  // Result handlers
  const handleApply = (variantText: string, outputLang?: string) => {
    // When applying a translation (outputLang is provided), the variant text
    // is the translated caption WITHOUT hashtags. Just apply it directly.
    // The old hashtags from the source content are intentionally not preserved
    // since they would be in the wrong language. User can generate new hashtags manually.
    onApplyText(variantText, outputLang)
    setTextResult(null)
    setMediaResult(null)
  }

  const handleCopy = async (text: string, index: number) => {
    await navigator.clipboard.writeText(text)
    setCopiedIndex(index)
    setTimeout(() => setCopiedIndex(null), 2000)
  }

  const handleCopyAltText = async (altText: string) => {
    await navigator.clipboard.writeText(altText)
    setAltTextCopied(true)
    setTimeout(() => setAltTextCopied(false), 2000)
  }

  const handleInsertHashtags = (hashtags: string[]) => {
    if (hashtags.length === 0) return
    const hashtagText = ' ' + hashtags.join(' ')
    onAppendText(hashtagText)
    setTextResult(null)
    setSelectedHashtags(new Set())
  }

  const handleSelectThumbnail = (index: number) => {
    if (mediaResult?.type === 'thumbnails') {
      setMediaResult({ ...mediaResult, selectedIndex: index })
    }
  }

  const handleSetThumbnail = () => {
    if (mediaResult?.type === 'thumbnails' && mediaResult.selectedIndex !== null && onSelectThumbnail) {
      const frame = mediaResult.frames[mediaResult.selectedIndex]
      onSelectThumbnail(frame.imageUrl)
      setMediaResult(null)
    }
  }

  const getSeverityIcon = (severity: AiIssueSeverity): string => {
    switch (severity) {
      case 'Error':
        return '!'
      case 'Warning':
        return '!'
      case 'Info':
        return 'i'
    }
  }

  const getScoreColor = (score: number): string => {
    if (score >= 80) return 'score-good'
    if (score >= 60) return 'score-ok'
    return 'score-poor'
  }

  const formatTimestamp = (seconds: number): string => {
    const mins = Math.floor(seconds / 60)
    const secs = Math.floor(seconds % 60)
    return `${mins}:${secs.toString().padStart(2, '0')}`
  }

  // Get media filename for display
  const getMediaFileName = (): string => {
    if (!supportedMediaItem?.assetUrl) return ''
    const parts = supportedMediaItem.assetUrl.split('/')
    return parts[parts.length - 1] || 'media'
  }

  return (
    <div className={`ai-assist-panel ${expanded ? 'expanded' : ''}`}>
      <button
        type="button"
        className="ai-assist-accordion"
        onClick={() => setExpanded((prev) => !prev)}
        aria-expanded={expanded}
      >
        <span className="ai-assist-accordion-icon" aria-hidden="true">🤖</span>
        <span className="ai-assist-accordion-copy">
          <span className="ai-assist-accordion-title">AI Content Assist</span>
          <span className="ai-assist-accordion-subtitle">Generate captions, translate, or improve your post text.</span>
        </span>
        {generatedVariantCount > 0 && (
          <span className="ai-assist-accordion-badge">{generatedVariantCount} variants generated</span>
        )}
        {loading && <span className="ai-assist-accordion-loading">...</span>}
        <span className={`ai-assist-accordion-chevron ${expanded ? 'expanded' : ''}`} aria-hidden="true">▼</span>
      </button>

      {expanded && (
        <div className="ai-assist-content">
          <div className="ai-assist-header">
            <div className="ai-tabs">
              <button
                type="button"
                className={`ai-tab ${activeTab === 'text' ? 'active' : ''}`}
                onClick={() => {
                  setActiveTab('text')
                  setError(null)
                }}
              >
                Text
              </button>
              <button
                type="button"
                className={`ai-tab ${activeTab === 'translate' ? 'active' : ''}`}
                onClick={() => {
                  setActiveTab('translate')
                  setError(null)
                }}
              >
                Translate
              </button>
              {showMediaTab && (
                <button
                  type="button"
                  className={`ai-tab ${activeTab === 'media' ? 'active' : ''}`}
                  onClick={() => {
                    setActiveTab('media')
                    setError(null)
                  }}
                >
                  Media
                </button>
              )}
            </div>
          </div>

          {showVoiceProfileControl && (
          <div className="ai-controls">
            <div className="ai-control-group ai-voice-profile-control">
              <label htmlFor="ai-voice-profile">Voice Profile</label>
              <div className="voice-profile-selector">
                <select
                  id="ai-voice-profile"
                  value={selectedVoiceProfileId || 'none'}
                  onChange={(e) => handleVoiceProfileChange(e.target.value)}
                  disabled={isDisabled || loading}
                >
                  <option value="none">None (Default)</option>
                  {voiceProfiles.map((profile) => (
                    <option key={profile.id} value={profile.id}>
                      {profile.name}
                    </option>
                  ))}
                  <option value="create">+ Create new...</option>
                </select>
                {selectedVoiceProfileId && (
                  <button
                    type="button"
                    className="voice-profile-edit-btn"
                    onClick={handleEditProfile}
                    disabled={isDisabled || loading}
                    title="Edit voice profile"
                  >
                    Edit
                  </button>
                )}
              </div>
            </div>
          </div>
          )}

          {!isDisabled && mediaAiUnsupportedReason !== null && mediaAiUnsupportedReason !== 'no-media' && (
            <div className="ai-empty-state">{MEDIA_AI_UNSUPPORTED_MESSAGE}</div>
          )}

          {/* Text Tab Content */}
          {activeTab === 'text' && (
        <>
          {/* disabled state message handled by top-level banner */}
          {!disabled && noPlatform && <div className="ai-empty-state">Select a platform to enable AI features</div>}
          {!disabled && !noPlatform && isTextEmpty && <div className="ai-empty-state">Enter text to enable AI features</div>}

          {/* Generator Controls */}
          <div className="ai-generator-controls">
            <div className="ai-control-row">
              <div className="ai-control-group">
                <label htmlFor="ai-goal">Goal</label>
                <select
                  id="ai-goal"
                  value={goal}
                  onChange={(e) => onGoalChange(e.target.value as AiGoal)}
                  disabled={isDisabled || loading}
                >
                  {goalOptions.map((opt) => (
                    <option key={opt.value} value={opt.value} title={opt.description}>
                      {opt.label}
                    </option>
                  ))}
                </select>
              </div>

              <div className="ai-control-group">
                <label htmlFor="ai-tone-gen">Tone</label>
                <select
                  id="ai-tone-gen"
                  value={tone}
                  onChange={(e) => setTone(e.target.value as AiTone)}
                  disabled={isDisabled || loading}
                >
                  {toneOptions.map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </select>
              </div>

              <div className="ai-control-group">
                <label htmlFor="ai-length">Length</label>
                <select
                  id="ai-length"
                  value={length}
                  onChange={(e) => setLength(e.target.value as AiLength)}
                  disabled={isDisabled || loading}
                >
                  {lengthOptions.map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            <div className="ai-include-toggles">
              <label className="ai-toggle">
                <input
                  type="checkbox"
                  checked={includeEmojis}
                  onChange={(e) => setIncludeEmojis(e.target.checked)}
                  disabled={isDisabled || loading}
                />
                <span>Emojis</span>
              </label>
              <label className="ai-toggle">
                <input
                  type="checkbox"
                  checked={includeHashtags}
                  onChange={(e) => setIncludeHashtags(e.target.checked)}
                  disabled={isDisabled || loading}
                />
                <span>Hashtags</span>
              </label>
              <label className="ai-toggle">
                <input
                  type="checkbox"
                  checked={includeCta}
                  onChange={(e) => setIncludeCta(e.target.checked)}
                  disabled={isDisabled || loading}
                />
                <span>CTA</span>
              </label>
              <label className="ai-toggle">
                <input
                  type="checkbox"
                  checked={includeQuestion}
                  onChange={(e) => setIncludeQuestion(e.target.checked)}
                  disabled={isDisabled || loading}
                />
                <span>Question</span>
              </label>
            </div>

            <button
              type="button"
              className="ai-generate-btn"
              onClick={handleGenerateVariants}
              disabled={isDisabled || isTextEmpty || loading}
            >
              {loading && !regeneratingIndex ? 'Generating...' : 'Generate'}
            </button>
          </div>

          {/* Quick Actions (secondary) */}
          <div className="ai-quick-actions">
            <button
              type="button"
              className="ai-quick-btn"
              onClick={handleHashtags}
              disabled={isDisabled || isTextEmpty || loading}
              title="Suggest relevant hashtags"
            >
              # Hashtags
            </button>
            <button
              type="button"
              className="ai-quick-btn"
              onClick={handlePreFlight}
              disabled={isDisabled || isTextEmpty || loading}
              title="Check post quality before publishing"
            >
              Pre-flight
            </button>
          </div>
        </>
          )}

          {/* Translate Tab Content */}
          {activeTab === 'translate' && (
        <>
          {/* disabled state message handled by top-level banner */}
          {!disabled && noPlatform && <div className="ai-empty-state">Select a platform to enable translation</div>}
          {!disabled && !noPlatform && isTextEmpty && <div className="ai-empty-state">Enter text to enable translation</div>}

          {!isDisabled && !isTextEmpty && (
            <>
              {/* Language Detection Display */}
              <div className="ai-language-detection">
                <strong>Source language:</strong>{' '}
                {isLanguageKnown ? (
                  <>
                    {languageOptions.find((l) => l.value === stickyLanguage.languageCode)?.label || stickyLanguage.languageCode}{' '}
                    ({Math.round(stickyLanguage.confidence * 100)}%)
                    {!stickyLanguage.isReliable && <span className="language-warning"> (low confidence)</span>}
                    <button
                      type="button"
                      className="ai-redetect-btn"
                      onClick={resetLanguage}
                      disabled={loading}
                      title="Re-detect language on next Generate"
                    >
                      Re-detect
                    </button>
                  </>
                ) : (
                  <span className="language-pending">unknown (will detect on Generate)</span>
                )}
              </div>

              {/* Output Language Selection */}
              <div className="ai-control-group">
                <label htmlFor="ai-output-language">Translate to</label>
                <select
                  id="ai-output-language"
                  value={outputLanguage}
                  onChange={(e) => setOutputLanguage(e.target.value)}
                  disabled={loading}
                >
                  {languageOptions.filter(opt => opt.value !== 'auto').map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </select>
              </div>

              {/* Caption Variants */}
              <div className="ai-control-group">
                <label htmlFor="ai-caption-variants">Variants</label>
                <select
                  id="ai-caption-variants"
                  value={captionVariants}
                  onChange={(e) => setCaptionVariants(Number(e.target.value))}
                  disabled={loading}
                >
                  <option value={1}>1</option>
                  <option value={3}>3</option>
                </select>
              </div>

              {/* Toggles */}
              <div className="ai-toggle-group">
                <label className="ai-toggle">
                  <input
                    type="checkbox"
                    checked={strictMeaning}
                    onChange={(e) => setStrictMeaning(e.target.checked)}
                    disabled={loading}
                  />
                  <span>Keep original meaning (strict)</span>
                </label>
              </div>

              {/* Generate Button */}
              <div className="ai-actions">
                <button
                  type="button"
                  className="ai-action-btn primary"
                  onClick={handleGenerateCaptions}
                  disabled={loading}
                >
                  {loading ? 'Generating...' : 'Generate'}
                </button>
              </div>
            </>
          )}
        </>
          )}

          {/* Media Tab Content */}
          {showMediaTab && activeTab === 'media' && (
        <>
          {/* disabled state message handled by top-level banner */}
          {!disabled && noPlatform && <div className="ai-empty-state">Select a platform to enable media AI features</div>}
          {!isDisabled && supportedMediaItem && (
            <div className="ai-media-info">
              <span className="media-type-indicator image">
                Photo
              </span>
              <span className="media-filename">{getMediaFileName()}</span>
            </div>
          )}

          {/* Image Features */}
          {!isDisabled && showImageActions && (
            <div className="ai-actions">
              <button
                type="button"
                className="ai-action-btn"
                onClick={handleImageCaptionIdeas}
                disabled={isDisabled || !supportedMediaItem || loading}
                title="Generate caption ideas based on image"
              >
                Caption ideas
              </button>
              <button
                type="button"
                className="ai-action-btn"
                onClick={handleImageQualityCheck}
                disabled={isDisabled || !supportedMediaItem || loading}
                title="Check image quality for social media"
              >
                Quality check
              </button>
              <button
                type="button"
                className="ai-action-btn"
                onClick={handleAltText}
                disabled={isDisabled || !supportedMediaItem || loading}
                title="Generate accessibility alt text"
              >
                Alt text
              </button>
            </div>
          )}

          {!isDisabled && !hasUploadedMedia && (
            <div className="ai-empty-state">Upload an image to enable media AI features</div>
          )}
        </>
          )}

          {/* Loading State */}
          {loading && (
            <div className="ai-loading">
              <div className="ai-spinner"></div>
              <span>AI is analyzing...</span>
            </div>
          )}

          {/* Error Display */}
          {error && (
            <div className="ai-error">
              <span className="error-icon">!</span>
              <span>{error}</span>
            </div>
          )}

          {/* Text Results - Generated Variants (new workflow) */}
          {activeTab === 'text' && textResult?.type === 'generated' && (
        <div className="ai-results">
          <h4>Generated Variants</h4>
          <div className="ai-variants">
            {textResult.variants.map((variant, index) => (
              <div
                key={index}
                className={`ai-variant-card ${regeneratingIndex === index ? 'regenerating' : ''}`}
              >
                <div className="variant-header">
                  <span className="variant-number">Option {index + 1}</span>
                  {regeneratingIndex === index && (
                    <span className="variant-regenerating">Regenerating...</span>
                  )}
                </div>
                <div className="variant-text">{variant.text}</div>
                <div className="variant-meta">
                  <span className="char-count">{variant.text.length} chars</span>
                </div>
                <div className="variant-actions">
                  <button
                    type="button"
                    className="variant-btn variant-btn-apply"
                    onClick={() => handleApply(variant.text)}
                    disabled={regeneratingIndex !== null}
                  >
                    Apply
                  </button>
                  <button
                    type="button"
                    className="variant-btn variant-btn-copy"
                    onClick={() => handleCopy(variant.text, index)}
                    disabled={regeneratingIndex !== null}
                  >
                    {copiedIndex === index ? 'Copied!' : 'Copy'}
                  </button>
                  <button
                    type="button"
                    className="variant-btn variant-btn-regenerate"
                    onClick={() => handleRegenerateVariant(index)}
                    disabled={regeneratingIndex !== null || loading}
                    title="Generate a new version of this variant"
                  >
                    {regeneratingIndex === index ? '...' : 'Regenerate'}
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
          )}

          {/* Text Results - Legacy variants (from old actions, kept for backward compat) */}
          {activeTab === 'text' && textResult?.type === 'variants' && (
        <div className="ai-results">
          <h4>Suggestions</h4>
          <div className="ai-variants">
            {textResult.variants.map((variant, index) => (
              <div key={index} className="ai-variant-card">
                <div className="variant-title">{variant.title}</div>
                <div className="variant-text">{variant.text}</div>
                <div className="variant-meta">
                  <span className="char-count">{variant.text.length} chars</span>
                </div>
                <div className="variant-actions">
                  <button
                    type="button"
                    className="variant-btn variant-btn-apply"
                    onClick={() => handleApply(variant.text)}
                  >
                    Apply
                  </button>
                  <button
                    type="button"
                    className="variant-btn variant-btn-copy"
                    onClick={() => handleCopy(variant.text, index)}
                  >
                    {copiedIndex === index ? 'Copied!' : 'Copy'}
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
          )}

          {/* Caption Results (Translate Tab) */}
          {activeTab === 'translate' && textResult?.type === 'captions' && (
        <div className="ai-results">
          <h4>Generated Captions</h4>
          <div className="caption-meta">
            <span>
              {textResult.sourceLanguage} → {textResult.outputLanguage}
            </span>
            {textResult.warnings.length > 0 && (
              <div className="caption-warnings">
                {textResult.warnings.map((warning, index) => (
                  <div key={index} className="warning-item">
                    {warning}
                  </div>
                ))}
              </div>
            )}
          </div>
          <div className="ai-variants">
            {textResult.captions.map((caption, index) => {
              const isRTL = RTL_LANGUAGES.includes(textResult.outputLanguage)
              return (
                <div key={index} className="ai-variant-card">
                  <div className="variant-header">
                    <span className="variant-number">Caption {index + 1}</span>
                  </div>
                  <div className={`variant-text ${isRTL ? 'rtl' : ''}`} dir={isRTL ? 'rtl' : 'ltr'}>
                    {caption}
                  </div>
                  <div className="variant-meta">
                    <span className="char-count">{caption.length} chars</span>
                  </div>
                  <div className="variant-actions">
                    <button
                      type="button"
                      className="variant-btn variant-btn-apply"
                      onClick={() => handleApply(caption, textResult.outputLanguage)}
                    >
                      Apply
                    </button>
                    <button
                      type="button"
                      className="variant-btn variant-btn-copy"
                      onClick={() => handleCopy(caption, index)}
                    >
                      {copiedIndex === index ? 'Copied!' : 'Copy'}
                    </button>
                  </div>
                </div>
              )
            })}
          </div>
        </div>
          )}

          {activeTab === 'text' && textResult?.type === 'hashtags' && (
        <div className="ai-results">
          <h4>Suggested Hashtags</h4>
          <p className="hashtags-hint">Click to select/deselect hashtags</p>
          <div className="ai-hashtags">
            {textResult.hashtags.map((hashtag, index) => (
              <button
                key={index}
                type="button"
                className={`hashtag-chip ${selectedHashtags.has(hashtag) ? 'selected' : ''}`}
                onClick={() => toggleHashtag(hashtag)}
              >
                {hashtag}
              </button>
            ))}
          </div>
          <div className="hashtags-actions">
            <div className="hashtags-select-actions">
              <button
                type="button"
                className="hashtags-select-btn"
                onClick={selectAllHashtags}
              >
                Select All
              </button>
              <button
                type="button"
                className="hashtags-select-btn"
                onClick={deselectAllHashtags}
              >
                Deselect All
              </button>
            </div>
            <button
              type="button"
              className="hashtags-insert-btn"
              onClick={() => handleInsertHashtags(Array.from(selectedHashtags))}
              disabled={selectedHashtags.size === 0}
            >
              Insert {selectedHashtags.size > 0 ? `(${selectedHashtags.size})` : ''} at end
            </button>
          </div>
        </div>
          )}

          {activeTab === 'text' && textResult?.type === 'preflight' && (
        <div className="ai-results">
          <h4>Pre-flight Check</h4>
          <div className={`preflight-score ${getScoreColor(textResult.score)}`}>
            <span className="score-value">{textResult.score}</span>
            <span className="score-label">/ 100</span>
          </div>
          <div className="preflight-issues">
            {textResult.issues.map((issue, index) => (
              <div key={index} className={`preflight-issue severity-${issue.severity.toLowerCase()}`}>
                <span className="issue-icon">{getSeverityIcon(issue.severity)}</span>
                <div className="issue-content">
                  <div className="issue-message">{issue.message}</div>
                  {issue.suggestedFix && (
                    <div className="issue-fix">
                      <strong>Fix:</strong> {issue.suggestedFix}
                    </div>
                  )}
                </div>
              </div>
            ))}
          </div>
          {textResult.score < 70 && (
            <button
              type="button"
              className="fix-with-polish-btn"
              onClick={handleGenerateVariants}
              disabled={loading}
            >
              Generate improved versions
            </button>
          )}
        </div>
          )}

          {/* Media Results - Captions */}
          {activeTab === 'media' && mediaResult?.type === 'captions' && (
        <div className="ai-results">
          <h4>Caption Ideas</h4>
          <div className="ai-variants">
            {mediaResult.variants.map((variant, index) => (
              <div key={index} className="ai-variant-card">
                <div className="variant-title">{variant.title}</div>
                <div className="variant-text">{variant.text}</div>
                <div className="variant-meta">
                  <span className="char-count">{variant.text.length} chars</span>
                </div>
                <div className="variant-actions">
                  <button
                    type="button"
                    className="variant-btn variant-btn-apply"
                    onClick={() => handleApply(variant.text)}
                  >
                    Apply
                  </button>
                  <button
                    type="button"
                    className="variant-btn variant-btn-copy"
                    onClick={() => handleCopy(variant.text, index)}
                  >
                    {copiedIndex === index ? 'Copied!' : 'Copy'}
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
          )}

          {/* Media Results - Quality Check */}
          {activeTab === 'media' && mediaResult?.type === 'quality' && (
        <div className="ai-results">
          <h4>Image Quality Check</h4>
          <div className={`preflight-score ${getScoreColor(mediaResult.score)}`}>
            <span className="score-value">{mediaResult.score}</span>
            <span className="score-label">/ 100</span>
          </div>
          {mediaResult.issues.length === 0 ? (
            <div className="quality-pass">Image looks great for social media!</div>
          ) : (
            <div className="preflight-issues">
              {mediaResult.issues.map((issue, index) => (
                <div
                  key={index}
                  className={`preflight-issue severity-${issue.severity.toLowerCase()}`}
                >
                  <span className="issue-icon">{getSeverityIcon(issue.severity)}</span>
                  <div className="issue-content">
                    <div className="issue-message">{issue.message}</div>
                    {issue.suggestedFix && (
                      <div className="issue-fix">
                        <strong>Fix:</strong> {issue.suggestedFix}
                      </div>
                    )}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
          )}

          {/* Media Results - Alt Text */}
          {activeTab === 'media' && mediaResult?.type === 'alttext' && (
        <div className="ai-results">
          <h4>Generated Alt Text</h4>
          <div className="alt-text-result">
            <div className="alt-text-content">{mediaResult.altText}</div>
            <button
              type="button"
              className="variant-btn variant-btn-copy alt-text-copy"
              onClick={() => handleCopyAltText(mediaResult.altText)}
            >
              {altTextCopied ? 'Copied!' : 'Copy'}
            </button>
          </div>
        </div>
          )}

          {/* Media Results - Thumbnails */}
          {activeTab === 'media' && mediaResult?.type === 'thumbnails' && (
        <div className="ai-results">
          <h4>Select Thumbnail</h4>
          <div className="thumbnail-grid">
            {mediaResult.frames.map((frame, index) => (
              <div
                key={index}
                className={`thumbnail-item ${mediaResult.selectedIndex === index ? 'selected' : ''}`}
                onClick={() => handleSelectThumbnail(index)}
              >
                <img src={frame.imageUrl} alt={`Frame at ${formatTimestamp(frame.timestampSeconds)}`} />
                <span className="thumbnail-timestamp">{formatTimestamp(frame.timestampSeconds)}</span>
              </div>
            ))}
          </div>
          <button
            type="button"
            className="set-thumbnail-btn"
            onClick={handleSetThumbnail}
            disabled={mediaResult.selectedIndex === null}
          >
            Set thumbnail
          </button>
        </div>
          )}
        </div>
      )}
    </div>
  )
}
