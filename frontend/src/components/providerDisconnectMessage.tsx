import type { ReactNode } from 'react'

/**
 * Provider-agnostic disconnect confirmation copy.
 *
 * Used by every provider's disconnect confirmation modal — Meta today,
 * LinkedIn/X/TikTok later. Keeps the wording in one place so all providers
 * surface the same lifecycle promise to the user.
 *
 * Mirrors the product spec verbatim (Section 12: Frontend requirements).
 */
export function buildProviderDisconnectMessage(providerLabel: string): ReactNode {
  return (
    <div className="provider-disconnect-message">
      <p>
        <strong>Disconnect {providerLabel} account?</strong> This affects the whole workspace.
      </p>
      <p>This will:</p>
      <ul>
        <li>Disconnect the {providerLabel} account from this workspace</li>
        <li>Disable its connected assets</li>
        <li>
          Cancel all drafts, scheduled posts, retries, and any non-executed posts
          related to this provider account
        </li>
        <li>Hide all {providerLabel}-related posts from the normal UI while disconnected</li>
        <li>Keep history in the database</li>
        <li>
          If the same {providerLabel} account is reconnected later, published and
          canceled history will become visible again
        </li>
        <li>Canceled posts will not be restored or rescheduled</li>
      </ul>
    </div>
  )
}
