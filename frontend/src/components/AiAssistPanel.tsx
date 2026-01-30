import { useState } from 'react'
import {
  aiApi,
  aiMediaApi,
  AiError,
  type AiPlatform,
  type AiTone,
  type AiTextVariant,
  type AiPreFlightIssue,
  type AiIssueSeverity,
  type AiMediaCaptionVariant,
  type AiImageQualityIssue,
  type AiVideoFrame,
} from '../api/ai'
import { getMediaUrl, type MediaType } from '../api/media'
import { extractVideoFrames, extractSingleFrame } from '../utils/videoFrameExtractor'
import './AiAssistPanel.css'

type TabType = 'text' | 'media'

interface AiAssistPanelProps {
  text: string
  onApplyText: (text: string) => void
  onAppendText: (text: string) => void
  // Media props
  mediaUrl?: string | null
  mediaType?: MediaType | null
  onSelectThumbnail?: (thumbnailUrl: string) => void
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

type TextResult = VariantsResult | HashtagsResult | PreFlightResult

// Media Results
interface CaptionsResult {
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

type MediaResult = CaptionsResult | QualityResult | AltTextResult | ThumbnailsResult

const platformOptions: { value: AiPlatform; label: string }[] = [
  { value: 'Facebook', label: 'Facebook' },
  { value: 'Instagram', label: 'Instagram' },
  { value: 'LinkedIn', label: 'LinkedIn' },
  { value: 'X', label: 'X (Twitter)' },
]

const toneOptions: { value: AiTone; label: string }[] = [
  { value: 'Professional', label: 'Professional' },
  { value: 'Casual', label: 'Casual' },
  { value: 'Funny', label: 'Funny' },
  { value: 'Sales', label: 'Sales' },
]

export function AiAssistPanel({
  text,
  onApplyText,
  onAppendText,
  mediaUrl,
  mediaType,
  onSelectThumbnail,
}: AiAssistPanelProps) {
  const [activeTab, setActiveTab] = useState<TabType>('text')
  const [platform, setPlatform] = useState<AiPlatform>('Facebook')
  const [tone, setTone] = useState<AiTone>('Professional')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Text tab state
  const [textResult, setTextResult] = useState<TextResult | null>(null)
  const [copiedIndex, setCopiedIndex] = useState<number | null>(null)

  // Media tab state
  const [mediaResult, setMediaResult] = useState<MediaResult | null>(null)
  const [altTextCopied, setAltTextCopied] = useState(false)

  const isTextEmpty = !text.trim()
  const hasMedia = !!mediaUrl && mediaType && mediaType !== 'None'
  const isImage = mediaType === 'Image'
  const isVideo = mediaType === 'Video'

  // Text tab handlers
  const handleTextAction = async (action: () => Promise<void>) => {
    if (isTextEmpty) return

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
    if (!hasMedia) return

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
      // Show specific error message for video frame extraction failures
      if (err.message.includes('cross-origin') || err.message.includes('CORS')) {
        setError('Failed to process video due to cross-origin restrictions.')
      } else if (err.message.includes('Failed to load video')) {
        setError('Failed to load video. Please try again.')
      } else {
        setError(err.message || 'An unexpected error occurred. Please try again.')
      }
    } else {
      setError('An unexpected error occurred. Please try again.')
    }
  }

  // Text actions
  const handlePolish = () =>
    handleTextAction(async () => {
      const response = await aiApi.polish(platform, text)
      setTextResult({ type: 'variants', variants: response.variants })
    })

  const handleRewriteTone = () =>
    handleTextAction(async () => {
      const response = await aiApi.rewriteTone(platform, text, tone)
      setTextResult({ type: 'variants', variants: response.variants })
    })

  const handleShorten = () =>
    handleTextAction(async () => {
      const response = await aiApi.shorten(platform, text)
      setTextResult({ type: 'variants', variants: response.variants })
    })

  const handleExpand = () =>
    handleTextAction(async () => {
      const response = await aiApi.expand(platform, text)
      setTextResult({ type: 'variants', variants: response.variants })
    })

  const handleHashtags = () =>
    handleTextAction(async () => {
      const response = await aiApi.hashtags(platform, text)
      setTextResult({ type: 'hashtags', hashtags: response.hashtags })
    })

  const handlePreFlight = () =>
    handleTextAction(async () => {
      const response = await aiApi.preFlight(platform, text)
      setTextResult({ type: 'preflight', score: response.score, issues: response.issues })
    })

  // Media actions
  const handleImageCaptionIdeas = () =>
    handleMediaAction(async () => {
      const response = await aiMediaApi.imageCaptionIdeas(platform, mediaUrl!, text || undefined)
      setMediaResult({ type: 'captions', variants: response.variants })
    })

  const handleImageQualityCheck = () =>
    handleMediaAction(async () => {
      const response = await aiMediaApi.imageQualityCheck(mediaUrl!)
      setMediaResult({ type: 'quality', score: response.score, issues: response.issues })
    })

  const handleAltText = () =>
    handleMediaAction(async () => {
      const response = await aiMediaApi.altText(mediaUrl!)
      setMediaResult({ type: 'alttext', altText: response.altText })
    })

  const handleVideoCaptionIdeas = () =>
    handleMediaAction(async () => {
      // Convert S3 key to full URL for browser video element
      const videoUrl = getMediaUrl(mediaUrl)
      console.log('Video caption ideas - mediaUrl:', mediaUrl, 'videoUrl:', videoUrl)
      if (!videoUrl) throw new Error('Invalid media URL')

      // Extract first frame client-side (works in Lambda - no FFmpeg needed)
      const frame = await extractSingleFrame(videoUrl, 0.5)

      // Send extracted frame to backend for AI analysis
      const response = await aiMediaApi.videoCaptionIdeasWithFrame(
        platform,
        frame.dataUrl,
        text || undefined
      )
      setMediaResult({ type: 'captions', variants: response.variants })
    })

  const handleThumbnailSuggest = () =>
    handleMediaAction(async () => {
      // Convert S3 key to full URL for browser video element
      const videoUrl = getMediaUrl(mediaUrl)
      if (!videoUrl) throw new Error('Invalid media URL')

      // Extract frames client-side (works in Lambda - no FFmpeg needed)
      const extractedFrames = await extractVideoFrames(videoUrl, {
        frameCount: 6,
        width: 640,
        quality: 0.85,
      })

      // Send extracted frames to backend for storage/URL generation
      const response = await aiMediaApi.submitThumbnailFrames(
        extractedFrames.map((f) => ({
          timestampSeconds: f.timestampSeconds,
          imageData: f.dataUrl,
        }))
      )

      setMediaResult({ type: 'thumbnails', frames: response.frames, selectedIndex: null })
    })

  // Result handlers
  const handleApply = (variantText: string) => {
    onApplyText(variantText)
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
    const hashtagText = ' ' + hashtags.join(' ')
    onAppendText(hashtagText)
    setTextResult(null)
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
    if (!mediaUrl) return ''
    const parts = mediaUrl.split('/')
    return parts[parts.length - 1] || 'media'
  }

  return (
    <div className="ai-assist-panel">
      <div className="ai-assist-header">
        <h3>AI Assist</h3>
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
            className={`ai-tab ${activeTab === 'media' ? 'active' : ''}`}
            onClick={() => {
              setActiveTab('media')
              setError(null)
            }}
          >
            Media
          </button>
        </div>
      </div>

      <div className="ai-controls">
        <div className="ai-control-group">
          <label htmlFor="ai-platform">Platform</label>
          <select
            id="ai-platform"
            value={platform}
            onChange={(e) => setPlatform(e.target.value as AiPlatform)}
            disabled={loading}
          >
            {platformOptions.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>
        </div>

        {activeTab === 'text' && (
          <div className="ai-control-group">
            <label htmlFor="ai-tone">Tone (for rewrite)</label>
            <select
              id="ai-tone"
              value={tone}
              onChange={(e) => setTone(e.target.value as AiTone)}
              disabled={loading}
            >
              {toneOptions.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </select>
          </div>
        )}
      </div>

      {/* Text Tab Content */}
      {activeTab === 'text' && (
        <>
          {isTextEmpty && <div className="ai-empty-state">Enter text to enable AI features</div>}

          <div className="ai-actions">
            <button
              type="button"
              className="ai-action-btn"
              onClick={handlePolish}
              disabled={isTextEmpty || loading}
              title="Fix grammar, clarity, remove repetition"
            >
              Polish
            </button>
            <button
              type="button"
              className="ai-action-btn"
              onClick={handleRewriteTone}
              disabled={isTextEmpty || loading}
              title={`Rewrite in ${tone.toLowerCase()} tone`}
            >
              Rewrite
            </button>
            <button
              type="button"
              className="ai-action-btn"
              onClick={handleShorten}
              disabled={isTextEmpty || loading}
              title="Make text shorter"
            >
              Shorten
            </button>
            <button
              type="button"
              className="ai-action-btn"
              onClick={handleExpand}
              disabled={isTextEmpty || loading}
              title="Add more detail and CTA"
            >
              Expand
            </button>
            <button
              type="button"
              className="ai-action-btn"
              onClick={handleHashtags}
              disabled={isTextEmpty || loading}
              title="Suggest relevant hashtags"
            >
              # Hashtags
            </button>
            <button
              type="button"
              className="ai-action-btn ai-action-preflight"
              onClick={handlePreFlight}
              disabled={isTextEmpty || loading}
              title="Check post quality before publishing"
            >
              Pre-flight
            </button>
          </div>
        </>
      )}

      {/* Media Tab Content */}
      {activeTab === 'media' && (
        <>
          {!hasMedia && (
            <div className="ai-empty-state">Add media to enable media AI features</div>
          )}

          {hasMedia && (
            <div className="ai-media-info">
              <span className={`media-type-indicator ${isImage ? 'image' : 'video'}`}>
                {isImage ? 'Image' : 'Video'}
              </span>
              <span className="media-filename">{getMediaFileName()}</span>
            </div>
          )}

          <div className="ai-actions">
            {isImage && (
              <>
                <button
                  type="button"
                  className="ai-action-btn"
                  onClick={handleImageCaptionIdeas}
                  disabled={!hasMedia || loading}
                  title="Generate caption ideas based on image"
                >
                  Caption ideas
                </button>
                <button
                  type="button"
                  className="ai-action-btn"
                  onClick={handleImageQualityCheck}
                  disabled={!hasMedia || loading}
                  title="Check image quality for social media"
                >
                  Quality check
                </button>
                <button
                  type="button"
                  className="ai-action-btn"
                  onClick={handleAltText}
                  disabled={!hasMedia || loading}
                  title="Generate accessibility alt text"
                >
                  Alt text
                </button>
              </>
            )}

            {isVideo && (
              <>
                <button
                  type="button"
                  className="ai-action-btn"
                  onClick={handleVideoCaptionIdeas}
                  disabled={!hasMedia || loading}
                  title="Generate caption ideas based on video"
                >
                  Caption ideas
                </button>
                <button
                  type="button"
                  className="ai-action-btn"
                  onClick={handleThumbnailSuggest}
                  disabled={!hasMedia || loading}
                  title="Pick a thumbnail from video frames"
                >
                  Pick thumbnail
                </button>
              </>
            )}
          </div>
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

      {/* Text Results */}
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

      {activeTab === 'text' && textResult?.type === 'hashtags' && (
        <div className="ai-results">
          <h4>Suggested Hashtags</h4>
          <div className="ai-hashtags">
            {textResult.hashtags.map((hashtag, index) => (
              <span key={index} className="hashtag-chip">
                {hashtag}
              </span>
            ))}
          </div>
          <button
            type="button"
            className="hashtags-insert-btn"
            onClick={() => handleInsertHashtags(textResult.hashtags)}
          >
            Insert at end
          </button>
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
              onClick={handlePolish}
              disabled={loading}
            >
              Fix with Polish
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
  )
}
