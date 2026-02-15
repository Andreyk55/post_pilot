import { useState, useRef, useEffect } from 'react'
import { postsApi, type Post, type PostDetails, type PostStatus } from '../api/posts'
import { getMediaUrl, getMediaTypeFromFile } from '../api/media'
import { VideoThumbnail } from './VideoThumbnail'
import './PostItem.css'

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

interface PostItemProps {
  post: Post
  onCancel: (id: string) => void
  onDelete: (id: string) => void
  cachedDetails: PostDetails | undefined
  onDetailsFetched: (id: string, details: PostDetails) => void
}

const platformConfig: Record<string, { icon: string; name: string; color: string }> = {
  Twitter: { icon: '𝕏', name: 'X', color: '#000000' },
  Instagram: { icon: '', name: 'Instagram', color: '#E4405F' },
  Facebook: { icon: '', name: 'Facebook', color: '#1877F2' },
  LinkedIn: { icon: '', name: 'LinkedIn', color: '#0A66C2' },
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
    case 'Canceled':
      return { label: 'Canceled', className: 'status-canceled' }
    default:
      return { label: status, className: '' }
  }
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

const formatNumber = (num: number | null | undefined): string => {
  if (num === null || num === undefined) return '-'
  if (num >= 1000000) return `${(num / 1000000).toFixed(1)}M`
  if (num >= 1000) return `${(num / 1000).toFixed(1)}K`
  return num.toString()
}

export function PostItem({ post, onCancel, onDelete, cachedDetails, onDetailsFetched }: PostItemProps) {
  const [isExpanded, setIsExpanded] = useState(false)
  const [isContentExpanded, setIsContentExpanded] = useState(false)
  const [isLoadingDetails, setIsLoadingDetails] = useState(false)
  const [detailsError, setDetailsError] = useState<string | null>(null)
  const [details, setDetails] = useState<PostDetails | undefined>(cachedDetails)
  const contentRef = useRef<HTMLParagraphElement>(null)
  const [hasOverflow, setHasOverflow] = useState(false)

  const { date, time } = formatDateTime(post.scheduledAt)
  const platform = platformConfig[post.platform]
  const statusConfig = getStatusConfig(post.status)

  // Check if content overflows
  useEffect(() => {
    if (post.content && (post.content.length > 100 || post.content.includes('\n'))) {
      setHasOverflow(true)
    }
  }, [post.content])

  // Update details from cache if it changes
  useEffect(() => {
    if (cachedDetails) {
      setDetails(cachedDetails)
    }
  }, [cachedDetails])

  const handleExpandClick = async () => {
    const newExpanded = !isExpanded
    setIsExpanded(newExpanded)

    // Fetch details on first expand if not already cached
    if (newExpanded && !details && !isLoadingDetails) {
      setIsLoadingDetails(true)
      setDetailsError(null)

      try {
        const fetchedDetails = await postsApi.getDetails(post.id)
        setDetails(fetchedDetails)
        onDetailsFetched(post.id, fetchedDetails)
      } catch (err) {
        console.error('Failed to fetch post details:', err)
        setDetailsError('Failed to load details')
      } finally {
        setIsLoadingDetails(false)
      }
    }
  }

  const toggleContentExpand = (e: React.MouseEvent) => {
    e.stopPropagation()
    setIsContentExpanded(prev => !prev)
  }

  const hasEngagement = details?.engagement && (
    details.engagement.likesCount !== null ||
    details.engagement.commentsCount !== null ||
    details.engagement.sharesCount !== null
  )

  return (
    <div className={`post-item ${isExpanded ? 'expanded' : ''}`}>
      <div className="post-main" onClick={handleExpandClick}>
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
            {(post.platform === 'Instagram' || (post.platform === 'Facebook' && post.mediaItems && post.mediaItems.length >= 2)) && (
              <span className="media-type-badge" data-type={getMediaBadgeType(post)}>
                {getMediaBadgeLabel(post)}
              </span>
            )}
            {(post.targetPageName || post.targetInstagramAccountName) && (
              <span className="target-page">{post.targetPageName || post.targetInstagramAccountName}</span>
            )}
            <span className="post-datetime">{date} at {time}</span>
          </div>

          <div className={`post-content ${isContentExpanded ? 'content-expanded' : ''}`}>
            <p ref={contentRef}>
              {post.content || <span className="no-content">No text content</span>}
            </p>
            {(hasOverflow || isContentExpanded) && (
              <button
                className="expand-btn"
                onClick={toggleContentExpand}
              >
                {isContentExpanded ? 'Show less' : 'Show more'}
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
          {/* Carousel preview */}
          {post.mediaItems && post.mediaItems.length >= 2 && (
            <div className="post-carousel-thumbnail">
              <img
                src={getMediaUrl(post.mediaItems[0].mediaUrl) || ''}
                alt="Carousel preview"
              />
              <span className="video-badge">
                {post.platform === 'Facebook'
                  ? `Photos (${post.mediaItems.length})`
                  : `Carousel (${post.mediaItems.length})`
                }
              </span>
            </div>
          )}
          {/* Single image */}
          {!(post.mediaItems && post.mediaItems.length >= 2) && post.mediaUrl && getEffectiveMediaType(post) === 'Image' && (
            <img
              src={getMediaUrl(post.mediaUrl) || ''}
              alt="Post attachment"
            />
          )}
          {/* Video */}
          {!(post.mediaItems && post.mediaItems.length >= 2) && post.mediaUrl && getEffectiveMediaType(post) === 'Video' && (
            post.selectedThumbnailUrl ? (
              <div className="post-video-thumbnail custom-thumbnail">
                <img src={post.selectedThumbnailUrl} alt="Video thumbnail" />
                <span className="video-badge">{getMediaBadgeLabel(post)}</span>
              </div>
            ) : (
              <VideoThumbnail
                videoUrl={getMediaUrl(post.mediaUrl) || ''}
                className="post-video-thumbnail"
                badgeLabel={getMediaBadgeLabel(post)}
              />
            )
          )}
        </div>

        <div className="post-expand-indicator">
          <svg
            width="16"
            height="16"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            className={isExpanded ? 'rotated' : ''}
          >
            <polyline points="6 9 12 15 18 9"></polyline>
          </svg>
        </div>

        <div className="post-actions">
          {(post.status === 'Pending' || post.status === 'RetryPending') && (
            <button
              className="action-btn delete-btn"
              onClick={(e) => {
                e.stopPropagation()
                onCancel(post.id)
              }}
              title="Cancel scheduled post"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M3 6h18M8 6V4a2 2 0 012-2h4a2 2 0 012 2v2m3 0v14a2 2 0 01-2 2H7a2 2 0 01-2-2V6h14z"/>
              </svg>
            </button>
          )}
          {(post.status === 'Failed' || post.status === 'Canceled') && (
            <button
              className="action-btn delete-btn"
              onClick={(e) => {
                e.stopPropagation()
                onDelete(post.id)
              }}
              title={post.status === 'Failed' ? 'Delete failed post' : 'Delete canceled post'}
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M3 6h18M8 6V4a2 2 0 012-2h4a2 2 0 012 2v2m3 0v14a2 2 0 01-2 2H7a2 2 0 01-2-2V6h14z"/>
              </svg>
            </button>
          )}
        </div>
      </div>

      {/* Expandable Details Panel */}
      <div className={`post-details-panel ${isExpanded ? 'visible' : ''}`}>
        {isLoadingDetails ? (
          <div className="details-loading">
            <div className="loading-spinner small"></div>
            <span>Loading details...</span>
          </div>
        ) : detailsError ? (
          <div className="details-error">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor">
              <path d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z"/>
            </svg>
            {detailsError}
            <button onClick={handleExpandClick} className="retry-btn">Retry</button>
          </div>
        ) : details ? (
          <div className="details-content">
            {/* Engagement Section - only show for Facebook with data */}
            {post.platform === 'Facebook' && post.status === 'Published' && hasEngagement && (
              <div className="engagement-section">
                <h4 className="section-title">Engagement</h4>
                <div className="engagement-stats">
                  <div className="stat-item">
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor">
                      <path d="M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z"/>
                    </svg>
                    <span className="stat-value">{formatNumber(details.engagement?.likesCount)}</span>
                    <span className="stat-label">Reactions</span>
                  </div>
                  <div className="stat-item">
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor">
                      <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v10z"/>
                    </svg>
                    <span className="stat-value">{formatNumber(details.engagement?.commentsCount)}</span>
                    <span className="stat-label">Comments</span>
                  </div>
                  <div className="stat-item">
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                      <path d="M4 12v8a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8"/>
                      <polyline points="16 6 12 2 8 6"/>
                      <line x1="12" y1="2" x2="12" y2="15"/>
                    </svg>
                    <span className="stat-value">{formatNumber(details.engagement?.sharesCount)}</span>
                    <span className="stat-label">Shares</span>
                  </div>
                </div>
              </div>
            )}

            {/* Post Info Section */}
            <div className="info-section">
              <h4 className="section-title">Post Info</h4>
              <div className="info-grid">
                {details.publishedAt && (
                  <div className="info-item">
                    <span className="info-label">Published</span>
                    <span className="info-value">
                      {formatDateTime(details.publishedAt).date} at {formatDateTime(details.publishedAt).time}
                    </span>
                  </div>
                )}
                <div className="info-item">
                  <span className="info-label">Created</span>
                  <span className="info-value">
                    {formatDateTime(details.createdAt).date} at {formatDateTime(details.createdAt).time}
                  </span>
                </div>
                {details.externalPostUrl && (
                  <div className="info-item">
                    <span className="info-label">View on {post.platform}</span>
                    <a
                      href={details.externalPostUrl}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="external-link"
                      onClick={(e) => e.stopPropagation()}
                    >
                      Open post
                      <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                        <path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/>
                        <polyline points="15 3 21 3 21 9"/>
                        <line x1="10" y1="14" x2="21" y2="3"/>
                      </svg>
                    </a>
                  </div>
                )}
                {details.retryCount > 0 && (
                  <div className="info-item">
                    <span className="info-label">Retry attempts</span>
                    <span className="info-value">{details.retryCount}</span>
                  </div>
                )}
              </div>
            </div>
          </div>
        ) : null}
      </div>
    </div>
  )
}
