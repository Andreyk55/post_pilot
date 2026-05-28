import { config } from '../config/appConfig'

const API_URL = config.apiBaseUrl

export interface WorkspaceSummary {
  id: string
  name: string
  role: 'Owner' | 'Member'
  isCurrent: boolean
}

export interface WorkspaceSwitchResponse {
  currentWorkspaceId: string
  workspaceName: string
}

export const workspacesApi = {
  async list(): Promise<WorkspaceSummary[]> {
    const response = await fetch(`${API_URL}/workspaces`, {
      method: 'GET',
      credentials: 'include',
    })
    if (!response.ok) {
      throw new Error(`workspaces_list_failed_${response.status}`)
    }
    return (await response.json()) as WorkspaceSummary[]
  },

  async create(name: string): Promise<WorkspaceSummary> {
    const response = await fetch(`${API_URL}/workspaces`, {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name }),
    })
    if (!response.ok) {
      const text = await response.text().catch(() => '')
      throw new Error(`workspaces_create_failed_${response.status}_${text}`)
    }
    return (await response.json()) as WorkspaceSummary
  },

  async switchTo(workspaceId: string): Promise<WorkspaceSwitchResponse> {
    const response = await fetch(`${API_URL}/workspaces/${workspaceId}/switch`, {
      method: 'POST',
      credentials: 'include',
    })
    if (response.status === 403) {
      throw new Error('workspaces_switch_forbidden')
    }
    if (!response.ok) {
      throw new Error(`workspaces_switch_failed_${response.status}`)
    }
    return (await response.json()) as WorkspaceSwitchResponse
  },
}
