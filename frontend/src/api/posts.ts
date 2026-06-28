import type { MediaType } from './media'
import { config } from '../config/appConfig'

const API_URL = config.apiBaseUrl

async function readErrorMessage(response: Response, fallback: string): Promise<string> {
  const contentType = response.headers.get('content-type') ?? ''

  if (contentType.includes('json')) {
    const body = await response.json().catch(() => null)
    return body?.detail || body?.error || body?.title || fallback
  }

  const text = await response.text().catch(() => '')
  return text.trim() || fallback
}

export type Platform = 'Twitter' | 'Instagram' | 'Facebook' | 'LinkedIn'

export type PostStatus = 'Scheduled' | 'Publishing' | 'Published' | 'Failed' | 'RetryPending' | 'Canceled' | 'Processing'

/** Server-side status group: collapses several backend statuses into one filter. */
export type PostStatusGroup = 'inProgress'

export type PostType = 'Feed' | 'Story'

export interface MediaThumbnailDto {
  storageKey: string | null
  url: string | null
  mimeType: string | null
  width: number | null
  height: number | null
  sizeBytes: number | null
  createdAtUtc: string | null
}

export interface PostMediaItem {
  id: string
  order: number
  mediaUrl: string
  mediaType: MediaType
  thumbnail?: MediaThumbnailDto | null
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
  postType: PostType
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
  profileUrl: string | null
  errorMessage: string | null
  retryCount: number
  processingPollCount: number
  nextRetryAt: string | null
  selectedThumbnailUrl: string | null
  instagramMediaType: string | null
  thumbnail?: MediaThumbnailDto | null
  mediaItems: PostMediaItem[] | null
}

export interface InstagramUserTag {
  username: string
  x: number
  y: number
}

export interface CreatePostRequest {
  content?: string
  mediaUrl?: string | null
  mediaType?: MediaType | null
  postType?: PostType
  platform: Platform
  scheduledAt: string
  targetPageId?: string | null
  targetInstagramAccountId?: string | null
  selectedThumbnailUrl?: string | null
  mediaItems?: CreatePostMediaItem[] | null
  instagramUserTags?: InstagramUserTag[] | null
  /** Per-media-item tags for carousel posts. Key = media item order (0-based). */
  instagramMediaTags?: Record<number, InstagramUserTag[]> | null
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
  postType: string
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
  processingPollCount: number
  nextRetryAt: string | null
  engagement: PostEngagement | null
  externalPostUrl: string | null
  profileUrl: string | null
  pageUrl: string | null
  instagramMediaType: string | null
  thumbnail?: MediaThumbnailDto | null
  mediaItems: PostMediaItem[] | null
}

export const postsApi = {
  async getAll(): Promise<Post[]> {
    const response = await fetch(`${API_URL}/posts?pageSize=1000`)
    if (!response.ok) throw new Error('Failed to fetch posts')
    const data: PaginatedResponse<Post> = await response.json()
    return data.items
  },

  async getPaginated(
    page: number = 1,
    pageSize: number = 10,
    status?: PostStatus,
    postType?: PostType,
    statusGroup?: PostStatusGroup,
  ): Promise<PaginatedResponse<Post>> {
    const params = new URLSearchParams({
      page: page.toString(),
      pageSize: pageSize.toString(),
    })
    // statusGroup takes precedence over a single status when both are provided.
    if (statusGroup) {
      params.append('statusGroup', statusGroup)
    } else if (status) {
      params.append('status', status)
    }
    if (postType) {
      params.append('postType', postType)
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
    if (!response.ok) {
      throw new Error(await readErrorMessage(response, 'Failed to create post'))
    }
    return response.json()
  },

  async publishNow(id: string): Promise<Post> {
    const response = await fetch(`${API_URL}/posts/${id}/publish-now`, {
      method: 'POST',
    })
    if (!response.ok) {
      if (response.status === 409) {
        throw new Error(await readErrorMessage(response, 'This post can no longer be published because its status has changed.'))
      }
      if (response.status === 502) {
        throw new Error(await readErrorMessage(response, 'Publishing to the platform failed. Please try again.'))
      }
      throw new Error(await readErrorMessage(response, 'Failed to publish post'))
    }
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
