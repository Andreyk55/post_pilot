const API_URL = 'http://localhost:5122/api'

export type Platform = 'Twitter' | 'Instagram' | 'Facebook' | 'LinkedIn'

export type PostStatus = 'Pending' | 'Publishing' | 'Published' | 'Failed' | 'RetryPending'

export interface Post {
  id: string
  content: string
  mediaUrl: string | null
  platform: Platform
  scheduledAt: string
  status: PostStatus
  createdAt: string
  updatedAt: string
  targetPageId: string | null
  targetPageName: string | null
  publishedAt: string | null
  externalPostId: string | null
  errorMessage: string | null
  retryCount: number
}

export interface CreatePostRequest {
  content: string
  mediaUrl?: string | null
  platform: Platform
  scheduledAt: string
  targetPageId?: string | null
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

  async delete(id: string): Promise<void> {
    const response = await fetch(`${API_URL}/posts/${id}`, {
      method: 'DELETE',
    })
    if (!response.ok) throw new Error('Failed to delete post')
  },
}
