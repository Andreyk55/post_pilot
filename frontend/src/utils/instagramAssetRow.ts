import type { ConnectedInstagramAccount } from '../types/meta'

/**
 * Presentation model for a row in Assets → Instagram Business → "Connected accounts".
 *
 * Product rule: a linked Instagram professional account is publishable iff its parent
 * Facebook Page is connected. Instagram connected accounts are therefore DERIVED assets,
 * not independently connected/disconnected ones. To disable Instagram publishing, the user
 * disconnects the parent Facebook Page (or the Meta provider) — there is no per-IG opt-out.
 *
 * Consequences encoded here so the UI and its tests stay in lockstep:
 *   - Rows are read-only: `canDisconnect` is always false (no X / disconnect action).
 *   - Each row attributes the IG to the Facebook Page it is available through.
 */
export interface InstagramAssetRowView {
  /** "@username", else the IG display name, else a stable fallback. */
  displayName: string
  /** e.g. "Available through connected Facebook Page: Posts Dev Page". */
  parentPageLabel: string
  /** Always false — IG connected accounts are derived assets and cannot be opted out individually. */
  canDisconnect: false
}

export function instagramAssetRowView(
  ig: Pick<ConnectedInstagramAccount, 'username' | 'name' | 'pageName'>,
): InstagramAssetRowView {
  const displayName = ig.username
    ? `@${ig.username}`
    : ig.name ?? 'Instagram Account'

  return {
    displayName,
    parentPageLabel: `Available through connected Facebook Page: ${ig.pageName}`,
    canDisconnect: false,
  }
}
