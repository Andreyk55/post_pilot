import { useRef, useEffect, useCallback, useState } from 'react'
import type { Post } from '../api/posts'
import { getMediaUrl, getMediaTypeFromFile } from '../api/media'
import { VideoThumbnail } from './VideoThumbnail'
import { ConfirmDialog } from './ConfirmDialog'
import { Toast } from './Toast'
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

// Helper to get the display label for the media badge
function getMediaBadgeLabel(post: Post): string {
  // Multi-image detection: IG carousel or FB multi-photo
  if (post.mediaItems && post.mediaItems.length >= 2) {
    if (post.platform === 'Facebook') return `Photos (${post.mediaItems.length})`
    return 'Carousel'
  }
  if (post.platform === 'Instagram' && post.instagramMediaType) {
    switch (post.instagramMediaType) {
      case 'Reels': return 'Reel'
      case 'Image': return 'Image Post'
      case 'CarouselAlbum': return 'Carousel'
      case 'Video': return 'Video'
      default: return getEffectiveMediaType(post) === 'Video' ? 'Video' : 'Image'
    }
  }
  return getEffectiveMediaType(post) === 'Video' ? 'Video' : 'Image'
}

// Returns the CSS data-type value for badge coloring
function getMediaBadgeType(post: Post): string {
  if (post.mediaItems && post.mediaItems.length >= 2) return 'carouselalbum'
  if (post.instagramMediaType) return post.instagramMediaType.toLowerCase()
  return getEffectiveMediaType(post) === 'Video' ? 'video' : 'image'
}

interface ScheduledPostsProps {
  posts: Post[]
  onDelete: (id: string) => Promise<void>
  onLoadMore: () => void
  hasMore: boolean
  isLoading: boolean
  totalCount: number
}

// SVG Icons
const CalendarIcon = () => (
  <svg className="schedule-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="3" y="4" width="18" height="18" rx="2" />
    <line x1="16" y1="2" x2="16" y2="6" />
    <line x1="8" y1="2" x2="8" y2="6" />
    <line x1="3" y1="10" x2="21" y2="10" />
  </svg>
)

const ChevronDownIcon = () => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <polyline points="6 9 12 15 18 9" />
  </svg>
)

const ChevronUpIcon = () => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <polyline points="18 15 12 9 6 15" />
  </svg>
)

const TrashIcon = () => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <polyline points="3 6 5 6 21 6" />
    <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
    <line x1="10" y1="11" x2="10" y2="17" />
    <line x1="14" y1="11" x2="14" y2="17" />
  </svg>
)

const ImageIcon = () => (
  <svg className="media-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="3" y="3" width="18" height="18" rx="2" />
    <circle cx="8.5" cy="8.5" r="1.5" />
    <polyline points="21 15 16 10 5 21" />
  </svg>
)

const VideoIcon = () => (
  <svg className="media-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="2" y="4" width="20" height="16" rx="2" />
    <polygon points="10 9 15 12 10 15" fill="currentColor" stroke="none" />
  </svg>
)

const EmptyCalendarIcon = () => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
    <rect x="3" y="4" width="18" height="18" rx="2" />
    <line x1="16" y1="2" x2="16" y2="6" />
    <line x1="8" y1="2" x2="8" y2="6" />
    <line x1="3" y1="10" x2="21" y2="10" />
    <line x1="9" y1="14" x2="15" y2="14" />
  </svg>
)

const platformIcons: Record<string, string> = {
  Twitter: '𝕏',
  Instagram: 'IG',
  Facebook: 'f',
  LinkedIn: 'in',
}

export function ScheduledPosts({ posts, onDelete, onLoadMore, hasMore, isLoading, totalCount }: ScheduledPostsProps) {
  const scrollContainerRef = useRef<HTMLDivElement>(null)
  const loadMoreTriggerRef = useRef<HTMLDivElement>(null)
  const [expandedPostId, setExpandedPostId] = useState<string | null>(null)
  const [overflowingPosts, setOverflowingPosts] = useState<Set<string>>(new Set())
  const contentRefs = useRef<Map<string, HTMLParagraphElement>>(new Map())
  const [deleteTarget, setDeleteTarget] = useState<Post | null>(null)
  const [isDeleting, setIsDeleting] = useState(false)

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

  const [toastMessage, setToastMessage] = useState('')
  const [toastVisible, setToastVisible] = useState(false)

  const handleConfirmDelete = async () => {
    if (!deleteTarget) return
    setIsDeleting(true)
    try {
      await onDelete(deleteTarget.id)
      setDeleteTarget(null)
    } catch {
      setDeleteTarget(null)
      setToastMessage('Failed to remove post. Please try again.')
      setToastVisible(true)
    } finally {
      setIsDeleting(false)
    }
  }

  const getRemoveTooltip = (status: string) =>
    status === 'Failed' ? 'Delete failed post' : 'Cancel scheduled post'
  const getConfirmMessage = (status: string) =>
    status === 'Failed'
      ? "This will remove the failed post record. This can't be undone."
      : "This will cancel the schedule and remove the post. This can't be undone."
  const canRemove = (status: string) => status === 'Pending' || status === 'Failed' || status === 'RetryPending'

  if ((!posts || posts.length === 0) && !isLoading) {
    return (
      <div className="scheduled-posts">
        <div className="scheduled-posts-header">
          <h2>Scheduled</h2>
          <span className="posts-count-badge">0</span>
        </div>
        <div className="empty-state">
          <div className="empty-icon-container">
            <EmptyCalendarIcon />
          </div>
          <p className="empty-state-title">No posts scheduled</p>
          <p className="empty-state-hint">Create your first post to get started</p>
        </div>
      </div>
    )
  }

  return (
    <div className="scheduled-posts">
      <div className="scheduled-posts-header">
        <h2>Scheduled</h2>
        <span className="posts-count-badge">{totalCount}</span>
      </div>

      <div className="posts-scroll-container" ref={scrollContainerRef}>
        <div className="posts-list">
          {posts.map(post => {
            const { date, time } = formatDateTime(post.scheduledAt)
            const mediaType = getEffectiveMediaType(post)
            return (
              <div key={post.id} className="post-card">
                <div
                  className={`post-content ${expandedPostId === post.id ? 'expanded' : ''}`}
                  onClick={() => overflowingPosts.has(post.id) && toggleExpand(post.id)}
                >
                  <p ref={el => { if (el) contentRefs.current.set(post.id, el) }}>{post.content}</p>
                  {(overflowingPosts.has(post.id) || expandedPostId === post.id) && (
                    <span className="expand-indicator">
                      {expandedPostId === post.id ? (
                        <><ChevronUpIcon /> Less</>
                      ) : (
                        <><ChevronDownIcon /> More</>
                      )}
                    </span>
                  )}
                </div>

                {/* Carousel preview (multiple images) */}
                {post.mediaItems && post.mediaItems.length >= 2 && (
                  <div className="post-media-preview carousel-preview">
                    <img
                      src={getMediaUrl(post.mediaItems[0].mediaUrl) || ''}
                      alt=""
                      className="media-thumbnail"
                    />
                    <span className="media-indicator">
                      <ImageIcon />
                      {post.platform === 'Facebook'
                        ? `Photos (${post.mediaItems.length})`
                        : `Carousel (${post.mediaItems.length})`
                      }
                    </span>
                  </div>
                )}

                {/* Single image preview (not carousel) */}
                {!(post.mediaItems && post.mediaItems.length >= 2) && post.mediaUrl && mediaType === 'Image' && (
                  <div className="post-media-preview">
                    <img
                      src={getMediaUrl(post.mediaUrl) || ''}
                      alt=""
                      className="media-thumbnail"
                    />
                    <span className="media-indicator">
                      <ImageIcon />
                      {getMediaBadgeLabel(post)}
                    </span>
                  </div>
                )}

                {/* Video preview */}
                {!(post.mediaItems && post.mediaItems.length >= 2) && post.mediaUrl && mediaType === 'Video' && (
                  <div className="post-media-preview">
                    {post.selectedThumbnailUrl ? (
                      <img
                        src={post.selectedThumbnailUrl}
                        alt="Video thumbnail"
                        className="media-thumbnail"
                      />
                    ) : (
                      <VideoThumbnail
                        videoUrl={getMediaUrl(post.mediaUrl) || ''}
                        className="media-thumbnail"
                      />
                    )}
                    <span className="media-indicator">
                      <VideoIcon />
                      {getMediaBadgeLabel(post)}
                    </span>
                  </div>
                )}

                <div className="post-meta">
                  <div className="post-schedule">
                    <CalendarIcon />
                    <span className="schedule-date">{date}</span>
                    <span className="schedule-divider" />
                    <span className="schedule-time">{time}</span>
                  </div>

                  <div className="post-badges">
                    <span
                      className="platform-badge"
                      data-platform={post.platform.toLowerCase()}
                      title={post.platform}
                    >
                      {platformIcons[post.platform] || post.platform.charAt(0)}
                    </span>
                    {(post.targetPageName || post.targetInstagramAccountName) && (
                      <span className="page-badge" title={post.targetPageName || post.targetInstagramAccountName || ''}>
                        {post.targetPageName || post.targetInstagramAccountName}
                      </span>
                    )}
                    <span className="status-badge" data-status={post.status.toLowerCase()}>
                      <span className="status-dot" />
                      {post.status}
                    </span>
                    {(post.platform === 'Instagram' || (post.platform === 'Facebook' && post.mediaItems && post.mediaItems.length >= 2)) && (
                      <span className="media-type-badge" data-type={getMediaBadgeType(post)}>
                        {getMediaBadgeLabel(post)}
                      </span>
                    )}
                    {canRemove(post.status) && (
                      <button
                        className="remove-btn"
                        onClick={() => setDeleteTarget(post)}
                        title={getRemoveTooltip(post.status)}
                      >
                        <TrashIcon />
                      </button>
                    )}
                  </div>
                </div>
              </div>
            )
          })}
        </div>

        {isLoading && (
          <div className="loading-more">
            <span className="loading-spinner"></span>
            Loading...
          </div>
        )}

        {!hasMore && posts.length > 0 && (
          <div className="end-of-list">
            End of list
          </div>
        )}

        <div ref={loadMoreTriggerRef} className="load-more-trigger" />
      </div>

      <ConfirmDialog
        isOpen={deleteTarget !== null}
        title="Remove post?"
        message={deleteTarget ? getConfirmMessage(deleteTarget.status) : ''}
        confirmText="Remove"
        cancelText="Cancel"
        confirmVariant="danger"
        onConfirm={handleConfirmDelete}
        onCancel={() => setDeleteTarget(null)}
        isLoading={isDeleting}
      />

      <Toast
        message={toastMessage}
        type="error"
        isVisible={toastVisible}
        onClose={() => setToastVisible(false)}
      />
    </div>
  )
}
