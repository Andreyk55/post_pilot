import { describe, it, expect } from 'vitest'
import { MetaApiError } from '../../api/meta'
import type { FacebookPage, MetaOAuthCallbackResponse } from '../../types/meta'
import {
  resolveOAuthCallbackError,
  resolveOAuthCallbackSuccess,
} from './oauthCallbackOutcome'

const PERMANENT_OWNERSHIP_MESSAGE =
  'This provider account is already permanently linked to another workspace. ' +
  'To use a different account, create or select another workspace.'

describe('resolveOAuthCallbackSuccess', () => {
  it('opens page selection with the returned pages + temp token', () => {
    const pages: FacebookPage[] = [
      { id: 'p1', name: 'Page One' } as FacebookPage,
    ]
    const response: MetaOAuthCallbackResponse = { tempToken: 'temp-123', pages }

    const outcome = resolveOAuthCallbackSuccess(response)

    expect(outcome.kind).toBe('pages')
    if (outcome.kind === 'pages') {
      expect(outcome.tempToken).toBe('temp-123')
      expect(outcome.pages).toEqual(pages)
    }
  })
})

describe('resolveOAuthCallbackError', () => {
  it('on a 409 ownership rejection, shows the EXACT server message and does NOT open page selection', () => {
    const err = new MetaApiError(PERMANENT_OWNERSHIP_MESSAGE, 409)

    const outcome = resolveOAuthCallbackError(err)

    expect(outcome.kind).toBe('rejected')
    if (outcome.kind === 'rejected') {
      // Server message surfaced verbatim — the user sees the permanent-ownership text.
      expect(outcome.message).toBe(PERMANENT_OWNERSHIP_MESSAGE)
      // It must never instruct the user to disconnect from the other workspace.
      expect(outcome.message).not.toContain('Disconnect')
    }
    // 'rejected' (not 'pages') means the wizard does not open the page-selection step.
    expect(outcome.kind).not.toBe('pages')
  })

  it('falls back to a generic message for non-409 errors', () => {
    const outcome = resolveOAuthCallbackError(new MetaApiError('boom', 500))

    expect(outcome.kind).toBe('rejected')
    if (outcome.kind === 'rejected') {
      expect(outcome.message).toBe('Failed to connect to Meta. Please try again.')
    }
  })

  it('falls back to a generic message for a plain Error', () => {
    const outcome = resolveOAuthCallbackError(new Error('network down'))

    expect(outcome.kind).toBe('rejected')
    if (outcome.kind === 'rejected') {
      expect(outcome.message).toBe('Failed to connect to Meta. Please try again.')
    }
  })
})
