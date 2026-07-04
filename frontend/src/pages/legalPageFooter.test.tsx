import { describe, expect, it } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import { renderToStaticMarkup } from 'react-dom/server'
import { PrivacyPage } from './PrivacyPage'
import { TermsPage } from './TermsPage'
import { DataDeletionPage } from './DataDeletionPage'

function renderPage(page: React.ReactElement) {
  return renderToStaticMarkup(<MemoryRouter>{page}</MemoryRouter>)
}

function expectSharedLegalFooter(html: string) {
  expect(html).toContain('Back to Publish Harbor')
  expect(html).toContain('Privacy Policy')
  expect(html).toContain('Terms of Service')
  expect(html).toContain('Data Deletion')
  expect(html).toContain('href="/"')
  expect(html).toContain('href="/privacy"')
  expect(html).toContain('href="/terms"')
  expect(html).toContain('href="/data-deletion"')
}

describe('Shared legal page footer', () => {
  it('appears on the privacy page', () => {
    expectSharedLegalFooter(renderPage(<PrivacyPage />))
  })

  it('appears on the terms page', () => {
    expectSharedLegalFooter(renderPage(<TermsPage />))
  })

  it('appears on the data deletion page', () => {
    expectSharedLegalFooter(renderPage(<DataDeletionPage />))
  })
})
