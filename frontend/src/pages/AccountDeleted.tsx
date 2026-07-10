import { Link } from 'react-router-dom'
import './AccountDeleted.css'

/**
 * Public, ungated confirmation page shown after a successful account deletion.
 * Route: /account-deleted.
 *
 * The delete flow lands here via a full-page redirect, so this page must render
 * WITHOUT any authenticated app state — it lives outside the gated app in App.tsx.
 */
export function AccountDeleted() {
  return (
    <div className="account-deleted-page">
      <div className="account-deleted-card">
        <h1>Account deleted</h1>
        <p>Your Publish Harbor account and related app data have been deleted.</p>
        <p className="account-deleted-note">
          Posts already published to Facebook or Instagram were not deleted automatically.
        </p>
        <Link className="account-deleted-button" to="/">
          Return to homepage
        </Link>
      </div>
    </div>
  )
}
