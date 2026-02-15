import { useState, useEffect, useCallback } from 'react'
import { postsApi, type Post, type CreatePostRequest, type CreatePostMediaItem, type Platform } from '../api/posts'
import type { MediaType } from '../api/media'
import { SchedulePost } from '../components/SchedulePost'
import { ScheduledPosts } from '../components/ScheduledPosts'
import { VoiceProfileModal } from '../components/VoiceProfileModal'
import { voiceProfileApi, type VoiceProfileSummary } from '../api/voiceProfiles'
import './SchedulePostsPage.css'

const platformMap: Record<string, Platform> = {
  twitter: 'Twitter',
  instagram: 'Instagram',
  facebook: 'Facebook',
  linkedin: 'LinkedIn',
}

const PAGE_SIZE = 10

interface SchedulePostsPageProps {
  /** Optional callback for navigating to other pages */
  onNavigate?: (page: string) => void
}

export function SchedulePostsPage({ onNavigate }: SchedulePostsPageProps) {
  const [posts, setPosts] = useState<Post[]>([])
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [currentPage, setCurrentPage] = useState(1)
  const [hasMore, setHasMore] = useState(true)
  const [totalCount, setTotalCount] = useState(0)

  const [voiceProfiles, setVoiceProfiles] = useState<VoiceProfileSummary[]>([])
  const [voiceProfileModalOpen, setVoiceProfileModalOpen] = useState(false)
  const [editingProfileId, setEditingProfileId] = useState<string | null>(null)

  useEffect(() => {
    loadPosts()
    loadVoiceProfiles()
  }, [])

  const loadPosts = async () => {
    try {
      setLoading(true)
      const data = await postsApi.getPaginated(1, PAGE_SIZE)
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

  const loadVoiceProfiles = async () => {
    try {
      const profiles = await voiceProfileApi.getProfiles()
      setVoiceProfiles(profiles)
    } catch (err) {
      console.error('Failed to load voice profiles:', err)
    }
  }

  const loadMorePosts = useCallback(async () => {
    if (loadingMore || !hasMore) return

    try {
      setLoadingMore(true)
      const nextPage = currentPage + 1
      const data = await postsApi.getPaginated(nextPage, PAGE_SIZE)
      setPosts(prev => [...prev, ...(data.items ?? [])])
      setCurrentPage(nextPage)
      setHasMore(data.hasNextPage)
      setTotalCount(data.totalCount)
    } catch (err) {
      console.error('Failed to load more posts:', err)
    } finally {
      setLoadingMore(false)
    }
  }, [currentPage, loadingMore, hasMore])

  const handleSchedule = async (formData: {
    content: string
    scheduledDate: string
    scheduledTime: string
    platforms: string[]
    targetPageId?: string
    targetInstagramAccountId?: string
    mediaUrl?: string
    mediaType?: MediaType
    selectedThumbnailUrl?: string
    mediaItems?: CreatePostMediaItem[]
  }) => {
    try {
      const scheduledAt = new Date(`${formData.scheduledDate}T${formData.scheduledTime}`).toISOString()

      // Create a post for each selected platform
      const newPosts: Post[] = []
      for (const platformId of formData.platforms) {
        const platform = platformMap[platformId]
        if (!platform) continue

        const request: CreatePostRequest = {
          content: formData.content,
          platform,
          scheduledAt,
          // Include targetPageId for Facebook posts
          targetPageId: platform === 'Facebook' ? formData.targetPageId : undefined,
          // Include targetInstagramAccountId for Instagram posts
          targetInstagramAccountId: platform === 'Instagram' ? formData.targetInstagramAccountId : undefined,
          // Include media URL and type if media was uploaded
          mediaUrl: formData.mediaUrl,
          mediaType: formData.mediaType,
          // Include selected thumbnail URL for video posts
          selectedThumbnailUrl: formData.selectedThumbnailUrl,
          // Include media items for multi-image posts (Instagram carousel, Facebook multi-photo)
          mediaItems: (platform === 'Instagram' || platform === 'Facebook') ? formData.mediaItems : undefined,
        }
        const post = await postsApi.create(request)
        newPosts.push(post)
      }

      setPosts(prev => [...newPosts, ...prev])
      setTotalCount(prev => prev + newPosts.length)
      setError(null)
    } catch (err) {
      setError('Failed to schedule post')
      console.error(err)
    }
  }

  const handleCancel = async (id: string) => {
    await postsApi.cancel(id)
    setPosts(prev => prev.map(post =>
      post.id === id ? { ...post, status: 'Canceled' as const } : post
    ))
    setError(null)
  }

  const handleDelete = async (id: string) => {
    await postsApi.delete(id)
    setPosts(prev => prev.filter(post => post.id !== id))
    setTotalCount(prev => prev - 1)
    setError(null)
  }

  const handleProfileSaved = () => {
    // Refresh the list
    loadVoiceProfiles()
  }

  const handleProfileDeleted = () => {
    // Refresh the list
    loadVoiceProfiles()
  }

  const handleVoiceProfileModalOpen = (profileId?: string | null) => {
    setEditingProfileId(profileId || null)
    setVoiceProfileModalOpen(true)
  }

  return (
    <div className="schedule-posts-page">
      <h1>Schedule Posts</h1>
      <p className="page-subtitle">Plan and schedule your social media content</p>

      {error && <div className="error-message">{error}</div>}

      <div className="schedule-content">
        <SchedulePost
          onSchedule={handleSchedule}
          voiceProfiles={voiceProfiles}
          onVoiceProfileModalOpen={handleVoiceProfileModalOpen}
          onNavigate={onNavigate}
        />
        {loading ? (
          <div className="loading">Loading posts...</div>
        ) : (
          <ScheduledPosts
            posts={posts}
            onCancel={handleCancel}
            onDelete={handleDelete}
            onLoadMore={loadMorePosts}
            hasMore={hasMore}
            isLoading={loadingMore}
            totalCount={totalCount}
          />
        )}
      </div>

      <VoiceProfileModal
        isOpen={voiceProfileModalOpen}
        onClose={() => setVoiceProfileModalOpen(false)}
        profileId={editingProfileId}
        onSaved={handleProfileSaved}
        onDeleted={handleProfileDeleted}
      />
    </div>
  )
}
