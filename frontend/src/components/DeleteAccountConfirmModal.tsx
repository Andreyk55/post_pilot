import './DeleteAccountConfirmModal.css'

interface DeleteAccountConfirmModalProps {
  isOpen: boolean
  /** True while the delete API call is in flight; disables both buttons. */
  deleting: boolean
  /** Failure message to show in-modal so the user can see it and retry. */
  error?: string | null
  onCancel: () => void
  onConfirm: () => void
}

/**
 * Final confirmation modal for the Danger Zone delete flow. Presentational only —
 * all state and the API call live in the parent via {@link createDeleteAccountController}.
 */
export function DeleteAccountConfirmModal({
  isOpen,
  deleting,
  error,
  onCancel,
  onConfirm,
}: DeleteAccountConfirmModalProps) {
  if (!isOpen) return null

  return (
    <div
      className="delete-account-modal-overlay"
      role="dialog"
      aria-modal="true"
      aria-labelledby="delete-account-modal-title"
      onClick={deleting ? undefined : onCancel}
    >
      <div className="delete-account-modal" onClick={(e) => e.stopPropagation()}>
        <h2 id="delete-account-modal-title" className="delete-account-modal-title">
          Delete account permanently?
        </h2>

        <p className="delete-account-modal-body">
          This <strong>cannot be undone.</strong> It permanently deletes your Publish Harbor
          account, your user/account records, owned workspaces, provider connections,
          scheduled posts, drafts, uploaded media, bucket files, and related app data.
        </p>
        <p className="delete-account-modal-body">
          Posts already published to Facebook or Instagram are not deleted automatically.
        </p>

        {error && <div className="delete-account-modal-error">{error}</div>}

        <div className="delete-account-modal-actions">
          <button
            type="button"
            className="delete-account-modal-cancel"
            onClick={onCancel}
            disabled={deleting}
          >
            Cancel
          </button>
          <button
            type="button"
            className="delete-account-modal-confirm"
            onClick={onConfirm}
            disabled={deleting}
          >
            {deleting ? 'Deleting…' : 'Yes, delete permanently'}
          </button>
        </div>
      </div>
    </div>
  )
}
