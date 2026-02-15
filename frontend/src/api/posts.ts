import type { MediaType } from './media'

const API_URL = 'http://localhost:5122/api'

export type Platform = 'Twitter' | 'Instagram' | 'Facebook' | 'LinkedIn'

export type PostStatus = 'Pending' | 'Publishing' | 'Published' | 'Failed' | 'RetryPending' | 'Canceled'

export interface PostMediaItem {
  id: string
  order: number
  mediaUrl: string
  mediaType: MediaType
}

export interface CreatePostMediaItem {
  mediaUrl: string
  mediaType: MediaType
  order: number
}

export interface Post {
  id: string
  content: string
  mediaUrl: string | null
  mediaType: MediaType
  platform: Platform
  scheduledAt: string
  status: PostStatus
  createdAt: string
  updatedAt: string
  targetPageId: string | null
  targetPageName: string | null
  targetInstagramAccountId: string | null
  targetInstagramAccountName: string | null
  publishedAt: string | null
  externalPostId: string | null
  externalPostUrl: string | null
  errorMessage: string | null
  retryCount: number
  selectedThumbnailUrl: string | null
  instagramMediaType: string | null
  mediaItems: PostMediaItem[] | null
}

export interface CreatePostRequest {
  content: string
  mediaUrl?: string | null
  mediaType?: MediaType | null
  platform: Platform
  scheduledAt: string
  targetPageId?: string | null
  targetInstagramAccountId?: string | null
  selectedThumbnailUrl?: string | null
  mediaItems?: CreatePostMediaItem[] | null
}

export interface PaginatedResponse<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
  hasNextPage: boolean
  hasPreviousPage: boolean
}

export interface PostEngagement {
  likesCount: number | null
  commentsCount: number | null
  sharesCount: number | null
}

export interface PostDetails {
  id: string
  content: string
  mediaUrl: string | null
  mediaType: string
  platform: Platform
  scheduledAt: string
  status: PostStatus
  createdAt: string
  updatedAt: string
  targetPageId: string | null
  targetPageName: string | null
  targetInstagramAccountId: string | null
  targetInstagramAccountName: string | null
  publishedAt: string | null
  externalPostId: string | null
  errorMessage: string | null
  retryCount: number
  engagement: PostEngagement | null
  externalPostUrl: string | null
  instagramMediaType: string | null
  mediaItems: PostMediaItem[] | null
}

export const postsApi = {
  async getAll(): Promise<Post[]> {
    const response = await fetch(`${API_URL}/posts?pageSize=1000`)
    if (!response.ok) throw new Error('Failed to fetch posts')
    const data: PaginatedResponse<Post> = await response.json()
    return data.items
  },

  async getPaginated(page: number = 1, pageSize: number = 10, status?: PostStatus): Promise<PaginatedResponse<Post>> {
    const params = new URLSearchParams({
      page: page.toString(),
      pageSize: pageSize.toString(),
    })
    if (status) {
      params.append('status', status)
    }
    const response = await fetch(`${API_URL}/posts?${params}`)
    if (!response.ok) throw new Error('Failed to fetch posts')
    return response.json()
  },

  async create(post: CreatePostRequest): Promise<Post> {
    const response = await fetch(`${API_URL}/posts`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(post),
    })
    if (!response.ok) throw new Error('Failed to create post')
    return response.json()
  },

  async cancel(id: string): Promise<void> {
    const response = await fetch(`${API_URL}/posts/${id}/cancel`, {
      method: 'POST',
    })
    if (!response.ok) {
      if (response.status === 409) {
        const body = await response.json().catch(() => null)
        throw new Error(body?.detail || 'This post can no longer be canceled because its status has changed.')
      }
      throw new Error('Failed to cancel post')
    }
  },

  async delete(id: string): Promise<void> {
    const response = await fetch(`${API_URL}/posts/${id}`, {
      method: 'DELETE',
    })
    if (!response.ok) {
      if (response.status === 409) {
        const body = await response.json().catch(() => null)
        throw new Error(body?.detail || 'This post can no longer be deleted because its status has changed.')
      }
      throw new Error('Failed to delete post')
    }
  },

  async getDetails(id: string): Promise<PostDetails> {
    const response = await fetch(`${API_URL}/posts/${id}/details`)
    if (!response.ok) throw new Error('Failed to fetch post details')
    return response.json()
  },
}
