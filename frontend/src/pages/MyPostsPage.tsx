import { useState, useEffect, useCallback, useRef } from 'react'
import { postsApi, type Post, type PostStatus } from '../api/posts'
import { getMediaUrl } from '../api/media'
import './MyPostsPage.css'

const STATUS_TABS: { label: string; value: PostStatus | 'all'; count?: number }[] = [
  { label: 'All', value: 'all' },
  { label: 'Pending', value: 'Pending' },
  { label: 'Publishing', value: 'Publishing' },
  { label: 'Published', value: 'Published' },
  { label: 'Failed', value: 'Failed' },
  { label: 'Retry Pending', value: 'RetryPending' },
]

const platformConfig: Record<string, { icon: string; name: string; color: string }> = {
  Twitter: { icon: '𝕏', name: 'X', color: '#000000' },
  Instagram: { icon: '', name: 'Instagram', color: '#E4405F' },
  Facebook: { icon: '', name: 'Facebook', color: '#1877F2' },
  LinkedIn: { icon: '', name: 'LinkedIn', color: '#0A66C2' },
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
      posts.forEach(post => {
        // Check if content is long enough to need expansion (more than ~100 chars or has newlines)
        if (post.content && (post.content.length > 100 || post.content.includes('\n'))) {
          newOverflowing.add(post.id)
        }
      })
      setOverflowingPosts(newOverflowing)
    }

    checkOverflow()
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
        month: 'short',
        day: 'numeric',
        year: 'numeric',
      }),
      time: date.toLocaleTimeString('en-US', {
        hour: 'numeric',
        minute: '2-digit',
        hour12: true,
      }),
    }
  }

  const getStatusConfig = (status: PostStatus) => {
    switch (status) {
      case 'Pending':
        return { label: 'Scheduled', className: 'status-pending' }
      case 'Publishing':
        return { label: 'Publishing', className: 'status-publishing' }
      case 'Published':
        return { label: 'Published', className: 'status-published' }
      case 'Failed':
        return { label: 'Failed', className: 'status-failed' }
      case 'RetryPending':
        return { label: 'Retrying', className: 'status-retry' }
      default:
        return { label: status, className: '' }
    }
  }

  return (
    <div className="my-posts-page">
      <div className="page-header">
        <div>
          <h1>My Posts</h1>
          <p className="page-subtitle">Manage and track all your scheduled content</p>
        </div>
      </div>

      {error && (
        <div className="error-banner">
          <span className="error-icon">!</span>
          {error}
          <button className="error-dismiss" onClick={() => setError(null)}>×</button>
        </div>
      )}

      <div className="filters-bar">
        <div className="status-tabs">
          {STATUS_TABS.map(tab => (
            <button
              key={tab.value}
              className={`status-tab ${activeStatus === tab.value ? 'active' : ''}`}
              onClick={() => handleStatusChange(tab.value)}
            >
              {tab.label}
            </button>
          ))}
        </div>
        <div className="posts-count">
          {totalCount} {totalCount === 1 ? 'post' : 'posts'}
        </div>
      </div>

      {loading ? (
        <div className="loading-state">
          <div className="loading-spinner"></div>
          <p>Loading posts...</p>
        </div>
      ) : posts.length === 0 ? (
        <div className="empty-state">
          <div className="empty-icon-wrapper">
            {activeStatus === 'all' ? (
              <svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
                <path d="M19 3H5a2 2 0 00-2 2v14a2 2 0 002 2h14a2 2 0 002-2V5a2 2 0 00-2-2z"/>
                <path d="M8 10h8M8 14h5"/>
              </svg>
            ) : (
              <svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
                <circle cx="11" cy="11" r="8"/>
                <path d="M21 21l-4.35-4.35"/>
              </svg>
            )}
          </div>
          <h3>{activeStatus === 'all' ? 'No posts yet' : `No ${activeStatus.toLowerCase()} posts`}</h3>
          <p>
            {activeStatus === 'all'
              ? 'Create your first post to get started'
              : 'Try selecting a different filter'}
          </p>
        </div>
      ) : (
        <div className="posts-container" ref={scrollContainerRef}>
          <div className="posts-list">
            {posts.map(post => {
              const { date, time } = formatDateTime(post.scheduledAt)
              const platform = platformConfig[post.platform]
              const statusConfig = getStatusConfig(post.status)

              return (
                <div key={post.id} className="post-item">
                  <div className="post-main">
                    <div className="post-platform">
                      <div
                        className="platform-icon"
                        style={{ backgroundColor: platform?.color || '#667eea' }}
                        title={platform?.name || post.platform}
                      >
                        {platform?.icon || post.platform[0]}
                      </div>
                    </div>

                    <div className="post-body">
                      <div className="post-meta-row">
                        <span className={`status-indicator ${statusConfig.className}`}>
                          {statusConfig.label}
                        </span>
                        {post.targetPageName && (
                          <span className="target-page">{post.targetPageName}</span>
                        )}
                        <span className="post-datetime">{date} at {time}</span>
                      </div>

                      <div className={`post-content ${expandedPostId === post.id ? 'expanded' : ''}`}>
                        <p ref={el => { if (el) contentRefs.current.set(post.id, el) }}>
                          {post.content || <span className="no-content">No text content</span>}
                        </p>
                        {(overflowingPosts.has(post.id) || expandedPostId === post.id) && (
                          <button
                            className="expand-btn"
                            onClick={() => toggleExpand(post.id)}
                          >
                            {expandedPostId === post.id ? 'Show less' : 'Show more'}
                          </button>
                        )}
                      </div>

                      {post.errorMessage && (
                        <div className="error-message">
                          <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor">
                            <path d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z"/>
                          </svg>
                          {post.errorMessage}
                        </div>
                      )}
                    </div>

                    <div className={`post-media ${post.mediaUrl ? '' : 'placeholder'}`}>
                      {post.mediaUrl && (
                        <img
                          src={getMediaUrl(post.mediaUrl) || ''}
                          alt="Post attachment"
                        />
                      )}
                    </div>

                    <div className="post-actions">
                      <button
                        className="action-btn delete-btn"
                        onClick={() => handleDelete(post.id)}
                        title="Delete post"
                      >
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                          <path d="M3 6h18M8 6V4a2 2 0 012-2h4a2 2 0 012 2v2m3 0v14a2 2 0 01-2 2H7a2 2 0 01-2-2V6h14z"/>
                        </svg>
                      </button>
                    </div>
                  </div>
                </div>
              )
            })}
          </div>

          {loadingMore && (
            <div className="loading-more">
              <div className="loading-spinner small"></div>
              Loading more...
            </div>
          )}

          {!hasMore && posts.length > 0 && (
            <div className="end-of-list">
              You've reached the end
            </div>
          )}
        </div>
      )}
    </div>
  )
}
