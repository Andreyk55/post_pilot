import { useMemo } from 'react'
import type { ConnectedPage } from '../types/meta'

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
  /** Currently selected platform IDs */
  selectedPlatforms: string[]
  /** List of connected Facebook pages */
  connectedPages: ConnectedPage[]
  /** Whether a Facebook account is connected */
  isAccountConnected: boolean
  /** Selected Facebook page ID */
  selectedPageId: string
  /** Whether pages are currently being loaded */
  loadingPages: boolean
}

/**
 * Hook to determine if the composer should be enabled based on platform and connection state.
 *
 * The composer is enabled only when:
 * 1. A platform is selected AND
 * 2. For platforms that require a target (Facebook), a connected target is selected AND
 * 3. That target exists in the fetched connected targets list
 *
 * When disabled, all composer inputs, AI features, and scheduling controls should be non-interactive.
 */
export function useComposerEnabled({
  selectedPlatforms,
  connectedPages,
  isAccountConnected,
  selectedPageId,
  loadingPages,
}: UseComposerEnabledOptions): ComposerEnabledState {
  return useMemo(() => {
    const hasPlatformSelected = selectedPlatforms.length > 0
    const selectedPlatform = selectedPlatforms[0]

    // Check if the selected platform requires a connected target
    const platformRequiresTarget = selectedPlatform === 'facebook'

    // Check if we have a valid target for platforms that need one
    let hasValidTarget = true
    let disabledReason: string | null = null
    let disabledMessage: string | null = null

    // Check if selected platform is not yet implemented
    // Instagram, Twitter, and LinkedIn are coming soon
    const isInstagramSelected = selectedPlatform === 'instagram'
    const isTwitterSelected = selectedPlatform === 'twitter'
    const isLinkedInSelected = selectedPlatform === 'linkedin'
    const isPlatformNotImplemented = isInstagramSelected || isTwitterSelected || isLinkedInSelected

    if (!hasPlatformSelected) {
      hasValidTarget = false
      disabledReason = 'no_platform'
      disabledMessage = 'Select a platform to start creating your post.'
    } else if (isPlatformNotImplemented) {
      // Instagram, Twitter, and LinkedIn are coming soon
      hasValidTarget = false
      disabledReason = 'platform_not_implemented'
      const platformName = isInstagramSelected ? 'Instagram' : isTwitterSelected ? 'Twitter/X' : 'LinkedIn'
      disabledMessage = `${platformName} integration coming soon.`
    } else if (platformRequiresTarget) {
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
  }, [selectedPlatforms, connectedPages, isAccountConnected, selectedPageId, loadingPages])
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
