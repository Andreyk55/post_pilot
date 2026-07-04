import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../hooks/useAuth'
import {
  accountApi,
  isDeleteAccountConfirmed,
  DELETE_ACCOUNT_CONFIRMATION,
} from '../api/account'
import './SettingsPage.css'

/**
 * Settings → Account, including the Danger Zone "Delete account" flow.
 * Intentionally separate from Meta connection settings — account deletion is a
 * full-account action, not a provider action.
 */
export function SettingsPage() {
  const { user, logout } = useAuth()

  const [confirmText, setConfirmText] = useState('')
  const [deleting, setDeleting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const canDelete = isDeleteAccountConfirmed(confirmText) && !deleting

  async function handleDelete() {
    if (!canDelete) return
    setDeleting(true)
    setError(null)
    try {
      await accountApi.deleteAccount(confirmText)
      // Clear local auth state, then send the user to the landing/login page.
      await logout().catch(() => undefined)
      window.location.href = '/'
    } catch {
      setError('We could not delete your account. Please try again or contact support.')
      setDeleting(false)
    }
  }

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
            disabled={deleting}
          />

          {error && <div className="settings-danger-error">{error}</div>}

          <button
            type="button"
            className="settings-danger-button"
            onClick={handleDelete}
            disabled={!canDelete}
          >
            {deleting ? 'Deleting…' : 'Delete account permanently'}
          </button>
        </div>
      </section>
    </div>
  )
}
