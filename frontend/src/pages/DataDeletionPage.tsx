import { Link } from 'react-router-dom'
import './DataDeletionPage.css'

/**
 * Public, ungated informational page (no login / no password gate). This is the
 * URL registered with Meta as the app's Data Deletion Instructions URL.
 *
 * Informational ONLY: it exposes no support email and no contact form. Logged-in
 * users are directed to the in-app "Contact Us" (sidebar) for help.
 *
 * Route: /data-deletion
 */

export function DataDeletionPage() {
  return (
    <div className="data-deletion-page">
      <div className="data-deletion-card">
        <h1>PostPilot Data Deletion</h1>
        <p className="data-deletion-lead">
          This page explains how to delete your data from PostPilot. There are two
          separate things you can delete, and they are independent of each other.
        </p>

        <section>
          <h2>1. Delete your entire PostPilot account</h2>
          <p>
            Sign in to PostPilot and go to{' '}
            <strong>Settings → Account → Danger zone → Delete account</strong>. This
            permanently deletes your PostPilot account and everything you own:
            your user/account records, owned workspaces, provider connections,
            scheduled posts, drafts, uploaded media, stored files, and related app
            data.
          </p>
        </section>

        <section>
          <h2>2. Remove PostPilot from Facebook / Meta</h2>
          <p>
            You can remove PostPilot from your Facebook settings (Settings &amp;
            Privacy → Settings → Apps and Websites) and request deletion of the data
            PostPilot obtained through Meta. When Meta notifies us of your deletion
            request, PostPilot deletes the Meta-related data it holds for your account:
          </p>
          <ul>
            <li>Access tokens</li>
            <li>Facebook Page data</li>
            <li>Instagram account data discovered through Meta</li>
            <li>Unpublished / scheduled Meta posts</li>
            <li>Related Meta publishing data</li>
            <li>Meta media stored by PostPilot</li>
          </ul>
        </section>

        <section className="data-deletion-note">
          <h2>Posts already published to Facebook or Instagram</h2>
          <p>
            Deleting your PostPilot data does <strong>not</strong> automatically delete
            posts that were already published to Facebook or Instagram. If you want to
            remove those, delete them directly in Facebook or Instagram.
          </p>
        </section>

        <section>
          <h2>Need help?</h2>
          <p>
            If you have a PostPilot account and need help, sign in to PostPilot and use{' '}
            <strong>Contact Us</strong> from the sidebar to send us a message. Deletion
            requests are typically processed immediately and confirmed within 30 days.
          </p>
          <p className="data-deletion-links">
            <Link to="/">Back to PostPilot</Link>
          </p>
        </section>
      </div>
    </div>
  )
}
