import { useState, useCallback } from 'react'
import { aiApi, type TimeSuggestion, type AiPlatform, type AiGoal, type AudienceLocationMode } from '../api/ai'
import './SuggestedTimes.css'

interface SuggestedTimesProps {
  postText: string
  selectedDate: string // YYYY-MM-DD format
  platform: AiPlatform | null
  goal: AiGoal
  audienceLocation: AudienceLocationMode
  country?: string | null
  onAudienceLocationChange: (location: AudienceLocationMode) => void
  onCountryChange: (country: string) => void
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

// Common countries list
const countryOptions = [
  { value: 'United States', label: 'United States' },
  { value: 'United Kingdom', label: 'United Kingdom' },
  { value: 'Canada', label: 'Canada' },
  { value: 'Australia', label: 'Australia' },
  { value: 'Germany', label: 'Germany' },
  { value: 'France', label: 'France' },
  { value: 'Spain', label: 'Spain' },
  { value: 'Italy', label: 'Italy' },
  { value: 'Netherlands', label: 'Netherlands' },
  { value: 'Israel', label: 'Israel' },
  { value: 'India', label: 'India' },
  { value: 'Japan', label: 'Japan' },
  { value: 'Brazil', label: 'Brazil' },
  { value: 'Mexico', label: 'Mexico' },
  { value: 'Singapore', label: 'Singapore' },
  { value: 'UAE', label: 'United Arab Emirates' },
]

const audienceLocationOptions: { value: AudienceLocationMode; label: string }[] = [
  { value: 'MyLocation', label: 'My location' },
  { value: 'SpecificCountry', label: 'Specific country' },
  { value: 'Worldwide', label: 'Worldwide' },
]

export function SuggestedTimes({
  postText,
  selectedDate,
  platform,
  goal,
  audienceLocation,
  country,
  onAudienceLocationChange,
  onCountryChange,
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

  // Check if we can fetch suggestions
  const canFetch = Boolean(
    postText.trim().length >= 10 &&
    selectedDate &&
    platform &&
    (audienceLocation !== 'SpecificCountry' || country)
  )

  const fetchSuggestions = useCallback(async () => {
    if (!canFetch || !platform) return

    const weekday = getWeekdayFromDate(selectedDate)
    if (!weekday) return

    const timezone = getUserTimezone()

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
        country: audienceLocation === 'SpecificCountry' ? country : null,
      })
      setSuggestions(result)
    } catch (err) {
      console.error('Failed to get time suggestions:', err)
      setError('Could not get suggestions')
      setSuggestions(null)
    } finally {
      setLoading(false)
    }
  }, [canFetch, platform, selectedDate, goal, postText, audienceLocation, country])

  const handleSelectTime = (time: string) => {
    onSelectTime(time)
  }

  // Always render the component (collapsible)
  return (
    <div className={`suggested-times ${disabled ? 'disabled' : ''}`}>
      <div className="suggested-times-header" onClick={() => setExpanded(!expanded)}>
        <span className="suggested-times-icon">⏰</span>
        <span className="suggested-times-title">Suggest Best Time</span>
        {loading && <span className="suggested-times-loading">...</span>}
        <span className={`suggested-times-chevron ${expanded ? 'expanded' : ''}`}>▼</span>
      </div>

      {expanded && (
        <div className="suggested-times-content">
          {/* Audience Location Controls */}
          <div className="suggested-times-controls">
            <div className="suggested-times-control-row">
              <label htmlFor="audience-location">Audience</label>
              <select
                id="audience-location"
                value={audienceLocation}
                onChange={(e) => onAudienceLocationChange(e.target.value as AudienceLocationMode)}
                disabled={loading}
              >
                {audienceLocationOptions.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </div>

            {audienceLocation === 'SpecificCountry' && (
              <div className="suggested-times-control-row">
                <label htmlFor="audience-country">Country</label>
                <select
                  id="audience-country"
                  value={country || ''}
                  onChange={(e) => onCountryChange(e.target.value)}
                  disabled={loading}
                >
                  <option value="">Select country...</option>
                  {countryOptions.map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </select>
              </div>
            )}

            {/* Suggest Button */}
            <button
              type="button"
              className="suggested-times-fetch-btn"
              onClick={fetchSuggestions}
              disabled={!canFetch || loading}
            >
              {loading ? 'Getting suggestions...' : 'Suggest Times'}
            </button>

            {!canFetch && (
              <div className="suggested-times-hint">
                {!postText.trim() || postText.trim().length < 10
                  ? 'Enter at least 10 characters of content'
                  : !selectedDate
                  ? 'Select a date'
                  : !platform
                  ? 'Select a platform'
                  : audienceLocation === 'SpecificCountry' && !country
                  ? 'Select a country'
                  : ''}
              </div>
            )}
          </div>

          {error && <div className="suggested-times-error">{error}</div>}

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
