import { useState, useEffect, useCallback } from 'react'
import { postsApi, type Post, type CreatePostRequest, type Platform } from '../api/posts'
import type { MediaType } from '../api/media'
import { SchedulePost } from '../components/SchedulePost'
import { ScheduledPosts } from '../components/ScheduledPosts'
import { VoiceProfileModal } from '../components/VoiceProfileModal'
import { voiceProfileApi, type VoiceProfileSummary, type VoiceProfile } from '../api/voiceProfiles'
import './SchedulePostsPage.css'

const platformMap: Record<string, Platform> = {
  twitter: 'Twitter',
  instagram: 'Instagram',
  facebook: 'Facebook',
  linkedin: 'LinkedIn',
}

const PAGE_SIZE = 10

export function SchedulePostsPage() {
  const [posts, setPosts] = useState<Post[]>([])
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [currentPage, setCurrentPage] = useState(1)
  const [hasMore, setHasMore] = useState(true)
  const [totalCount, setTotalCount] = useState(0)

  // Voice Profile state
  const [voiceProfiles, setVoiceProfiles] = useState<VoiceProfileSummary[]>([])
  const [voiceProfileModalOpen, setVoiceProfileModalOpen] = useState(false)
  const [editingProfileId, setEditingProfileId] = useState<string | null>(null)
  const [loadingProfiles, setLoadingProfiles] = useState(false)

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
    setLoadingProfiles(true)
    try {
      const profiles = await voiceProfileApi.getProfiles()
      setVoiceProfiles(profiles)
    } catch (err) {
      console.error('Failed to load voice profiles:', err)
    } finally {
      setLoadingProfiles(false)
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
    mediaUrl?: string
    mediaType?: MediaType
    selectedThumbnailUrl?: string
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
          // Include media URL and type if media was uploaded
          mediaUrl: formData.mediaUrl,
          mediaType: formData.mediaType,
          // Include selected thumbnail URL for video posts
          selectedThumbnailUrl: formData.selectedThumbnailUrl,
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

  const handleProfileSaved = (profile: VoiceProfile) => {
    // Refresh the list and select the saved profile
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
        />
        {loading ? (
          <div className="loading">Loading posts...</div>
        ) : (
          <ScheduledPosts
            posts={posts}
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
