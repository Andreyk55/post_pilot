import { useRef, useEffect, useCallback, useState } from 'react'
import type { Post } from '../api/posts'
import { getMediaUrl, getMediaTypeFromFile } from '../api/media'
import './ScheduledPosts.css'

// Helper to get effective media type (use mediaType if set, otherwise detect from URL)
function getEffectiveMediaType(post: Post): 'None' | 'Image' | 'Video' {
  if (post.mediaType && post.mediaType !== 'None') {
    return post.mediaType
  }
  if (post.mediaUrl) {
    return getMediaTypeFromFile(post.mediaUrl)
  }
  return 'None'
}

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
        // Check for vertical overflow (multi-line clamped text)
        if (el && el.scrollHeight > el.clientHeight) {
          newOverflowing.add(postId)
        }
      })
      setOverflowingPosts(newOverflowing)
    }

    // Small delay to ensure layout is complete
    const timer = setTimeout(checkOverflow, 50)
    window.addEventListener('resize', checkOverflow)
    return () => {
      clearTimeout(timer)
      window.removeEventListener('resize', checkOverflow)
    }
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

                {post.mediaUrl && getEffectiveMediaType(post) === 'Image' && (
                  <div className="post-media-preview">
                    <img
                      src={getMediaUrl(post.mediaUrl) || ''}
                      alt="Post attachment"
                      className="media-thumbnail"
                    />
                    <span className="media-indicator" title="This post includes an image">
                      <svg className="media-icon" viewBox="0 0 24 24" fill="currentColor">
                        <path d="M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z"/>
                      </svg>
                      Image attached
                    </span>
                  </div>
                )}

                {post.mediaUrl && getEffectiveMediaType(post) === 'Video' && (
                  <div className="post-media-preview">
                    <div className="video-placeholder">
                      <svg className="video-play-icon" width="20" height="20" viewBox="0 0 24 24" fill="currentColor">
                        <path d="M8 5v14l11-7z"/>
                      </svg>
                    </div>
                    <span className="media-indicator" title="This post includes a video">
                      <svg className="media-icon" viewBox="0 0 24 24" fill="currentColor">
                        <path d="M18 4l2 4h-3l-2-4h-2l2 4h-3l-2-4H8l2 4H7L5 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V4h-4z"/>
                      </svg>
                      Video attached · {post.mediaUrl.split('.').pop()?.toUpperCase() || 'MP4'}
                    </span>
                  </div>
                )}

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
