import type { Post } from '../api/posts'
import './ScheduledPosts.css'

interface ScheduledPostsProps {
  posts: Post[]
  onDelete: (id: string) => void
}

const platformIcons: Record<string, string> = {
  Twitter: '𝕏',
  Instagram: '📷',
  Facebook: 'f',
  LinkedIn: 'in',
}

export function ScheduledPosts({ posts, onDelete }: ScheduledPostsProps) {
  const formatDateTime = (isoString: string) => {
    const date = new Date(isoString)
    return {
      date: date.toLocaleDateString('en-US', {
        weekday: 'short',
        month: 'short',
        day: 'numeric',
      }),
      time: date.toLocaleTimeString('en-US', {
        hour: 'numeric',
        minute: '2-digit',
        hour12: true,
      }),
    }
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
        {posts.map(post => {
          const { date, time } = formatDateTime(post.scheduledAt)
          return (
            <div key={post.id} className="post-card">
              <div className="post-content">
                <p>{post.content}</p>
              </div>

              <div className="post-meta">
                <div className="post-schedule">
                  <span className="schedule-icon">🗓️</span>
                  <span>{date}</span>
                  <span className="schedule-divider">•</span>
                  <span className="schedule-icon">🕐</span>
                  <span>{time}</span>
                </div>

                <div className="post-platforms">
                  <span className="platform-badge" title={post.platform}>
                    {platformIcons[post.platform] || post.platform}
                  </span>
                  {post.targetPageName && (
                    <span className="page-badge" title={`Posting to: ${post.targetPageName}`}>
                      {post.targetPageName}
                    </span>
                  )}
                  <span className="status-badge" data-status={post.status.toLowerCase()}>
                    {post.status}
                  </span>
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
          )
        })}
      </div>
    </div>
  )
}
