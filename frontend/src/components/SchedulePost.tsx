import { useState, useEffect } from 'react'
import { metaApi } from '../api/meta'
import type { MediaType } from '../api/media'
import type { ConnectedPage } from '../types/meta'
import { MediaUpload } from './MediaUpload'
import { AiAssistPanel } from './AiAssistPanel'
import { type VoiceProfileSummary } from '../api/voiceProfiles'
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
}

const platforms = [
  { id: 'twitter', name: 'Twitter/X', icon: '𝕏' },
  { id: 'instagram', name: 'Instagram', icon: '📷' },
  { id: 'facebook', name: 'Facebook', icon: 'f' },
  { id: 'linkedin', name: 'LinkedIn', icon: 'in' },
]

export function SchedulePost({ onSchedule, voiceProfiles, onVoiceProfileModalOpen }: SchedulePostProps) {
  const [content, setContent] = useState('')
  const [scheduledDate, setScheduledDate] = useState('')
  const [scheduledTime, setScheduledTime] = useState('')
  const [selectedPlatforms, setSelectedPlatforms] = useState<string[]>([])
  const [connectedPages, setConnectedPages] = useState<ConnectedPage[]>([])
  const [selectedPageId, setSelectedPageId] = useState<string>('')
  const [loadingPages, setLoadingPages] = useState(false)
  const [mediaUrl, setMediaUrl] = useState<string | null>(null)
  const [mediaType, setMediaType] = useState<MediaType | null>(null)
  const [uploadError, setUploadError] = useState<string | null>(null)
  const [uploadKey, setUploadKey] = useState(0)
  const [isUploading, setIsUploading] = useState(false)
  const [aiPanelKey, setAiPanelKey] = useState(0)
  const [selectedThumbnailUrl, setSelectedThumbnailUrl] = useState<string | null>(null)

  // Load connected Facebook Pages on mount
  useEffect(() => {
    loadConnectedPages()
  }, [])

  const loadConnectedPages = async () => {
    try {
      setLoadingPages(true)
      const response = await metaApi.getConnection()
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

  const togglePlatform = (platformId: string) => {
    setSelectedPlatforms(prev =>
      prev.includes(platformId)
        ? prev.filter(p => p !== platformId)
        : [...prev, platformId]
    )
    // Clear page selection if Facebook is deselected
    if (platformId === 'facebook' && selectedPlatforms.includes('facebook')) {
      setSelectedPageId('')
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

    // Reset form
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
  }

  // Form is valid if there's content OR media, plus date/time/platform, and not uploading
  const isFormValid = (content || mediaUrl) && scheduledDate && scheduledTime &&
    selectedPlatforms.length > 0 &&
    (!isFacebookSelected || selectedPageId) &&
    !isUploading

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
    setSelectedThumbnailUrl(null)
  }

  return (
    <div className="schedule-post">
      <h2>Schedule a Post</h2>

      <form onSubmit={handleSubmit}>
        <div className="form-group">
          <label htmlFor="content">Post Content</label>
          <textarea
            id="content"
            value={content}
            onChange={(e) => setContent(e.target.value)}
            placeholder="What do you want to share?"
            rows={4}
          />
          <span className="char-count">{content.length} characters</span>

          <AiAssistPanel
            key={aiPanelKey}
            text={content}
            onApplyText={(text) => setContent(text)}
            onAppendText={(text) => setContent((prev) => prev + text)}
            mediaUrl={mediaUrl}
            mediaType={mediaType}
            onSelectThumbnail={(url) => setSelectedThumbnailUrl(url)}
            voiceProfiles={voiceProfiles}
            onVoiceProfileModalOpen={onVoiceProfileModalOpen}
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
            }}
            onUploadingChange={setIsUploading}
          />
          {uploadError && <div className="upload-error">{uploadError}</div>}
        </div>

        <div className="form-row">
          <div className="form-group">
            <label htmlFor="date">Date</label>
            <input
              type="date"
              id="date"
              value={scheduledDate}
              onChange={(e) => setScheduledDate(e.target.value)}
            />
          </div>

          <div className="form-group">
            <label htmlFor="time">Time</label>
            <input
              type="time"
              id="time"
              value={scheduledTime}
              onChange={(e) => setScheduledTime(e.target.value)}
            />
          </div>
        </div>

        <div className="form-group">
          <label>Platforms</label>
          <div className="platforms">
            {platforms.map(platform => (
              <button
                key={platform.id}
                type="button"
                className={'platform-btn ' + (selectedPlatforms.includes(platform.id) ? 'selected' : '')}
                onClick={() => togglePlatform(platform.id)}
                title={platform.name}
              >
                <span className="platform-icon">{platform.icon}</span>
                <span className="platform-name">{platform.name}</span>
              </button>
            ))}
          </div>
        </div>

        {/* Facebook Page Selector - shown when Facebook is selected */}
        {isFacebookSelected && (
          <div className="form-group">
            <label htmlFor="facebookPage">Facebook Page</label>
            {loadingPages ? (
              <div className="loading-pages">Loading pages...</div>
            ) : connectedPages.length === 0 ? (
              <div className="no-pages-warning">
                No Facebook Pages connected. Please connect a page in{' '}
                <a href="#connected-accounts">Connected Accounts</a>.
              </div>
            ) : (
              <select
                id="facebookPage"
                value={selectedPageId}
                onChange={(e) => setSelectedPageId(e.target.value)}
                className="page-select"
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
