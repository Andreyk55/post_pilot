import { useState, useEffect, useCallback, useRef } from 'react'
import { postsApi, type Post, type PostDetails } from '../api/posts'
import { PostItem } from '../components/PostItem'
import { STATUS_TABS, resolveFilter, tabLabel } from './myPostsTabs'
import './MyPostsPage.css'

const PAGE_SIZE = 20

export function MyPostsPage() {
  const [posts, setPosts] = useState<Post[]>([])
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [currentPage, setCurrentPage] = useState(1)
  const [hasMore, setHasMore] = useState(true)
  const [totalCount, setTotalCount] = useState(0)
  const [activeTab, setActiveTab] = useState<string>('all')
  const scrollContainerRef = useRef<HTMLDivElement>(null)

  // Cache for post details - persists across re-renders
  const [detailsCache, setDetailsCache] = useState<Map<string, PostDetails>>(new Map())

  const loadPosts = async (tabValue: string) => {
    try {
      setLoading(true)
      const { status, statusGroup } = resolveFilter(tabValue)
      const data = await postsApi.getPaginated(1, PAGE_SIZE, status, undefined, statusGroup)
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
    loadPosts(activeTab)
  }, [activeTab])

  const loadMorePosts = useCallback(async () => {
    if (loadingMore || !hasMore) return

    try {
      setLoadingMore(true)
      const nextPage = currentPage + 1
      const { status, statusGroup } = resolveFilter(activeTab)
      const data = await postsApi.getPaginated(nextPage, PAGE_SIZE, status, undefined, statusGroup)
      setPosts(prev => [...prev, ...(data.items ?? [])])
      setCurrentPage(nextPage)
      setHasMore(data.hasNextPage)
      setTotalCount(data.totalCount)
    } catch (err) {
      console.error('Failed to load more posts:', err)
    } finally {
      setLoadingMore(false)
    }
  }, [currentPage, loadingMore, hasMore, activeTab])

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

  const handleStatusChange = (tabValue: string) => {
    setActiveTab(tabValue)
  }

  const activeTabLabel = tabLabel(activeTab)

  const handleDetailsFetched = useCallback((id: string, details: PostDetails) => {
    setDetailsCache(prev => {
      const newCache = new Map(prev)
      newCache.set(id, details)
      return newCache
    })
  }, [])

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
              className={`status-tab ${activeTab === tab.value ? 'active' : ''}`}
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
            {activeTab === 'all' ? (
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
          <h3>{activeTab === 'all' ? 'No posts yet' : `No ${activeTabLabel.toLowerCase()} posts`}</h3>
          <p>
            {activeTab === 'all'
              ? 'Create your first post to get started'
              : 'Try selecting a different filter'}
          </p>
        </div>
      ) : (
        <div className="posts-container" ref={scrollContainerRef}>
          <div className="posts-list">
            {posts.map(post => (
              <PostItem
                key={post.id}
                post={post}
                cachedDetails={detailsCache.get(post.id)}
                onDetailsFetched={handleDetailsFetched}
              />
            ))}
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
