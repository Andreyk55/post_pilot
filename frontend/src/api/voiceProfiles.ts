const API_URL = 'http://localhost:5122/api'

export interface VoiceProfile {
  id: string
  name: string
  description: string | null
  doRules: string | null
  dontRules: string | null
  bannedWords: string | null
  examplePosts: string | null
  createdAt: string
  updatedAt: string
}

export interface VoiceProfileSummary {
  id: string
  name: string
  updatedAt: string
}

export interface CreateVoiceProfileRequest {
  name: string
  description?: string | null
  doRules?: string | null
  dontRules?: string | null
  bannedWords?: string | null
  examplePosts?: string | null
}

export interface UpdateVoiceProfileRequest {
  name: string
  description?: string | null
  doRules?: string | null
  dontRules?: string | null
  bannedWords?: string | null
  examplePosts?: string | null
}

export class VoiceProfileError extends Error {
  status: number
  title: string

  constructor(message: string, status: number, title: string = 'Error') {
    super(message)
    this.name = 'VoiceProfileError'
    this.status = status
    this.title = title
  }
}

async function handleResponse<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const error = await response.json().catch(() => ({
      title: 'Error',
      detail: 'An unexpected error occurred',
      status: response.status,
    }))
    throw new VoiceProfileError(error.detail || error.title, response.status, error.title)
  }
  return response.json()
}

export const voiceProfileApi = {
  /**
   * Get all voice profiles for the current user.
   */
  async getProfiles(): Promise<VoiceProfileSummary[]> {
    const response = await fetch(`${API_URL}/ai/voice-profiles`)
    return handleResponse<VoiceProfileSummary[]>(response)
  },

  /**
   * Get a specific voice profile by ID.
   */
  async getProfile(id: string): Promise<VoiceProfile> {
    const response = await fetch(`${API_URL}/ai/voice-profiles/${id}`)
    return handleResponse<VoiceProfile>(response)
  },

  /**
   * Create a new voice profile.
   */
  async createProfile(request: CreateVoiceProfileRequest): Promise<VoiceProfile> {
    const response = await fetch(`${API_URL}/ai/voice-profiles`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })
    return handleResponse<VoiceProfile>(response)
  },

  /**
   * Update an existing voice profile.
   */
  async updateProfile(id: string, request: UpdateVoiceProfileRequest): Promise<VoiceProfile> {
    const response = await fetch(`${API_URL}/ai/voice-profiles/${id}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })
    return handleResponse<VoiceProfile>(response)
  },

  /**
   * Delete a voice profile.
   */
  async deleteProfile(id: string): Promise<void> {
    const response = await fetch(`${API_URL}/ai/voice-profiles/${id}`, {
      method: 'DELETE',
    })
    if (!response.ok) {
      const error = await response.json().catch(() => ({
        title: 'Error',
        detail: 'Failed to delete voice profile',
        status: response.status,
      }))
      throw new VoiceProfileError(error.detail || error.title, response.status, error.title)
    }
  },
}
