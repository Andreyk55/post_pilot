import { useState } from 'react'
import {
  supportApi,
  isContactFormValid,
  SUPPORT_CATEGORIES,
  SUPPORT_SUBJECT_MAX_LENGTH,
  SUPPORT_MESSAGE_MAX_LENGTH,
  type SupportCategory,
} from '../api/contact'
import './ContactPage.css'

/**
 * Authenticated in-app "Contact Us" support form. Rendered only inside the logged-in app
 * (reached via the sidebar) — there is no public route. The backend derives the sender
 * from the session, so this form never collects or sends a userId/accountId/email.
 */
export function ContactPage() {
  const [category, setCategory] = useState<SupportCategory | ''>('')
  const [subject, setSubject] = useState('')
  const [message, setMessage] = useState('')
  const [sending, setSending] = useState(false)
  const [sent, setSent] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const canSend = isContactFormValid(subject, message) && !sending

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!canSend) return

    setSending(true)
    setError(null)
    try {
      await supportApi.sendContactMessage({
        category: category || null,
        subject,
        message,
      })
      // Reset the form so a resolved success state can't be re-submitted as-is.
      setSent(true)
      setCategory('')
      setSubject('')
      setMessage('')
    } catch {
      setError('We could not send your message right now. Please try again.')
    } finally {
      setSending(false)
    }
  }

  if (sent) {
    return (
      <div className="contact-page">
        <h1>Contact Us</h1>
        <div className="contact-success" role="status">
          Thanks, your message was sent. We will review it and get back to you.
        </div>
        <button
          type="button"
          className="contact-secondary-button"
          onClick={() => setSent(false)}
        >
          Send another message
        </button>
      </div>
    )
  }

  return (
    <div className="contact-page">
      <h1>Contact Us</h1>
      <p className="contact-lead">
        Need help with Publish Harbor? Send us a message and we’ll review it.
      </p>

      <form className="contact-form" onSubmit={handleSubmit}>
        <label className="contact-field" htmlFor="contact-category">
          <span className="contact-label">Category (optional)</span>
          <select
            id="contact-category"
            className="contact-select"
            value={category}
            onChange={(e) => setCategory(e.target.value as SupportCategory | '')}
            disabled={sending}
          >
            <option value="">General question</option>
            {SUPPORT_CATEGORIES.filter((c) => c.value !== 'General').map((c) => (
              <option key={c.value} value={c.value}>
                {c.label}
              </option>
            ))}
          </select>
        </label>

        <label className="contact-field" htmlFor="contact-subject">
          <span className="contact-label">Subject</span>
          <input
            id="contact-subject"
            type="text"
            className="contact-input"
            value={subject}
            onChange={(e) => setSubject(e.target.value)}
            maxLength={SUPPORT_SUBJECT_MAX_LENGTH}
            placeholder="How can we help?"
            disabled={sending}
            required
          />
        </label>

        <label className="contact-field" htmlFor="contact-message">
          <span className="contact-label">Message</span>
          <textarea
            id="contact-message"
            className="contact-textarea"
            value={message}
            onChange={(e) => setMessage(e.target.value)}
            maxLength={SUPPORT_MESSAGE_MAX_LENGTH}
            rows={8}
            placeholder="Tell us what’s going on…"
            disabled={sending}
            required
          />
        </label>

        {error && <div className="contact-error" role="alert">{error}</div>}

        <button type="submit" className="contact-submit-button" disabled={!canSend}>
          {sending ? 'Sending…' : 'Send message'}
        </button>
      </form>
    </div>
  )
}
