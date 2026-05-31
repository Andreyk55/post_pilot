import { describe, it, expect } from 'vitest'
import type { ConnectedPage, ConnectedInstagramAccount } from '../types/meta'
import {
  computeComposerEnabled,
  NO_WORKSPACE_DISABLED_REASON,
  NO_WORKSPACE_DISABLED_MESSAGE,
} from './useComposerEnabled'

const PAGE: ConnectedPage = {
  id: 'page-1',
  name: 'Test Page',
} as ConnectedPage

const IG: ConnectedInstagramAccount = {
  id: 'ig-1',
  username: 'test_ig',
} as ConnectedInstagramAccount

/** A fully-valid Facebook composer config (workspace + connected page selected). */
function validFacebookOptions(overrides = {}) {
  return {
    hasWorkspace: true,
    selectedPlatforms: ['facebook'],
    connectedPages: [PAGE],
    isAccountConnected: true,
    selectedPageId: 'page-1',
    loadingPages: false,
    connectedInstagramAccounts: [] as ConnectedInstagramAccount[],
    selectedInstagramAccountId: '',
    ...overrides,
  }
}

describe('computeComposerEnabled — workspace gating', () => {
  it('disables all composer actions when no workspace is selected', () => {
    const state = computeComposerEnabled(validFacebookOptions({ hasWorkspace: false }))

    expect(state.isEnabled).toBe(false)
    expect(state.disabledReason).toBe(NO_WORKSPACE_DISABLED_REASON)
    expect(state.disabledMessage).toBe(NO_WORKSPACE_DISABLED_MESSAGE)
    expect(state.disabledMessage).toBe('Select a workspace before continuing.')
  })

  it('enables the composer normally when a workspace IS selected and target is valid', () => {
    const state = computeComposerEnabled(validFacebookOptions({ hasWorkspace: true }))

    expect(state.isEnabled).toBe(true)
    expect(state.disabledReason).toBeNull()
    expect(state.disabledMessage).toBeNull()
  })

  it('treats missing hasWorkspace as enabled (backwards compatible default)', () => {
    const opts = validFacebookOptions()
    // Strip hasWorkspace entirely to exercise the default.
    delete (opts as { hasWorkspace?: boolean }).hasWorkspace

    const state = computeComposerEnabled(opts)
    expect(state.isEnabled).toBe(true)
  })

  it('prioritizes the no-workspace reason over other disable reasons', () => {
    // No platform selected AND no workspace: workspace must win so the user is
    // steered to the real blocker first.
    const state = computeComposerEnabled(validFacebookOptions({
      hasWorkspace: false,
      selectedPlatforms: [],
    }))

    expect(state.isEnabled).toBe(false)
    expect(state.disabledReason).toBe(NO_WORKSPACE_DISABLED_REASON)
  })

  it('still surfaces platform/target reasons when a workspace IS selected', () => {
    // With a workspace but no connected page, the existing connection reason applies.
    const state = computeComposerEnabled(validFacebookOptions({
      hasWorkspace: true,
      connectedPages: [],
      selectedPageId: '',
    }))

    expect(state.isEnabled).toBe(false)
    expect(state.disabledReason).toBe('no_pages_connected')
  })
})

describe('computeComposerEnabled — Instagram with workspace', () => {
  it('enables Instagram when workspace + account + IG selection are present', () => {
    const state = computeComposerEnabled({
      hasWorkspace: true,
      selectedPlatforms: ['instagram'],
      connectedPages: [],
      isAccountConnected: true,
      selectedPageId: '',
      loadingPages: false,
      connectedInstagramAccounts: [IG],
      selectedInstagramAccountId: 'ig-1',
    })

    expect(state.isEnabled).toBe(true)
  })

  it('disables Instagram for no workspace before checking IG account state', () => {
    const state = computeComposerEnabled({
      hasWorkspace: false,
      selectedPlatforms: ['instagram'],
      connectedPages: [],
      isAccountConnected: false,
      selectedPageId: '',
      loadingPages: false,
      connectedInstagramAccounts: [],
      selectedInstagramAccountId: '',
    })

    expect(state.disabledReason).toBe(NO_WORKSPACE_DISABLED_REASON)
  })
})
