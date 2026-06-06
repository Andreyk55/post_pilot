import { MetaApiError } from '../../api/meta'
import type { FacebookPage, MetaOAuthCallbackResponse } from '../../types/meta'

/**
 * Pure decision for what the Meta connection wizard should do after the OAuth
 * callback resolves (or fails). Kept separate from the React component so the
 * permanent-ownership rejection behavior can be unit-tested without a DOM.
 *
 * - On success → open the page-selection step with the returned pages + temp token.
 * - On a 409 (permanent ownership / binding violation) → go to the 'rejected' step,
 *   surface the EXACT server message, and carry no pages/temp token so the wizard
 *   never shows page selection.
 * - On any other error → 'rejected' with a generic message.
 */
export type OAuthCallbackOutcome =
  | { kind: 'pages'; tempToken: string; pages: FacebookPage[] }
  | { kind: 'rejected'; message: string }

export function resolveOAuthCallbackSuccess(
  response: MetaOAuthCallbackResponse
): OAuthCallbackOutcome {
  return { kind: 'pages', tempToken: response.tempToken, pages: response.pages }
}

export function resolveOAuthCallbackError(err: unknown): OAuthCallbackOutcome {
  // 409 = permanent ownership: this Meta account belongs to another workspace, or
  // this workspace is bound to a different account. The backend rejects BEFORE
  // returning any pages, so we must NOT open page selection — show the exact server
  // message instead.
  if (err instanceof MetaApiError && err.status === 409) {
    return { kind: 'rejected', message: err.message }
  }
  return { kind: 'rejected', message: 'Failed to connect to Meta. Please try again.' }
}
