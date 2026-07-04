import { describe, expect, it } from 'vitest'
import appSource from '../App.tsx?raw'
import dataDeletionPageSource from './DataDeletionPage.tsx?raw'
import settingsPageSource from './SettingsPage.tsx?raw'
import legalPageFooterSource from '../components/LegalPageFooter.tsx?raw'

describe('Public /data-deletion page', () => {
  it('is registered as an ungated public route', () => {
    expect(appSource).toMatch(/path="\/data-deletion"\s+element={<DataDeletionPage \/>}/)
  })

  it('renders markdown content through react-markdown', () => {
    expect(dataDeletionPageSource).toMatch(/ReactMarkdown/)
    expect(dataDeletionPageSource).toMatch(/data-deletion\.md\?raw/)
    expect(dataDeletionPageSource).toMatch(/\.\/PrivacyPage\.css/)
    expect(dataDeletionPageSource).toMatch(/LegalPageFooter/)
    expect(legalPageFooterSource).toMatch(/Data Deletion/)
  })

  it('is linked from an authenticated account/settings page', () => {
    expect(settingsPageSource).toMatch(/to="\/data-deletion"/)
    expect(settingsPageSource).toMatch(/Data Deletion Instructions/)
    expect(settingsPageSource).toMatch(/Legal:/)
  })
})
