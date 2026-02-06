import { useState, useEffect, useCallback, useRef } from 'react'
import { metaApi } from '../api/meta'
import { aiApi, type AiPlatform, type AiGoal, type AudienceLocationMode } from '../api/ai'
import type { MediaType, ValidationStatus, MediaValidationError, MediaValidationWarning } from '../api/media'
import type { ConnectedPage } from '../types/meta'
import { MediaUpload } from './MediaUpload'
import { AiAssistPanel, type StickyLanguageState } from './AiAssistPanel'
import { SuggestedTimes } from './SuggestedTimes'
import { type VoiceProfileSummary } from '../api/voiceProfiles'
import {
  getPostTextMaxChars,
  getPlatformDisplayName,
  type PlatformId,
} from '../constants/validationLimits'
import { MAX_PLATFORMS_PER_POST } from '../constants/features'
import { useComposerEnabled } from '../hooks/useComposerEnabled'
import './SchedulePost.css'

interface SchedulePostProps {
  onSchedule: (data: {
    content: string
    scheduledDate: string
    scheduledTime: string
    platforms: string[]
    targetPageId?: string
    mediaUrl?: string
    mediaType?: MediaType
    selectedThumbnailUrl?: string
  }) => void
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

export function SchedulePost({ onSchedule, voiceProfiles, onVoiceProfileModalOpen, onNavigate }: SchedulePostProps) {
  const [content, setContent] = useState('')
  const [scheduledDate, setScheduledDate] = useState('')
  const [scheduledTime, setScheduledTime] = useState('')
  const [selectedPlatforms, setSelectedPlatforms] = useState<string[]>([])
  const [connectedPages, setConnectedPages] = useState<ConnectedPage[]>([])
  const [isAccountConnected, setIsAccountConnected] = useState(false)
  const [selectedPageId, setSelectedPageId] = useState<string>('')
  const [loadingPages, setLoadingPages] = useState(false)
  const [mediaUrl, setMediaUrl] = useState<string | null>(null)
  const [mediaType, setMediaType] = useState<MediaType | null>(null)
  const [uploadError, setUploadError] = useState<string | null>(null)
  const [uploadKey, setUploadKey] = useState(0)
  const [isUploading, setIsUploading] = useState(false)
  const [aiPanelKey, setAiPanelKey] = useState(0)
  const [suggestedTimesKey, setSuggestedTimesKey] = useState(0)
  const [selectedThumbnailUrl, setSelectedThumbnailUrl] = useState<string | null>(null)
  const [mediaValidationStatus, setMediaValidationStatus] = useState<ValidationStatus | null>(null)
  const [mediaValidationErrors, setMediaValidationErrors] = useState<MediaValidationError[]>([])

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

  // Load connected Facebook Pages on mount
  useEffect(() => {
    loadConnectedPages()
  }, [])

  const loadConnectedPages = async () => {
    try {
      setLoadingPages(true)
      const response = await metaApi.getConnection()
      setIsAccountConnected(response.isConnected)
      if (response.isConnected && response.connection) {
        setConnectedPages(response.connection.pages)
        // Auto-select first page if only one exists
        if (response.connection.pages.length === 1) {
          setSelectedPageId(response.connection.pages[0].id)
        }
      }
    } catch (err) {
      console.error('Failed to load connected pages:', err)
    } finally {
      setLoadingPages(false)
    }
  }

  const isFacebookSelected = selectedPlatforms.includes('facebook')

  // Determine if composer should be enabled based on platform and connection state
  const composerState = useComposerEnabled({
    selectedPlatforms,
    connectedPages,
    isAccountConnected,
    selectedPageId,
    loadingPages,
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

  const selectPlatform = (platformId: string) => {
    if (MAX_PLATFORMS_PER_POST === 1) {
      // Single selection mode: replace current selection
      if (selectedPlatforms.includes(platformId)) {
        // Clicking selected platform deselects it
        setSelectedPlatforms([])
        if (platformId === 'facebook') {
          setSelectedPageId('')
        }
      } else {
        // Select new platform, replacing any previous selection
        setSelectedPlatforms([platformId])
        // Clear page selection if Facebook is deselected
        if (selectedPlatforms.includes('facebook') && platformId !== 'facebook') {
          setSelectedPageId('')
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
    }
  }

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault()

    // Require either content or media, plus date/time/platform
    if ((!content && !mediaUrl) || !scheduledDate || !scheduledTime || selectedPlatforms.length === 0) {
      return
    }

    // Require page selection for Facebook
    if (isFacebookSelected && !selectedPageId) {
      return
    }

    onSchedule({
      content,
      scheduledDate,
      scheduledTime,
      platforms: selectedPlatforms,
      targetPageId: isFacebookSelected ? selectedPageId : undefined,
      mediaUrl: mediaUrl || undefined,
      mediaType: mediaType || undefined,
      selectedThumbnailUrl: selectedThumbnailUrl || undefined,
    })

    // Reset form including language
    setContent('')
    setScheduledDate('')
    setScheduledTime('')
    setSelectedPlatforms([])
    setSelectedPageId('')
    setMediaUrl(null)
    setMediaType(null)
    setUploadError(null)
    setUploadKey(k => k + 1)
    setSelectedThumbnailUrl(null)
    setMediaValidationStatus(null)
    setMediaValidationErrors([])
    setStickyLanguage({ languageCode: 'unknown', confidence: 0, isReliable: false })
  }

  // Get the max character limit for the first selected platform
  const selectedPlatformId = selectedPlatforms[0] as PlatformId | undefined
  const maxChars = getPostTextMaxChars(selectedPlatformId ?? null)
  const isTextTooLong = content.length > maxChars
  const platformDisplayName = selectedPlatformId ? getPlatformDisplayName(selectedPlatformId) : ''

  // Media validation status check - invalid media blocks submission
  const hasInvalidMedia = mediaUrl && mediaValidationStatus === 'Invalid'

  // Form is valid if there's content OR media, plus date/time/platform, not uploading, text within limits, and no invalid media
  const isFormValid = (content || mediaUrl) && scheduledDate && scheduledTime &&
    selectedPlatforms.length > 0 &&
    (!isFacebookSelected || selectedPageId) &&
    !isUploading &&
    !isTextTooLong &&
    !hasInvalidMedia

  // Check if there's any data in the form to show reset button
  const hasFormData = content || mediaUrl || scheduledDate || scheduledTime || selectedPlatforms.length > 0

  const handleReset = () => {
    setContent('')
    setScheduledDate('')
    setScheduledTime('')
    setSelectedPlatforms([])
    setSelectedPageId('')
    setMediaUrl(null)
    setMediaType(null)
    setUploadError(null)
    setUploadKey(k => k + 1)
    setAiPanelKey(k => k + 1)
    setSuggestedTimesKey(k => k + 1)
    setSelectedThumbnailUrl(null)
    setMediaValidationStatus(null)
    setMediaValidationErrors([])
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
  // Show when no account is connected or when a page was disconnected,
  // but not when account is connected and user just needs to select/connect a page
  const showConnectedAccountsLink = disabledReason === 'no_account_connected' ||
    disabledReason === 'page_not_found'

  return (
    <div className={`schedule-post ${!isComposerEnabled ? 'composer-disabled' : ''}`}>
      <h2>Schedule a Post</h2>

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
              const isNotImplemented = platform.id === 'instagram' || platform.id === 'twitter' || platform.id === 'linkedin'
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
                disabled={!isComposerEnabled && connectedPages.length > 0}
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

        <div className="form-group">
          <label htmlFor="content">Post Content</label>
          <textarea
            id="content"
            value={content}
            onChange={(e) => setContent(e.target.value)}
            placeholder="What do you want to share?"
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

        <div className="form-group">
          <label>Media (optional)</label>
          <MediaUpload
            key={uploadKey}
            onUploadComplete={(s3Key, type) => {
              setMediaUrl(s3Key)
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
              onChange={(e) => setScheduledTime(e.target.value)}
              disabled={!isComposerEnabled}
            />
          </div>
        </div>

        {/* AI-powered time suggestions */}
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

        <div className="form-actions">
          <button
            type="submit"
            className="submit-btn"
            disabled={!isFormValid}
          >
            Schedule Post
          </button>
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
