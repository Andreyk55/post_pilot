import { describe, expect, it } from 'vitest'
// Source-level checks (Vite `?raw`) — no DOM harness is configured for this project,
// matching SchedulePost.workspace.test.ts. Behavioural coverage of the connect flow
// lives in assetsPageController.test.ts (the load/connect wiring is extracted there).
import assetsPageSource from './AssetsPage.tsx?raw'
import assetsControllerSource from './assetsPageController.ts?raw'

describe('AssetsPage — Publishing Assets rename', () => {
  it('titles the page "Publishing Assets"', () => {
    expect(assetsPageSource).toMatch(/<h1>Publishing Assets<\/h1>/)
    expect(assetsPageSource).not.toMatch(/<h1>Assets<\/h1>/)
  })

  it('uses the publishing-focused subtitle', () => {
    expect(assetsPageSource).toMatch(
      /Manage the Facebook Pages and Instagram professional accounts available for publishing\./,
    )
  })

  it('keeps the detailed Facebook Page and linked Instagram sections', () => {
    // The rename is copy-only — the detailed asset management sections stay intact.
    expect(assetsPageSource).toMatch(/<h2>Facebook Pages<\/h2>/)
    expect(assetsPageSource).toMatch(/<h2>Linked Instagram professional accounts<\/h2>/)
    expect(assetsPageSource).toMatch(/Available through connected Facebook Pages/)
  })

  it('does not rename backend/provider asset concepts (route id, API terms)', () => {
    // Only visible copy changes; the 'accounts' navigation target stays on the page,
    // and the metaApi asset calls are untouched (now wired through the controller).
    expect(assetsPageSource).toMatch(/onNavigate\('accounts'\)/)
    expect(assetsControllerSource).toMatch(/getAvailablePages: metaApi\.getAvailablePages/)
  })
})

describe('AssetsPage — connect does not blank the page', () => {
  it('shows the full-page "Loading assets..." branch only behind the global loading flag', () => {
    // The only full-page loader is guarded by `if (loading)`, and `loading` is the
    // GLOBAL initial-load flag — nothing else in the page toggles it.
    expect(assetsPageSource).toMatch(/if \(loading\) \{[\s\S]*Loading assets\.\.\./)
  })

  it('drives connect/disconnect through the controller, not the global loader', () => {
    // Clicking Connect must go through the controller (in-place refresh), never a
    // handler that flips the global loading state.
    expect(assetsPageSource).toMatch(/controller\.connectPage\(page, metaConnection\)/)
    expect(assetsPageSource).toMatch(/controller\.disconnectPage\(page, metaConnection\)/)
    // Only the initial mount may show the full-page loader.
    expect(assetsPageSource).toMatch(/controller\.loadInitial\(\)/)
  })

  it('keeps the per-row "Connecting..." state and renders a Toast for feedback', () => {
    expect(assetsPageSource).toMatch(/connectingPageIds\.has\(page\.id\)/)
    expect(assetsPageSource).toMatch(/Connecting\.\.\./)
    // Feedback is a non-blocking Toast, not a blocking alert().
    expect(assetsPageSource).toMatch(/<Toast\b/)
    expect(assetsPageSource).not.toMatch(/\balert\(/)
  })
})

describe('AssetsPage — connect/disconnect keeps the lower relationship section mounted', () => {
  it('renders the "Facebook Pages and linked Instagram accounts" section unconditionally', () => {
    // During an in-place connect/disconnect refresh the lower relationship section must
    // stay mounted so the last known rows remain visible (stale-while-refresh). It was
    // previously wrapped in `{!loadingPages && (...)}`, which unmounted it while refreshing.
    expect(assetsPageSource).toMatch(/Facebook Pages and linked Instagram accounts/)
    expect(assetsPageSource).not.toMatch(
      /\{!loadingPages && \([\s\S]*?Facebook Pages and linked Instagram accounts/,
    )
  })

  it('signals the in-place refresh with a "Refreshing..." badge, never by hiding a section', () => {
    // Refresh feedback is the small badge; the section itself never unmounts on `loadingPages`.
    expect(assetsPageSource).toMatch(
      /loadingPages && <span className="loading-badge">Refreshing\.\.\.<\/span>/,
    )
  })

  it('keeps the full-page "Loading assets..." state exclusive to the initial-load flag', () => {
    // Only `if (loading)` — the GLOBAL initial-load flag — may show the full-page loader;
    // the in-place refresh flag (loadingPages) must never blank the page.
    expect(assetsPageSource).toMatch(/if \(loading\) \{[\s\S]*Loading assets\.\.\./)
    expect(assetsPageSource).not.toMatch(/if \(loadingPages\)/)
  })
})
