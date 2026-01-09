import { useState } from 'react'
import './SchedulePost.css'

interface SchedulePostProps {
  onSchedule: (data: {
    content: string
    scheduledDate: string
    scheduledTime: string
    platforms: string[]
  }) => void
}

const platforms = [
  { id: 'twitter', name: 'Twitter/X', icon: '𝕏' },
  { id: 'instagram', name: 'Instagram', icon: '📷' },
  { id: 'facebook', name: 'Facebook', icon: 'f' },
  { id: 'linkedin', name: 'LinkedIn', icon: 'in' },
]

export function SchedulePost({ onSchedule }: SchedulePostProps) {
  const [content, setContent] = useState('')
  const [scheduledDate, setScheduledDate] = useState('')
  const [scheduledTime, setScheduledTime] = useState('')
  const [selectedPlatforms, setSelectedPlatforms] = useState<string[]>([])

  const togglePlatform = (platformId: string) => {
    setSelectedPlatforms(prev =>
      prev.includes(platformId)
        ? prev.filter(p => p !== platformId)
        : [...prev, platformId]
    )
  }

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault()

    if (!content || !scheduledDate || !scheduledTime || selectedPlatforms.length === 0) {
      return
    }

    onSchedule({
      content,
      scheduledDate,
      scheduledTime,
      platforms: selectedPlatforms,
    })

    // Reset form
    setContent('')
    setScheduledDate('')
    setScheduledTime('')
    setSelectedPlatforms([])
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

        <button
          type="submit"
          className="submit-btn"
          disabled={!content || !scheduledDate || !scheduledTime || selectedPlatforms.length === 0}
        >
          Schedule Post
        </button>
      </form>
    </div>
  )
}
