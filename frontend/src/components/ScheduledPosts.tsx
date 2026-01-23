import { useRef, useEffect, useCallback, useState } from 'react'
import type { Post } from '../api/posts'
import './ScheduledPosts.css'

interface ScheduledPostsProps {
  posts: Post[]
  onDelete: (id: string) => void
  onLoadMore: () => void
  hasMore: boolean
  isLoading: boolean
  totalCount: number
}

const platformIcons: Record<string, string> = {
  Twitter: '𝕏',
  Instagram: '📷',
  Facebook: 'f',
  LinkedIn: 'in',
}

export function ScheduledPosts({ posts, onDelete, onLoadMore, hasMore, isLoading, totalCount }: ScheduledPostsProps) {
  const scrollContainerRef = useRef<HTMLDivElement>(null)
  const loadMoreTriggerRef = useRef<HTMLDivElement>(null)
  const [expandedPostId, setExpandedPostId] = useState<string | null>(null)
  const [overflowingPosts, setOverflowingPosts] = useState<Set<string>>(new Set())
  const contentRefs = useRef<Map<string, HTMLParagraphElement>>(new Map())

  const toggleExpand = (postId: string) => {
    setExpandedPostId(prev => prev === postId ? null : postId)
  }

  useEffect(() => {
    const checkOverflow = () => {
      const newOverflowing = new Set<string>()
      contentRefs.current.forEach((el, postId) => {
        if (el && el.scrollWidth > el.clientWidth) {
          newOverflowing.add(postId)
        }
      })
      setOverflowingPosts(newOverflowing)
    }

    checkOverflow()
    window.addEventListener('resize', checkOverflow)
    return () => window.removeEventListener('resize', checkOverflow)
  }, [posts])

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

  const handleScroll = useCallback(() => {
    if (!scrollContainerRef.current || isLoading || !hasMore) return

    const container = scrollContainerRef.current
    const scrollBottom = container.scrollHeight - container.scrollTop - container.clientHeight

    // Load more when within 100px of the bottom
    if (scrollBottom < 100) {
      onLoadMore()
    }
  }, [isLoading, hasMore, onLoadMore])

  useEffect(() => {
    const container = scrollContainerRef.current
    if (!container) return

    container.addEventListener('scroll', handleScroll)
    return () => container.removeEventListener('scroll', handleScroll)
  }, [handleScroll])

  if ((!posts || posts.length === 0) && !isLoading) {
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
      <h2>Scheduled Posts ({totalCount})</h2>

      <div className="posts-scroll-container" ref={scrollContainerRef}>
        <div className="posts-list">
          {posts.map(post => {
            const { date, time } = formatDateTime(post.scheduledAt)
            return (
              <div key={post.id} className="post-card">
                <div
                  className={`post-content ${expandedPostId === post.id ? 'expanded' : ''}`}
                  onClick={() => overflowingPosts.has(post.id) && toggleExpand(post.id)}
                >
                  <p ref={el => { if (el) contentRefs.current.set(post.id, el) }}>{post.content}</p>
                  {(overflowingPosts.has(post.id) || expandedPostId === post.id) && (
                    <span className="expand-indicator">
                      {expandedPostId === post.id ? '▲ Show less' : '▼ Show more'}
                    </span>
                  )}
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

        {isLoading && (
          <div className="loading-more">
            <span className="loading-spinner"></span>
            Loading more posts...
          </div>
        )}

        {!hasMore && posts.length > 0 && (
          <div className="end-of-list">
            No more posts to load
          </div>
        )}

        <div ref={loadMoreTriggerRef} className="load-more-trigger" />
      </div>
    </div>
  )
}
