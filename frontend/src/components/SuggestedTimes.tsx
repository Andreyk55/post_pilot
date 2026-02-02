import { useState, useEffect, useCallback, useRef } from 'react'
import { aiApi, type TimeSuggestion, type AiPlatform, type AiGoal, type AudienceLocationMode } from '../api/ai'
import './SuggestedTimes.css'

interface SuggestedTimesProps {
  postText: string
  selectedDate: string // YYYY-MM-DD format
  platform: AiPlatform | null
  goal: AiGoal
  audienceLocation: AudienceLocationMode
  country?: string | null
  onSelectTime: (time: string) => void
  disabled?: boolean
}

// Get weekday name from date string
function getWeekdayFromDate(dateStr: string): string {
  if (!dateStr) return ''
  const date = new Date(dateStr + 'T00:00:00')
  return date.toLocaleDateString('en-US', { weekday: 'long' })
}

// Get user's timezone
function getUserTimezone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone
}

export function SuggestedTimes({
  postText,
  selectedDate,
  platform,
  goal,
  audienceLocation,
  country,
  onSelectTime,
  disabled = false,
}: SuggestedTimesProps) {
  const [suggestions, setSuggestions] = useState<{
    primary: TimeSuggestion
    alternatives: TimeSuggestion[]
  } | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [expanded, setExpanded] = useState(false)

  // Track the last request parameters to avoid duplicate calls
  const lastRequestRef = useRef<string>('')

  const fetchSuggestions = useCallback(async () => {
    if (!postText.trim() || !selectedDate || !platform) {
      setSuggestions(null)
      return
    }

    const weekday = getWeekdayFromDate(selectedDate)
    if (!weekday) return

    const timezone = getUserTimezone()

    // Create a key for this request to avoid duplicates
    const requestKey = `${platform}:${goal}:${weekday}:${audienceLocation}:${country || ''}:${postText.slice(0, 100)}`
    if (requestKey === lastRequestRef.current && suggestions) {
      return // Already have suggestions for this request
    }

    lastRequestRef.current = requestKey
    setLoading(true)
    setError(null)

    try {
      const result = await aiApi.suggestPostTime({
        platform,
        goal,
        postText,
        weekday,
        timezone,
        audienceLocation,
        country,
      })
      setSuggestions(result)
    } catch (err) {
      console.error('Failed to get time suggestions:', err)
      setError('Could not get suggestions')
      setSuggestions(null)
    } finally {
      setLoading(false)
    }
  }, [postText, selectedDate, platform, goal, audienceLocation, country, suggestions])

  // Auto-fetch when requirements are met and user has entered content
  useEffect(() => {
    // Only fetch if we have minimum requirements
    if (postText.trim().length >= 10 && selectedDate && platform) {
      // Debounce the fetch
      const timer = setTimeout(() => {
        fetchSuggestions()
      }, 500)
      return () => clearTimeout(timer)
    }
  }, [postText, selectedDate, platform, goal, audienceLocation, country, fetchSuggestions])

  const handleSelectTime = (time: string) => {
    onSelectTime(time)
  }

  // Don't render if missing required inputs
  if (!platform || !selectedDate) {
    return null
  }

  // Don't render if content is too short
  if (postText.trim().length < 10) {
    return null
  }

  return (
    <div className={`suggested-times ${disabled ? 'disabled' : ''}`}>
      <div className="suggested-times-header" onClick={() => setExpanded(!expanded)}>
        <span className="suggested-times-icon">⏰</span>
        <span className="suggested-times-title">Suggested Times</span>
        {loading && <span className="suggested-times-loading">...</span>}
        <span className={`suggested-times-chevron ${expanded ? 'expanded' : ''}`}>▼</span>
      </div>

      {expanded && (
        <div className="suggested-times-content">
          {error && <div className="suggested-times-error">{error}</div>}

          {!suggestions && !loading && !error && (
            <div className="suggested-times-empty">
              Enter more content to get AI-powered time suggestions
            </div>
          )}

          {suggestions && (
            <>
              {/* Primary recommendation */}
              <div
                className="suggested-time-card primary"
                onClick={() => !disabled && handleSelectTime(suggestions.primary.time)}
              >
                <div className="suggested-time-header">
                  <span className="suggested-time-badge">Recommended</span>
                  <span className="suggested-time-confidence">
                    {suggestions.primary.confidence}% confidence
                  </span>
                </div>
                <div className="suggested-time-main">
                  <span className="suggested-time-value">{suggestions.primary.time}</span>
                  <span className="suggested-time-label">{suggestions.primary.label}</span>
                </div>
                <div className="suggested-time-reason">{suggestions.primary.reason}</div>
              </div>

              {/* Alternatives */}
              {suggestions.alternatives.length > 0 && (
                <div className="suggested-times-alternatives">
                  <div className="suggested-times-alt-label">Alternatives</div>
                  <div className="suggested-times-alt-list">
                    {suggestions.alternatives.map((alt, index) => (
                      <div
                        key={index}
                        className="suggested-time-card alternative"
                        onClick={() => !disabled && handleSelectTime(alt.time)}
                      >
                        <div className="suggested-time-main">
                          <span className="suggested-time-value">{alt.time}</span>
                          <span className="suggested-time-label">{alt.label}</span>
                        </div>
                        <div className="suggested-time-meta">
                          <span className="suggested-time-confidence-small">
                            {alt.confidence}%
                          </span>
                          <span className="suggested-time-reason-short">{alt.reason}</span>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </>
          )}
        </div>
      )}
    </div>
  )
}
