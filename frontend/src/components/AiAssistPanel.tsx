import { useState } from 'react'
import {
  aiApi,
  AiError,
  type AiPlatform,
  type AiTone,
  type AiTextVariant,
  type AiPreFlightIssue,
  type AiIssueSeverity,
} from '../api/ai'
import './AiAssistPanel.css'

interface AiAssistPanelProps {
  text: string
  onApplyText: (text: string) => void
  onAppendText: (text: string) => void
}

type ResultType = 'variants' | 'hashtags' | 'preflight' | null

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

type AiResult = VariantsResult | HashtagsResult | PreFlightResult

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

export function AiAssistPanel({ text, onApplyText, onAppendText }: AiAssistPanelProps) {
  const [platform, setPlatform] = useState<AiPlatform>('Facebook')
  const [tone, setTone] = useState<AiTone>('Professional')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<AiResult | null>(null)
  const [copiedIndex, setCopiedIndex] = useState<number | null>(null)

  const isTextEmpty = !text.trim()

  const handleAction = async (action: () => Promise<void>) => {
    if (isTextEmpty) return

    setLoading(true)
    setError(null)
    setResult(null)

    try {
      await action()
    } catch (err) {
      if (err instanceof AiError) {
        if (err.isRateLimited) {
          setError('You have reached your daily AI request limit. Please try again tomorrow.')
        } else if (err.isUnavailable) {
          setError('AI service is temporarily unavailable. Please try again later.')
        } else {
          setError(err.message)
        }
      } else {
        setError('An unexpected error occurred. Please try again.')
      }
    } finally {
      setLoading(false)
    }
  }

  const handlePolish = () => handleAction(async () => {
    const response = await aiApi.polish(platform, text)
    setResult({ type: 'variants', variants: response.variants })
  })

  const handleRewriteTone = () => handleAction(async () => {
    const response = await aiApi.rewriteTone(platform, text, tone)
    setResult({ type: 'variants', variants: response.variants })
  })

  const handleShorten = () => handleAction(async () => {
    const response = await aiApi.shorten(platform, text)
    setResult({ type: 'variants', variants: response.variants })
  })

  const handleExpand = () => handleAction(async () => {
    const response = await aiApi.expand(platform, text)
    setResult({ type: 'variants', variants: response.variants })
  })

  const handleHashtags = () => handleAction(async () => {
    const response = await aiApi.hashtags(platform, text)
    setResult({ type: 'hashtags', hashtags: response.hashtags })
  })

  const handlePreFlight = () => handleAction(async () => {
    const response = await aiApi.preFlight(platform, text)
    setResult({ type: 'preflight', score: response.score, issues: response.issues })
  })

  const handleApply = (variantText: string) => {
    onApplyText(variantText)
    setResult(null)
  }

  const handleCopy = async (variantText: string, index: number) => {
    await navigator.clipboard.writeText(variantText)
    setCopiedIndex(index)
    setTimeout(() => setCopiedIndex(null), 2000)
  }

  const handleInsertHashtags = (hashtags: string[]) => {
    const hashtagText = ' ' + hashtags.join(' ')
    onAppendText(hashtagText)
    setResult(null)
  }

  const getSeverityIcon = (severity: AiIssueSeverity): string => {
    switch (severity) {
      case 'Error': return '❌'
      case 'Warning': return '⚠️'
      case 'Info': return 'ℹ️'
    }
  }

  const getScoreColor = (score: number): string => {
    if (score >= 80) return 'score-good'
    if (score >= 60) return 'score-ok'
    return 'score-poor'
  }

  return (
    <div className="ai-assist-panel">
      <div className="ai-assist-header">
        <h3>AI Assist</h3>
        {isTextEmpty && <span className="hint">Enter text to enable AI features</span>}
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
      </div>

      <div className="ai-actions">
        <button
          type="button"
          className="ai-action-btn"
          onClick={handlePolish}
          disabled={isTextEmpty || loading}
          title="Fix grammar, clarity, remove repetition"
        >
          ✨ Polish
        </button>
        <button
          type="button"
          className="ai-action-btn"
          onClick={handleRewriteTone}
          disabled={isTextEmpty || loading}
          title={`Rewrite in ${tone.toLowerCase()} tone`}
        >
          🎭 Rewrite
        </button>
        <button
          type="button"
          className="ai-action-btn"
          onClick={handleShorten}
          disabled={isTextEmpty || loading}
          title="Make text shorter"
        >
          ✂️ Shorten
        </button>
        <button
          type="button"
          className="ai-action-btn"
          onClick={handleExpand}
          disabled={isTextEmpty || loading}
          title="Add more detail and CTA"
        >
          ➕ Expand
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
          ✅ Pre-flight
        </button>
      </div>

      {loading && (
        <div className="ai-loading">
          <div className="ai-spinner"></div>
          <span>AI is thinking...</span>
        </div>
      )}

      {error && (
        <div className="ai-error">
          <span className="error-icon">⚠️</span>
          <span>{error}</span>
        </div>
      )}

      {result?.type === 'variants' && (
        <div className="ai-results">
          <h4>Suggestions</h4>
          <div className="ai-variants">
            {result.variants.map((variant, index) => (
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

      {result?.type === 'hashtags' && (
        <div className="ai-results">
          <h4>Suggested Hashtags</h4>
          <div className="ai-hashtags">
            {result.hashtags.map((hashtag, index) => (
              <span key={index} className="hashtag-chip">
                {hashtag}
              </span>
            ))}
          </div>
          <button
            type="button"
            className="hashtags-insert-btn"
            onClick={() => handleInsertHashtags(result.hashtags)}
          >
            Insert at end
          </button>
        </div>
      )}

      {result?.type === 'preflight' && (
        <div className="ai-results">
          <h4>Pre-flight Check</h4>
          <div className={`preflight-score ${getScoreColor(result.score)}`}>
            <span className="score-value">{result.score}</span>
            <span className="score-label">/ 100</span>
          </div>
          <div className="preflight-issues">
            {result.issues.map((issue, index) => (
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
          {result.score < 70 && (
            <button
              type="button"
              className="fix-with-polish-btn"
              onClick={handlePolish}
              disabled={loading}
            >
              ✨ Fix with Polish
            </button>
          )}
        </div>
      )}
    </div>
  )
}
