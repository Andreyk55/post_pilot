import { describe, expect, it } from 'vitest'
import appSource from '../App.tsx?raw'
import dataDeletionPageSource from './DataDeletionPage.tsx?raw'
import settingsPageSource from './SettingsPage.tsx?raw'

describe('Public /data-deletion page', () => {
  it('is registered as an ungated public route', () => {
    expect(appSource).toMatch(/path="\/data-deletion"\s+element={<DataDeletionPage \/>}/)
  })

  it('renders markdown content through react-markdown', () => {
    expect(dataDeletionPageSource).toMatch(/ReactMarkdown/)
    expect(dataDeletionPageSource).toMatch(/data-deletion\.md\?raw/)
    expect(dataDeletionPageSource).toMatch(/\.\/PrivacyPage\.css/)
  })

  it('is linked from an authenticated account/settings page', () => {
    expect(settingsPageSource).toMatch(/to="\/data-deletion"/)
    expect(settingsPageSource).toMatch(/Data Deletion Instructions/)
  })
})
