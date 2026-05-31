import { useMemo } from 'react'
import type { ConnectedPage, ConnectedInstagramAccount } from '../types/meta'

export interface ComposerEnabledState {
  /** Whether the composer is enabled and interactive */
  isEnabled: boolean
  /** Reason why composer is disabled (null if enabled) */
  disabledReason: string | null
  /** Platform-specific message for the disabled banner */
  disabledMessage: string | null
  /** Whether a platform is selected */
  hasPlatformSelected: boolean
  /** Whether the selected platform requires a target (e.g., Facebook needs a page) */
  platformRequiresTarget: boolean
  /** Whether a valid target is selected for platforms that need one */
  hasValidTarget: boolean
}

interface UseComposerEnabledOptions {
  /**
   * Whether the signed-in user has a valid selected workspace. Defense-in-depth:
   * the backend rejects workspace-scoped operations without one, and the
   * WorkspaceGuard modal blocks the UI, but we also hard-disable the composer so
   * its actions can never fire. When false this is the highest-priority disable
   * reason. Optional for backwards compatibility; defaults to true (enabled).
   */
  hasWorkspace?: boolean
  /** Currently selected platform IDs */
  selectedPlatforms: string[]
  /** List of connected Facebook pages */
  connectedPages: ConnectedPage[]
  /** Whether a Facebook/Meta account is connected */
  isAccountConnected: boolean
  /** Selected Facebook page ID */
  selectedPageId: string
  /** Whether pages are currently being loaded */
  loadingPages: boolean
  /** List of connected Instagram business accounts */
  connectedInstagramAccounts: ConnectedInstagramAccount[]
  /** Selected Instagram account ID */
  selectedInstagramAccountId: string
}

/** Disabled reason / message used when no workspace is selected. */
export const NO_WORKSPACE_DISABLED_REASON = 'no_workspace'
export const NO_WORKSPACE_DISABLED_MESSAGE = 'Select a workspace before continuing.'

/**
 * Hook to determine if the composer should be enabled based on platform and connection state.
 *
 * The composer is enabled only when:
 * 1. The user has a valid selected workspace AND
 * 2. A platform is selected AND
 * 3. For platforms that require a target (Facebook/Instagram), a connected target is selected AND
 * 4. That target exists in the fetched connected targets list
 *
 * When disabled, all composer inputs, AI features, and scheduling controls should be non-interactive.
 */
export function useComposerEnabled(options: UseComposerEnabledOptions): ComposerEnabledState {
  const {
    hasWorkspace = true,
    selectedPlatforms,
    connectedPages,
    isAccountConnected,
    selectedPageId,
    loadingPages,
    connectedInstagramAccounts,
    selectedInstagramAccountId,
  } = options
  return useMemo(
    () => computeComposerEnabled(options),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- deps are the option fields, not the object identity
    [hasWorkspace, selectedPlatforms, connectedPages, isAccountConnected, selectedPageId, loadingPages, connectedInstagramAccounts, selectedInstagramAccountId],
  )
}

/**
 * Pure composer-enablement decision. Extracted from the hook so the rules can be
 * unit-tested without a React renderer. The hook is a thin memoized wrapper.
 */
export function computeComposerEnabled({
  hasWorkspace = true,
  selectedPlatforms,
  connectedPages,
  isAccountConnected,
  selectedPageId,
  loadingPages,
  connectedInstagramAccounts,
  selectedInstagramAccountId,
}: UseComposerEnabledOptions): ComposerEnabledState {
  const hasPlatformSelected = selectedPlatforms.length > 0
  const selectedPlatform = selectedPlatforms[0]

  // Check if the selected platform requires a connected target
  const platformRequiresTarget = selectedPlatform === 'facebook' || selectedPlatform === 'instagram'

  // No workspace selected is the highest-priority block: without one, every
  // workspace-scoped action (upload, schedule, publish) would be rejected by
  // the backend. Short-circuit before any platform/target checks so the reason
  // is unambiguous.
  if (!hasWorkspace) {
    return {
      isEnabled: false,
      disabledReason: NO_WORKSPACE_DISABLED_REASON,
      disabledMessage: NO_WORKSPACE_DISABLED_MESSAGE,
      hasPlatformSelected,
      platformRequiresTarget,
      hasValidTarget: false,
    }
  }

  // Check if we have a valid target for platforms that need one
  let hasValidTarget = true
  let disabledReason: string | null = null
  let disabledMessage: string | null = null

  // Check if selected platform is not yet implemented
  // Twitter and LinkedIn are coming soon (Instagram is now implemented)
  const isTwitterSelected = selectedPlatform === 'twitter'
  const isLinkedInSelected = selectedPlatform === 'linkedin'
  const isPlatformNotImplemented = isTwitterSelected || isLinkedInSelected

  if (!hasPlatformSelected) {
    hasValidTarget = false
    disabledReason = 'no_platform'
    disabledMessage = 'Select a platform to start creating your post.'
  } else if (isPlatformNotImplemented) {
    hasValidTarget = false
    disabledReason = 'platform_not_implemented'
    const platformName = isTwitterSelected ? 'Twitter/X' : 'LinkedIn'
    disabledMessage = `${platformName} integration coming soon.`
  } else if (selectedPlatform === 'instagram') {
    // Instagram requires an IG business account selection
    if (loadingPages) {
      hasValidTarget = false
      disabledReason = 'loading'
      disabledMessage = 'Loading connected accounts...'
    } else if (!isAccountConnected) {
      hasValidTarget = false
      disabledReason = 'no_account_connected'
      disabledMessage = 'No Meta account connected. Connect your account to enable Instagram scheduling.'
    } else if (connectedInstagramAccounts.length === 0) {
      hasValidTarget = false
      disabledReason = 'no_ig_accounts_connected'
      disabledMessage = 'No Instagram Business Account connected. Link an Instagram account in Connected Accounts.'
    } else if (!selectedInstagramAccountId) {
      hasValidTarget = false
      disabledReason = 'no_ig_account_selected'
      disabledMessage = 'Select an Instagram account above to enable scheduling and AI features.'
    } else {
      const accountExists = connectedInstagramAccounts.some(a => a.id === selectedInstagramAccountId)
      if (!accountExists) {
        hasValidTarget = false
        disabledReason = 'ig_account_not_found'
        disabledMessage = 'Selected Instagram account is no longer connected. Please select a different account or reconnect in Connected Accounts.'
      }
    }
  } else if (selectedPlatform === 'facebook') {
    // Facebook requires a page selection
    if (loadingPages) {
      hasValidTarget = false
      disabledReason = 'loading'
      disabledMessage = 'Loading connected pages...'
    } else if (!isAccountConnected) {
      hasValidTarget = false
      disabledReason = 'no_account_connected'
      disabledMessage = 'No Facebook account connected. Connect your account to enable scheduling and AI features.'
    } else if (connectedPages.length === 0) {
      hasValidTarget = false
      disabledReason = 'no_pages_connected'
      disabledMessage = 'No Facebook Page connected. Connect a page to enable scheduling and AI features.'
    } else if (!selectedPageId) {
      hasValidTarget = false
      disabledReason = 'no_page_selected'
      disabledMessage = 'Select a Facebook Page above to enable scheduling and AI features.'
    } else {
      // Verify the selected page exists in the connected pages list
      const pageExists = connectedPages.some(page => page.id === selectedPageId)
      if (!pageExists) {
        hasValidTarget = false
        disabledReason = 'page_not_found'
        disabledMessage = 'Selected page is no longer connected. Please select a different page or reconnect in Connected Accounts.'
      }
    }
  }

  const isEnabled = hasPlatformSelected && hasValidTarget

  return {
    isEnabled,
    disabledReason,
    disabledMessage,
    hasPlatformSelected,
    platformRequiresTarget,
    hasValidTarget,
  }
}

/**
 * Get the platform display name for the disabled message
 */
export function getPlatformNameForMessage(platformId: string): string {
  const names: Record<string, string> = {
    facebook: 'Facebook',
    instagram: 'Instagram',
    twitter: 'Twitter/X',
    linkedin: 'LinkedIn',
  }
  return names[platformId] || platformId
}
