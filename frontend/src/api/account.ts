import { config } from '../config/appConfig'

const API_URL = config.apiBaseUrl

/**
 * The exact phrase the user must type to confirm irreversible account deletion.
 * Mirrors the backend's AccountController.ConfirmationPhrase.
 */
export const DELETE_ACCOUNT_CONFIRMATION = 'DELETE MY ACCOUNT'

/**
 * True only when the typed text matches the confirmation phrase EXACTLY.
 * Used to gate the "Delete account permanently" button.
 */
export function isDeleteAccountConfirmed(text: string): boolean {
  return text === DELETE_ACCOUNT_CONFIRMATION
}

export const accountApi = {
  /**
   * Permanently deletes the CURRENT authenticated user's PostPilot account.
   *
   * The request body carries ONLY the confirmation phrase. We deliberately never
   * send a userId/accountId — the backend derives the target solely from the
   * session, so a tampered client cannot delete a different account.
   */
  async deleteAccount(confirmationText: string): Promise<void> {
    const response = await fetch(`${API_URL}/account`, {
      method: 'DELETE',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ confirmationText }),
    })
    if (!response.ok) {
      throw new Error(`Failed to delete account (${response.status})`)
    }
  },
}
