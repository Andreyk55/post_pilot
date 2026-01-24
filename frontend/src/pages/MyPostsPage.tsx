import { useState, useEffect, useCallback, useRef } from 'react'
import { postsApi, type Post, type PostStatus } from '../api/posts'
import { getMediaUrl } from '../api/media'
import './MyPostsPage.css'

const STATUS_TABS: { label: string; value: PostStatus | 'all' }[] = [
  { label: 'All', value: 'all' },
  { label: 'Pending', value: 'Pending' },
  { label: 'Publishing', value: 'Publishing' },
  { label: 'Published', value: 'Published' },
  { label: 'Failed', value: 'Failed' },
  { label: 'Retry Pending', value: 'RetryPending' },
]

const platformIcons: Record<string, string> = {
  Twitter: '𝕏',
  Instagram: '📷',
  Facebook: 'f',
  LinkedIn: 'in',
}

const PAGE_SIZE = 20

export function MyPostsPage() {
  const [posts, setPosts] = useState<Post[]>([])
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [currentPage, setCurrentPage] = useState(1)
  const [hasMore, setHasMore] = useState(true)
  const [totalCount, setTotalCount] = useState(0)
  const [activeStatus, setActiveStatus] = useState<PostStatus | 'all'>('all')
  const [expandedPostId, setExpandedPostId] = useState<string | null>(null)
  const [overflowingPosts, setOverflowingPosts] = useState<Set<string>>(new Set())
  const contentRefs = useRef<Map<string, HTMLParagraphElement>>(new Map())
  const scrollContainerRef = useRef<HTMLDivElement>(null)

  const loadPosts = async (status: PostStatus | 'all') => {
    try {
      setLoading(true)
      const statusFilter = status === 'all' ? undefined : status
      const data = await postsApi.getPaginated(1, PAGE_SIZE, statusFilter)
      setPosts(data.items ?? [])
      setCurrentPage(1)
      setHasMore(data.hasNextPage)
      setTotalCount(data.totalCount)
      setError(null)
    } catch (err) {
      setError('Failed to load posts')
      console.error(err)
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    loadPosts(activeStatus)
  }, [activeStatus])

  useEffect(() => {
    const checkOverflow = () => {
      const newOverflowing = new Set<string>()
      contentRefs.current.forEach((el, postId) => {
        if (el && el.scrollHeight > el.clientHeight) {
          newOverflowing.add(postId)
        }
      })
      setOverflowingPosts(newOverflowing)
    }

    checkOverflow()
    window.addEventListener('resize', checkOverflow)
    return () => window.removeEventListener('resize', checkOverflow)
  }, [posts])

  const loadMorePosts = useCallback(async () => {
    if (loadingMore || !hasMore) return

    try {
      setLoadingMore(true)
      const nextPage = currentPage + 1
      const statusFilter = activeStatus === 'all' ? undefined : activeStatus
      const data = await postsApi.getPaginated(nextPage, PAGE_SIZE, statusFilter)
      setPosts(prev => [...prev, ...(data.items ?? [])])
      setCurrentPage(nextPage)
      setHasMore(data.hasNextPage)
      setTotalCount(data.totalCount)
    } catch (err) {
      console.error('Failed to load more posts:', err)
    } finally {
      setLoadingMore(false)
    }
  }, [currentPage, loadingMore, hasMore, activeStatus])

  const handleScroll = useCallback(() => {
    if (!scrollContainerRef.current || loadingMore || !hasMore) return

    const container = scrollContainerRef.current
    const scrollBottom = container.scrollHeight - container.scrollTop - container.clientHeight

    if (scrollBottom < 100) {
      loadMorePosts()
    }
  }, [loadingMore, hasMore, loadMorePosts])

  useEffect(() => {
    const container = scrollContainerRef.current
    if (!container) return

    container.addEventListener('scroll', handleScroll)
    return () => container.removeEventListener('scroll', handleScroll)
  }, [handleScroll])

  const handleDelete = async (id: string) => {
    try {
      await postsApi.delete(id)
      setPosts(prev => prev.filter(post => post.id !== id))
      setTotalCount(prev => prev - 1)
      setError(null)
    } catch (err) {
      setError('Failed to delete post')
      console.error(err)
    }
  }

  const handleStatusChange = (status: PostStatus | 'all') => {
    setActiveStatus(status)
    setExpandedPostId(null)
  }

  const toggleExpand = (postId: string) => {
    setExpandedPostId(prev => prev === postId ? null : postId)
  }

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

  return (
    <div className="my-posts-page">
      <h1>My Posts</h1>
      <p className="page-subtitle">View and manage all your posts</p>

      {error && <div className="error-message">{error}</div>}

      <div className="status-tabs">
        {STATUS_TABS.map(tab => (
          <button
            key={tab.value}
            className={`status-tab ${activeStatus === tab.value ? 'active' : ''}`}
            onClick={() => handleStatusChange(tab.value)}
            data-status={tab.value.toLowerCase()}
          >
            {tab.label}
          </button>
        ))}
      </div>

      <div className="posts-count">
        {totalCount} {totalCount === 1 ? 'post' : 'posts'}
      </div>

      {loading ? (
        <div className="loading">Loading posts...</div>
      ) : posts.length === 0 ? (
        <div className="empty-state">
          <span className="empty-icon">{activeStatus === 'all' ? '📝' : '🔍'}</span>
          <p>{activeStatus === 'all' ? 'No posts yet' : `No ${activeStatus.toLowerCase()} posts`}</p>
          <p className="empty-hint">
            {activeStatus === 'all'
              ? 'Schedule your first post to get started'
              : 'Try selecting a different status filter'}
          </p>
        </div>
      ) : (
        <div className="posts-scroll-container" ref={scrollContainerRef}>
          <div className="posts-grid">
            {posts.map(post => {
              const { date, time } = formatDateTime(post.scheduledAt)
              return (
                <div key={post.id} className="post-card">
                  <div className="post-header">
                    <span className="platform-badge" title={post.platform}>
                      {platformIcons[post.platform] || post.platform}
                    </span>
                    <span className="status-badge" data-status={post.status.toLowerCase()}>
                      {post.status}
                    </span>
                    <button
                      className="delete-btn"
                      onClick={() => handleDelete(post.id)}
                      title="Delete post"
                    >
                      ✕
                    </button>
                  </div>

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

                  {post.mediaUrl && (
                    <div className="post-media-preview">
                      <img
                        src={getMediaUrl(post.mediaUrl) || ''}
                        alt="Post attachment"
                        className="media-thumbnail"
                      />
                    </div>
                  )}

                  <div className="post-footer">
                    <div className="post-schedule">
                      <span className="schedule-icon">🗓️</span>
                      <span>{date}</span>
                      <span className="schedule-divider">•</span>
                      <span className="schedule-icon">🕐</span>
                      <span>{time}</span>
                    </div>
                    {post.targetPageName && (
                      <span className="page-badge" title={`Posting to: ${post.targetPageName}`}>
                        {post.targetPageName}
                      </span>
                    )}
                  </div>

                  {post.errorMessage && (
                    <div className="post-error">
                      <span className="error-icon">⚠️</span>
                      {post.errorMessage}
                    </div>
                  )}
                </div>
              )
            })}
          </div>

          {loadingMore && (
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
        </div>
      )}
    </div>
  )
}
