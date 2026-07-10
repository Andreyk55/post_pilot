import { describe, expect, it } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import { renderToStaticMarkup } from 'react-dom/server'
import appSource from '../App.tsx?raw'
import settingsPageSource from './SettingsPage.tsx?raw'
import { AccountDeleted } from './AccountDeleted'

function renderPage() {
  return renderToStaticMarkup(
    <MemoryRouter>
      <AccountDeleted />
    </MemoryRouter>,
  )
}

describe('AccountDeleted page', () => {
  it('renders the post-deletion confirmation text', () => {
    const html = renderPage()
    expect(html).toContain('Account deleted')
    expect(html).toContain('Your Publish Harbor account and related app data have been deleted.')
    expect(html).toContain(
      'Posts already published to Facebook or Instagram were not deleted automatically.',
    )
  })

  it('offers a link back to the homepage', () => {
    const html = renderPage()
    expect(html).toContain('Return to homepage')
    expect(html).toContain('href="/"')
  })
})

describe('/account-deleted routing', () => {
  it('is registered as an ungated public route', () => {
    expect(appSource).toMatch(/path="\/account-deleted"\s+element={<AccountDeleted \/>}/)
  })

  it('is declared before the gated app, so it is reachable without authentication', () => {
    const routeIdx = appSource.indexOf('path="/account-deleted"')
    const gatedIdx = appSource.indexOf('<GatedApp')
    expect(routeIdx).toBeGreaterThan(-1)
    expect(gatedIdx).toBeGreaterThan(-1)
    // Public routes must precede the catch-all gated app or they get swallowed by it.
    expect(routeIdx).toBeLessThan(gatedIdx)
  })
})

describe('delete-account success redirect', () => {
  it('redirects to /account-deleted after a successful deletion', () => {
    // confirmDelete → onDeleted (proven in deleteAccountController.test.ts) and
    // onDeleted is wired to handleDeleted, which performs the redirect.
    expect(settingsPageSource).toMatch(/onDeleted: handleDeleted/)
    expect(settingsPageSource).toMatch(/window\.location\.replace\('\/account-deleted'\)/)
  })

  it('uses replacement navigation so Back cannot return to the deleted account screen', () => {
    // replace() drops the current (authenticated) history entry; href = would
    // push, leaving the Settings/Danger Zone page reachable via the Back button.
    expect(settingsPageSource).toContain("window.location.replace('/account-deleted')")
    expect(settingsPageSource).not.toMatch(/window\.location\.href\s*=\s*'\/account-deleted'/)
  })
})
