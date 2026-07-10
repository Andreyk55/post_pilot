import { useCallback, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../hooks/useAuth'
import { isDeleteAccountConfirmed, DELETE_ACCOUNT_CONFIRMATION } from '../api/account'
import {
  createDeleteAccountController,
  type DeleteAccountPhase,
} from './deleteAccountController'
import { DeleteAccountConfirmModal } from '../components/DeleteAccountConfirmModal'
import './SettingsPage.css'

/**
 * Settings → Account, including the Danger Zone "Delete account" flow.
 * Intentionally separate from Meta connection settings — account deletion is a
 * full-account action, not a provider action.
 */
export function SettingsPage() {
  const { user } = useAuth()

  const [confirmText, setConfirmText] = useState('')
  const [phase, setPhase] = useState<DeleteAccountPhase>('idle')
  const [error, setError] = useState<string | null>(null)

  const deleting = phase === 'deleting'
  // First button opens the modal; it stays available only while idle and confirmed.
  const canRequestDelete = isDeleteAccountConfirmed(confirmText) && phase === 'idle'

  // Send the user straight to the public post-deletion confirmation page.
  //
  // Deliberately does NOT clear client auth state first: DELETE /account has
  // already signed out the session cookie server-side, and dropping the local
  // user would make RequireAuth render the login screen while the browser is
  // still fetching /account-deleted — a visible login flash. The full-page
  // navigation discards all in-memory auth state anyway.
  //
  // We use replace() (not href =) so the now-defunct authenticated
  // Settings/Danger Zone entry is REMOVED from history — pressing Back must not
  // return to (or restore from bfcache) a deleted account's screen. A full-page
  // navigation also guarantees a fresh load with no stale auth state.
  const handleDeleted = useCallback(() => {
    window.location.replace('/account-deleted')
  }, [])

  const deleteController = useMemo(
    () => createDeleteAccountController({ setPhase, setError, onDeleted: handleDeleted }),
    [handleDeleted],
  )

  return (
    <div className="settings-page">
      <h1>Account Settings</h1>

      <section className="settings-section">
        <h2>Account</h2>
        {user && (
          <dl className="settings-account">
            <div>
              <dt>Name</dt>
              <dd>{user.displayName}</dd>
            </div>
            <div>
              <dt>Email</dt>
              <dd>{user.email}</dd>
            </div>
          </dl>
        )}
        <p>
          Legal:
          {' '}
          <Link to="/terms">Terms of Service</Link>
          {' '}
          <span aria-hidden>·</span>
          {' '}
          <Link to="/privacy">Privacy Policy</Link>
          {' '}
          <span aria-hidden>·</span>
          {' '}
          <Link to="/data-deletion">Data Deletion Instructions</Link>
        </p>
      </section>

      <section className="settings-section settings-danger">
        <h2>Danger zone</h2>
        <div className="settings-danger-card">
          <h3>Delete Publish Harbor account</h3>
          <p>
            This permanently deletes your Publish Harbor account, your user/account records,
            owned workspaces, provider connections, scheduled posts, drafts, uploaded
            media, bucket files, and related app data.
          </p>
          <p>
            This does not automatically delete posts already published to Facebook or
            Instagram. You can delete those directly in Facebook or Instagram.
          </p>

          <hr className="settings-danger-divider" />

          <label className="settings-danger-label" htmlFor="delete-confirm">
            To confirm, type <strong>{DELETE_ACCOUNT_CONFIRMATION}</strong>.
          </label>
          <input
            id="delete-confirm"
            type="text"
            className="settings-danger-input"
            value={confirmText}
            onChange={(e) => setConfirmText(e.target.value)}
            placeholder={DELETE_ACCOUNT_CONFIRMATION}
            autoComplete="off"
            disabled={phase !== 'idle'}
          />

          {error && <div className="settings-danger-error">{error}</div>}

          <button
            type="button"
            className="settings-danger-button"
            onClick={() => deleteController.requestDelete()}
            disabled={!canRequestDelete}
          >
            Delete account permanently
          </button>
        </div>
      </section>

      <DeleteAccountConfirmModal
        isOpen={phase !== 'idle'}
        deleting={deleting}
        error={error}
        onCancel={() => deleteController.cancel()}
        onConfirm={() => deleteController.confirmDelete(confirmText)}
      />
    </div>
  )
}
