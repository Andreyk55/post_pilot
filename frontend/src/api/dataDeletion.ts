import { config } from '../config/appConfig'

const API_URL = config.apiBaseUrl

/** Public status of a data-deletion request (no internal ids are ever exposed). */
export interface DataDeletionStatus {
  confirmationCode: string
  provider: string
  status: 'Processing' | 'Completed' | 'AlreadyDeleted' | 'Failed' | string
  requestedAt: string
  completedAt: string | null
}

export const dataDeletionApi = {
  /**
   * Fetches the status for a confirmation code. Returns null when the code is
   * unknown (404) so the page can show a friendly "not found" instead of erroring.
   */
  async getStatus(confirmationCode: string): Promise<DataDeletionStatus | null> {
    const response = await fetch(
      `${API_URL}/data-deletion/status/${encodeURIComponent(confirmationCode)}`,
    )
    if (response.status === 404) return null
    if (!response.ok) {
      throw new Error(`Failed to fetch deletion status (${response.status})`)
    }
    return (await response.json()) as DataDeletionStatus
  },
}
