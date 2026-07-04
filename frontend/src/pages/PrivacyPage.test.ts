import { describe, expect, it } from 'vitest'
import appSource from '../App.tsx?raw'
import loginScreenSource from '../components/LoginScreen.tsx?raw'
import privacyPageSource from './PrivacyPage.tsx?raw'

describe('Public /privacy page', () => {
  it('is registered as an ungated public route', () => {
    expect(appSource).toMatch(/path="\/privacy"\s+element={<PrivacyPage \/>}/)
  })

  it('renders markdown content through react-markdown', () => {
    expect(privacyPageSource).toMatch(/ReactMarkdown/)
    expect(privacyPageSource).toMatch(/privacy\.md\?raw/)
  })

  it('is linked from the login screen', () => {
    expect(loginScreenSource).toMatch(/to="\/privacy"/)
    expect(loginScreenSource).toMatch(/Privacy Policy/)
  })
})
