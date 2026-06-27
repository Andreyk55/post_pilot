import { config } from '../config/appConfig'

const API_URL = config.apiBaseUrl

/**
 * Support category values. These mirror the backend `SupportCategory` enum names
 * exactly (sent/serialized by name). Category is optional — omitting it is a valid
 * "General question".
 */
export type SupportCategory =
  | 'General'
  | 'AccountIssue'
  | 'MetaConnection'
  | 'InstagramPublishing'
  | 'DataDeletion'
  | 'Billing'
  | 'BugReport'
  | 'FeatureRequest'

export interface SupportCategoryOption {
  value: SupportCategory
  label: string
}

/** Options for the optional category dropdown, in display order. */
export const SUPPORT_CATEGORIES: SupportCategoryOption[] = [
  { value: 'General', label: 'General question' },
  { value: 'AccountIssue', label: 'Account issue' },
  { value: 'MetaConnection', label: 'Meta/Facebook connection' },
  { value: 'InstagramPublishing', label: 'Instagram publishing' },
  { value: 'DataDeletion', label: 'Data deletion' },
  { value: 'Billing', label: 'Billing / subscription' },
  { value: 'BugReport', label: 'Bug report' },
  { value: 'FeatureRequest', label: 'Feature request' },
]

// Mirror the backend ValidationLimits.Support* caps.
export const SUPPORT_SUBJECT_MAX_LENGTH = 200
export const SUPPORT_MESSAGE_MAX_LENGTH = 5000

export interface SupportContactResponse {
  id: string
  status: string
  createdAt: string
}

export interface SendContactMessageInput {
  category?: SupportCategory | null
  subject: string
  message: string
}

/**
 * True only when both subject and message have non-whitespace content. Used to gate
 * the "Send message" button. Mirrors the backend's required-after-trim rule.
 */
export function isContactFormValid(subject: string, message: string): boolean {
  return subject.trim().length > 0 && message.trim().length > 0
}

export const supportApi = {
  /**
   * Sends an authenticated support message from the in-app Contact Us form.
   *
   * The body is built explicitly so ONLY category/subject/message are ever sent — we
   * deliberately never include a userId/accountId/email. The backend derives the sender
   * from the session, so a tampered client cannot attribute a message to someone else.
   * Category is included only when chosen (a "General question" otherwise).
   */
  async sendContactMessage(input: SendContactMessageInput): Promise<SupportContactResponse> {
    const body: { subject: string; message: string; category?: SupportCategory } = {
      subject: input.subject.trim(),
      message: input.message.trim(),
    }
    if (input.category) {
      body.category = input.category
    }

    const response = await fetch(`${API_URL}/support/contact`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })

    if (!response.ok) {
      throw new Error(`Failed to send support message (${response.status})`)
    }
    return (await response.json()) as SupportContactResponse
  },
}
