import { describe, expect, it } from 'vitest'
// Source-level checks (Vite `?raw`) — no DOM harness is configured for this project,
// matching SchedulePost.workspace.test.ts. We assert the helper copy and the link
// live inside the connected-state branch via positional ordering.
import connectedAccountsSource from './ConnectedAccountsPage.tsx?raw'

const connectedStateStart = connectedAccountsSource.indexOf('<div className="meta-connected-state">')
// The connected branch ends at the disconnect button; the disconnected branch lives
// further down behind `className="connect-btn"` / `Connect to Meta`.
const disconnectBtnIndex = connectedAccountsSource.indexOf('className="disconnect-btn"', connectedStateStart)

describe('ConnectedAccountsPage — Meta publishing access clarity', () => {
  it('shows the access helper copy when Meta is connected', () => {
    const hintIndex = connectedAccountsSource.indexOf(
      'This connection gives Post Pilot access to the Facebook',
    )
    expect(hintIndex).toBeGreaterThan(connectedStateStart)
    expect(hintIndex).toBeLessThan(disconnectBtnIndex)
    // Full copy intact (whitespace-insensitive across JSX line wrapping).
    expect(connectedAccountsSource).toMatch(
      /This connection gives Post Pilot access to the Facebook\s+Pages you\s+allowed and any linked Instagram professional accounts\./,
    )
  })

  it('renders a "View Publishing Assets" link that navigates to the assets route', () => {
    const linkIndex = connectedAccountsSource.indexOf('View Publishing Assets')
    expect(linkIndex).toBeGreaterThan(connectedStateStart)
    expect(linkIndex).toBeLessThan(disconnectBtnIndex)
    expect(connectedAccountsSource).toMatch(/onClick=\{\(\) => onNavigate\('assets'\)\}/)
  })

  it('accepts an onNavigate prop for the assets link', () => {
    expect(connectedAccountsSource).toMatch(/onNavigate\?: \(page: string\) => void/)
  })

  it('does not duplicate the full asset list on this page', () => {
    // The detailed Page/IG asset rows live on the Publishing Assets page; this page
    // only links to them and never fetches the available-pages asset list.
    expect(connectedAccountsSource).not.toMatch(/Available through connected Facebook Pages/)
    expect(connectedAccountsSource).not.toMatch(/getAvailablePages/)
  })
})
