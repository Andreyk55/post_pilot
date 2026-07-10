import { accountApi } from '../api/account'

/**
 * The three states of the Danger Zone delete flow:
 *  - 'idle'       → no modal; the "Delete account permanently" button is available.
 *  - 'confirming' → the final confirmation modal is open, awaiting an explicit choice.
 *  - 'deleting'   → the delete API call is in flight.
 */
export type DeleteAccountPhase = 'idle' | 'confirming' | 'deleting'

export const DELETE_ACCOUNT_ERROR =
  'We could not delete your account. Please try again or contact support.'

export interface DeleteAccountControllerDeps {
  /** Persists the new phase; drives whether the modal is shown and buttons are disabled. */
  setPhase: (phase: DeleteAccountPhase) => void
  /** Surfaces or clears the inline error message. */
  setError: (error: string | null) => void
  /** Runs after a successful deletion (e.g. clear auth + redirect). */
  onDeleted: () => Promise<void> | void
  /**
   * The delete-account API call. Injected so tests can assert exactly when — and
   * whether — the irreversible call happens. Defaults to the real endpoint.
   */
  deleteAccount?: (confirmationText: string) => Promise<void>
}

/**
 * Owns the wiring for the Danger Zone delete flow so the "which action calls the
 * irreversible API" decision is unit-testable without a DOM.
 *
 * Contract:
 *  - `requestDelete` (first button) ONLY opens the modal — it never calls the API.
 *  - `cancel` ONLY closes the modal — it never calls the API.
 *  - `confirmDelete` (final modal button) is the ONE path that calls the delete API.
 */
export function createDeleteAccountController(deps: DeleteAccountControllerDeps) {
  const deleteAccount = deps.deleteAccount ?? accountApi.deleteAccount

  return {
    /** First "Delete account permanently" button: opens the confirmation modal only. */
    requestDelete() {
      deps.setError(null)
      deps.setPhase('confirming')
    },

    /** Modal "Cancel": closes the modal without touching the account. */
    cancel() {
      deps.setPhase('idle')
    },

    /** Modal "Yes, delete permanently": the only action that performs the deletion. */
    async confirmDelete(confirmationText: string) {
      deps.setPhase('deleting')
      deps.setError(null)
      try {
        await deleteAccount(confirmationText)
        await deps.onDeleted()
      } catch {
        // Keep the modal open so the user can retry the confirmed action.
        deps.setError(DELETE_ACCOUNT_ERROR)
        deps.setPhase('confirming')
      }
    },
  }
}

export type DeleteAccountController = ReturnType<typeof createDeleteAccountController>
