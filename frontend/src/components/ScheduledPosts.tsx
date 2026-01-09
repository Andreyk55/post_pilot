import type { ScheduledPost } from '../types/post'
import './ScheduledPosts.css'

interface ScheduledPostsProps {
  posts: ScheduledPost[]
  onDelete: (id: string) => void
}

const platformIcons: Record<string, string> = {
  twitter: '𝕏',
  instagram: '📷',
  facebook: 'f',
  linkedin: 'in',
  tiktok: '♪',
}

export function ScheduledPosts({ posts, onDelete }: ScheduledPostsProps) {
  const formatDate = (date: string) => {
    return new Date(date).toLocaleDateString('en-US', {
      weekday: 'short',
      month: 'short',
      day: 'numeric',
    })
  }

  const formatTime = (time: string) => {
    const [hours, minutes] = time.split(':')
    const hour = parseInt(hours)
    const ampm = hour >= 12 ? 'PM' : 'AM'
    const displayHour = hour % 12 || 12
    return `${displayHour}:${minutes} ${ampm}`
  }

  if (posts.length === 0) {
    return (
      <div className="scheduled-posts empty">
        <h2>Scheduled Posts</h2>
        <div className="empty-state">
          <span className="empty-icon">📅</span>
          <p>No posts scheduled yet</p>
          <p className="empty-hint">Create your first scheduled post above</p>
        </div>
      </div>
    )
  }

  return (
    <div className="scheduled-posts">
      <h2>Scheduled Posts ({posts.length})</h2>

      <div className="posts-list">
        {posts.map(post => (
          <div key={post.id} className="post-card">
            <div className="post-content">
              <p>{post.content}</p>
            </div>

            <div className="post-meta">
              <div className="post-schedule">
                <span className="schedule-icon">🗓️</span>
                <span>{formatDate(post.scheduledDate)}</span>
                <span className="schedule-divider">•</span>
                <span className="schedule-icon">🕐</span>
                <span>{formatTime(post.scheduledTime)}</span>
              </div>

              <div className="post-platforms">
                {post.platforms.map(platform => (
                  <span key={platform} className="platform-badge" title={platform}>
                    {platformIcons[platform]}
                  </span>
                ))}
              </div>
            </div>

            <button
              className="delete-btn"
              onClick={() => onDelete(post.id)}
              title="Delete post"
            >
              ✕
            </button>
          </div>
        ))}
      </div>
    </div>
  )
}