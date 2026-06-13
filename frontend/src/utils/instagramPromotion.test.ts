import { describe, it, expect } from 'vitest'
import { hasUnpromotedLinkedInstagram } from './instagramPromotion'
import type { InstagramEligibilityDto } from '../types/meta'

const eligibility = (
  overrides: Partial<InstagramEligibilityDto> & Pick<InstagramEligibilityDto, 'pageId' | 'eligibilityStatus'>,
): InstagramEligibilityDto => ({
  pageId: overrides.pageId,
  pageName: overrides.pageName ?? 'Posts Dev Page',
  igUserId: overrides.igUserId ?? null,
  igUsername: overrides.igUsername ?? null,
  igDisplayName: overrides.igDisplayName ?? null,
  igProfilePictureUrl: overrides.igProfilePictureUrl ?? null,
  eligibilityStatus: overrides.eligibilityStatus,
  reason: overrides.reason ?? '',
})

describe('hasUnpromotedLinkedInstagram', () => {
  it('detects the production bug: connected page with a linked IG that is not a connected asset', () => {
    // Screenshot state: "Posts Dev Page" connected, eligibility shows linked @appquestor,
    // but "Connected accounts" (instagramAccounts) is empty.
    const result = hasUnpromotedLinkedInstagram(
      [{ pageId: 'fb-page-1' }],
      [], // no connected IG assets — the bug
      [eligibility({ pageId: 'fb-page-1', eligibilityStatus: 'Connected', igUserId: 'ig-appquestor', igUsername: 'appquestor' })],
    )
    expect(result).toBe(true)
  })

  it('returns false once the linked IG is already a connected asset (post-repair)', () => {
    const result = hasUnpromotedLinkedInstagram(
      [{ pageId: 'fb-page-1' }],
      [{ igBusinessId: 'ig-appquestor' }],
      [eligibility({ pageId: 'fb-page-1', eligibilityStatus: 'Connected', igUserId: 'ig-appquestor', igUsername: 'appquestor' })],
    )
    expect(result).toBe(false)
  })

  it('returns false when the page has no linked IG', () => {
    const result = hasUnpromotedLinkedInstagram(
      [{ pageId: 'fb-page-1' }],
      [],
      [eligibility({ pageId: 'fb-page-1', eligibilityStatus: 'NotLinked' })],
    )
    expect(result).toBe(false)
  })

  it('ignores a linked IG whose page is NOT connected (workspace/selection scope)', () => {
    const result = hasUnpromotedLinkedInstagram(
      [{ pageId: 'fb-page-1' }], // connected page
      [],
      [eligibility({ pageId: 'fb-other-page', eligibilityStatus: 'Connected', igUserId: 'ig-other', igUsername: 'other' })],
    )
    expect(result).toBe(false)
  })

  it('returns false when eligibility reports Connected but without an igUserId', () => {
    const result = hasUnpromotedLinkedInstagram(
      [{ pageId: 'fb-page-1' }],
      [],
      [eligibility({ pageId: 'fb-page-1', eligibilityStatus: 'Connected', igUserId: null })],
    )
    expect(result).toBe(false)
  })
})
