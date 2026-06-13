import type { ConnectedPage, ConnectedInstagramAccount, InstagramEligibilityDto } from '../types/meta'

/**
 * Detects the production-bug state: a CONNECTED Facebook Page whose Meta-linked Instagram
 * professional account (eligibility status "Connected") was never promoted to a connected
 * publishable IG asset. In that state the composer wrongly shows "No Instagram Business
 * Account connected" even though the linked IG is available.
 *
 * When this returns true, the caller should trigger the idempotent backend repair
 * (`metaApi.refreshAssets`) once and reload — promoting the linked IG so it appears as a
 * connected Instagram account everywhere (Assets, SchedulePost, post validation, publisher).
 *
 * Single source of this predicate so the Assets and SchedulePost pages stay in lockstep
 * (avoids duplicated, drifting `hasUnpromotedLinkedIg` logic).
 */
export function hasUnpromotedLinkedInstagram(
  connectedPages: Pick<ConnectedPage, 'pageId'>[],
  connectedInstagramAccounts: Pick<ConnectedInstagramAccount, 'igBusinessId'>[],
  eligibility: Pick<InstagramEligibilityDto, 'pageId' | 'igUserId' | 'eligibilityStatus'>[],
): boolean {
  const connectedPageIds = new Set(connectedPages.map(p => p.pageId))
  const connectedIgBusinessIds = new Set(connectedInstagramAccounts.map(ig => ig.igBusinessId))

  return eligibility.some(
    e =>
      e.eligibilityStatus === 'Connected' &&
      !!e.igUserId &&
      connectedPageIds.has(e.pageId) &&
      !connectedIgBusinessIds.has(e.igUserId),
  )
}
