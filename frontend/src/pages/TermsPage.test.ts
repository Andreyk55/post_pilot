import { describe, expect, it } from 'vitest'
import appSource from '../App.tsx?raw'
import loginScreenSource from '../components/LoginScreen.tsx?raw'
import settingsPageSource from './SettingsPage.tsx?raw'
import termsPageSource from './TermsPage.tsx?raw'

describe('Public /terms page', () => {
  it('is registered as an ungated public route', () => {
    expect(appSource).toMatch(/path="\/terms"\s+element={<TermsPage \/>}/)
  })

  it('renders markdown content through react-markdown', () => {
    expect(termsPageSource).toMatch(/ReactMarkdown/)
    expect(termsPageSource).toMatch(/terms\.md\?raw/)
    expect(termsPageSource).toMatch(/\.\/PrivacyPage\.css/)
  })

  it('is linked from the login screen and authenticated settings page', () => {
    expect(loginScreenSource).toMatch(/to="\/terms"/)
    expect(loginScreenSource).toMatch(/Terms of Service/)
    expect(settingsPageSource).toMatch(/to="\/terms"/)
    expect(settingsPageSource).toMatch(/Terms of Service/)
  })
})
