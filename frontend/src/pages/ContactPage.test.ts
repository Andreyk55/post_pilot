import { describe, it, expect } from 'vitest'
// Import component sources as raw strings (Vite `?raw`) so these checks run in the
// project's Node test environment without a DOM harness (mirrors
// components/SchedulePost.workspace.test.ts).
import contactPageSource from './ContactPage.tsx?raw'
import sidebarSource from '../components/Sidebar.tsx?raw'
import appSource from '../App.tsx?raw'
import dataDeletionSource from './DataDeletionPage.tsx?raw'

describe('Sidebar — Contact Us entry', () => {
  it('lists a Contact Us nav item (visible only inside the logged-in app)', () => {
    expect(sidebarSource).toMatch(/id:\s*'contact'/)
    expect(sidebarSource).toMatch(/label:\s*'Contact Us'/)
  })
})

describe('ContactPage — form', () => {
  it('renders the Contact Us title and the help description', () => {
    expect(contactPageSource).toMatch(/<h1>Contact Us<\/h1>/)
    expect(contactPageSource).toMatch(/Need help with PostPilot\? Send us a message/)
  })

  it('renders subject, message, and optional category fields', () => {
    expect(contactPageSource).toMatch(/contact-subject/)
    expect(contactPageSource).toMatch(/contact-message/)
    expect(contactPageSource).toMatch(/contact-category/)
    expect(contactPageSource).toMatch(/Category \(optional\)/)
  })

  it('gates the submit button on isContactFormValid and the sending flag', () => {
    expect(contactPageSource).toMatch(/isContactFormValid\(subject,\s*message\)\s*&&\s*!sending/)
    expect(contactPageSource).toMatch(/disabled=\{!canSend\}/)
  })

  it('uses the exact success and error copy', () => {
    expect(contactPageSource).toMatch(
      /Thanks, your message was sent\. We will review it and get back to you\./,
    )
    expect(contactPageSource).toMatch(
      /We could not send your message right now\. Please try again\./,
    )
  })

  it('submits via supportApi with only category/subject/message, and collects no identity fields', () => {
    // Delegates to the API client (which builds the body); the page passes only the
    // three allowed fields.
    expect(contactPageSource).toMatch(/supportApi\.sendContactMessage\(/)
    expect(contactPageSource).toMatch(/category:\s*category\s*\|\|\s*null/)
    expect(contactPageSource).toMatch(/\bsubject\b/)
    expect(contactPageSource).toMatch(/\bmessage\b/)
    // No identity input is ever rendered (no email/userId/accountId field).
    expect(contactPageSource).not.toMatch(/type="email"/)
    expect(contactPageSource).not.toMatch(/id="contact-(email|user|account)/)
  })
})

describe('Contact Us route — authenticated only, not public', () => {
  it('is wired through the gated MainApp page switch, not a public <Route>', () => {
    // Rendered via the in-app page switch (reached from the sidebar)…
    expect(appSource).toMatch(/case 'contact':/)
    expect(appSource).toMatch(/<ContactPage \/>/)
    // …and NOT registered as a public route alongside the data-deletion pages.
    expect(appSource).not.toMatch(/path="\/contact"/)
  })
})

describe('Public /data-deletion page — informational only', () => {
  it('exposes no support email / mailto and no contact form', () => {
    expect(dataDeletionSource).not.toMatch(/mailto:/)
    expect(dataDeletionSource).not.toMatch(/support@/)
    expect(dataDeletionSource).not.toMatch(/<form/)
  })

  it('directs logged-in users to in-app Contact Us', () => {
    expect(dataDeletionSource).toMatch(/sign in to PostPilot and use/)
    expect(dataDeletionSource).toMatch(/Contact Us/)
  })
})
