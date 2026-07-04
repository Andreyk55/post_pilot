import { describe, expect, it } from 'vitest'
import appSource from '../App.tsx?raw'
import loginScreenSource from '../components/LoginScreen.tsx?raw'
import settingsPageSource from './SettingsPage.tsx?raw'
import privacyPageSource from './PrivacyPage.tsx?raw'
import legalPageFooterSource from '../components/LegalPageFooter.tsx?raw'

describe('Public /privacy page', () => {
  it('is registered as an ungated public route', () => {
    expect(appSource).toMatch(/path="\/privacy"\s+element={<PrivacyPage \/>}/)
  })

  it('renders markdown content through react-markdown', () => {
    expect(privacyPageSource).toMatch(/ReactMarkdown/)
    expect(privacyPageSource).toMatch(/privacy\.md\?raw/)
    expect(privacyPageSource).toMatch(/LegalPageFooter/)
    expect(legalPageFooterSource).toMatch(/Back to Publish Harbor/)
    expect(legalPageFooterSource).toMatch(/Privacy Policy/)
    expect(legalPageFooterSource).toMatch(/Terms of Service/)
    expect(legalPageFooterSource).toMatch(/Data Deletion/)
  })

  it('is linked from the login screen', () => {
    expect(loginScreenSource).toMatch(/to="\/privacy"/)
    expect(loginScreenSource).toMatch(/acknowledge the/)
    expect(loginScreenSource).toMatch(/Privacy Policy/)
  })

  it('is linked from an authenticated account/settings page', () => {
    expect(settingsPageSource).toMatch(/to="\/privacy"/)
    expect(settingsPageSource).toMatch(/Privacy Policy/)
  })
})
