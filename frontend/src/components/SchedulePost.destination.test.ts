import { describe, expect, it } from 'vitest'
// Source-level checks (Vite `?raw`) — no DOM harness is configured for this project,
// matching SchedulePost.workspace.test.ts / SchedulePost.channelSwitch.test.ts. These
// pin the compact "selected publishing destination" confirmation shown under each
// asset selector.
import schedulePostSource from './SchedulePost.tsx?raw'

describe('SchedulePost — selected destination confirmation', () => {
  it('resolves the selected Page/IG only from a chosen id (display-only lookup)', () => {
    expect(schedulePostSource).toMatch(
      /const selectedPage = selectedPageId\s*\?\s*connectedPages\.find\(page => page\.id === selectedPageId\)/,
    )
    expect(schedulePostSource).toMatch(
      /const selectedInstagramAccount = selectedInstagramAccountId\s*\?\s*connectedInstagramAccounts\.find\(account => account\.id === selectedInstagramAccountId\)/,
    )
  })

  it('shows the Facebook destination confirmation only after a Page is selected', () => {
    // Guarded by the resolved selection so nothing renders before an asset is picked.
    expect(schedulePostSource).toMatch(/\{selectedPage && \(/)
    expect(schedulePostSource).toMatch(/Posting to Facebook Page: <strong>\{selectedPage\.name\}<\/strong>/)
  })

  it('shows the connected Meta account name for the Facebook destination', () => {
    expect(schedulePostSource).toMatch(/setConnectedMetaAccountName\(response\.connection\.providerAccountName \?\? ''\)/)
    expect(schedulePostSource).toMatch(
      /Connected via Meta account: <strong>\{connectedMetaAccountName\}<\/strong>/,
    )
  })

  it('shows the Instagram destination confirmation only after an account is selected', () => {
    expect(schedulePostSource).toMatch(/\{selectedInstagramAccount && \(/)
    expect(schedulePostSource).toMatch(
      /Posting to Instagram account: <strong>@\{selectedInstagramAccount\.username\}<\/strong>/,
    )
  })

  it('shows the linked Facebook Page for the Instagram destination', () => {
    expect(schedulePostSource).toMatch(
      /Linked Facebook Page: <strong>\{selectedInstagramAccount\.pageName\}<\/strong>/,
    )
  })

  it('places each confirmation next to its selector (within the selector block)', () => {
    const fbSelectorIndex = schedulePostSource.indexOf('id="facebookPage"')
    const fbConfirmIndex = schedulePostSource.indexOf('Posting to Facebook Page:')
    const igSelectorIndex = schedulePostSource.indexOf('id="instagramAccount"')
    const igConfirmIndex = schedulePostSource.indexOf('Posting to Instagram account:')

    expect(fbConfirmIndex).toBeGreaterThan(fbSelectorIndex)
    expect(igConfirmIndex).toBeGreaterThan(igSelectorIndex)
    // FB confirmation comes before the IG selector — they are not interleaved.
    expect(fbConfirmIndex).toBeLessThan(igSelectorIndex)
  })

  it('keeps the confirmation display-only (no new validation/publishing wiring)', () => {
    // The confirmation must not introduce new gating: the submitted target ids and
    // the form-validity gates still key off selectedPageId / selectedInstagramAccountId.
    expect(schedulePostSource).toMatch(/targetPageId: isFacebookSelected \? selectedPageId : undefined/)
    expect(schedulePostSource).toMatch(
      /targetInstagramAccountId: isInstagramSelected \? selectedInstagramAccountId : undefined/,
    )
  })
})
