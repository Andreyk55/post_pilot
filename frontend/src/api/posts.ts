const API_URL = 'http://localhost:5122/api'

export type Platform = 'Twitter' | 'Instagram' | 'Facebook' | 'LinkedIn'

export type PostStatus = 'Pending' | 'Published' | 'Failed'

export interface Post {
  id: string
  content: string
  mediaUrl: string | null
  platform: Platform
  scheduledAt: string
  status: PostStatus
  createdAt: string
  updatedAt: string
}

export interface CreatePostRequest {
  content: string
  mediaUrl?: string | null
  platform: Platform
  scheduledAt: string
}

export const postsApi = {
  async getAll(): Promise<Post[]> {
    const response = await fetch(`${API_URL}/posts`)
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
