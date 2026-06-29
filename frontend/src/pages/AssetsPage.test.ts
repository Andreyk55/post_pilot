import { describe, expect, it } from 'vitest'
// Source-level checks (Vite `?raw`) — no DOM harness is configured for this project,
// matching SchedulePost.workspace.test.ts.
import assetsPageSource from './AssetsPage.tsx?raw'

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
    // Only visible copy changes; the 'accounts' navigation target and metaApi asset
    // calls are untouched.
    expect(assetsPageSource).toMatch(/onNavigate\('accounts'\)/)
    expect(assetsPageSource).toMatch(/metaApi\.getAvailablePages\(\)/)
  })
})
