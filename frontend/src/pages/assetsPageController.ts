import { metaApi } from '../api/meta'
import type {
  MetaConnection,
  ConnectedPage,
  FacebookPage,
  InstagramEligibilityDto,
  MetaConnectionResponse,
  InstagramDiscoveryResponse,
  MetaUpdatePagesRequest,
} from '../types/meta'
import { hasUnpromotedLinkedInstagram } from '../utils/instagramPromotion'

export const CONNECT_PAGE_ERROR = 'Failed to connect page. Please try again.'
export const DISCONNECT_PAGE_ERROR = 'Failed to disconnect page. Please try again.'

/**
 * The provider asset calls the Publishing Assets page depends on. Injected so the
 * connect / refresh flow is unit-testable without a DOM or a real network, and so
 * tests can assert exactly which calls happen (and in which order).
 */
export interface AssetsPageApi {
  getConnection: () => Promise<MetaConnectionResponse>
  getAvailablePages: () => Promise<{ pages: FacebookPage[] }>
  getInstagramEligibility: () => Promise<InstagramDiscoveryResponse>
  updateConnection: (request: MetaUpdatePagesRequest) => Promise<unknown>
  refreshAssets: () => Promise<unknown>
}

export interface AssetsPageControllerDeps {
  /** Provider asset API. Defaults to the real {@link metaApi} endpoints. */
  api?: AssetsPageApi
  setMetaConnection: (connection: MetaConnection | null) => void
  setAvailablePages: (pages: FacebookPage[]) => void
  setIgEligibility: (eligibility: InstagramEligibilityDto[]) => void
  /**
   * GLOBAL full-page "Loading assets..." state. ONLY the very first page load may
   * toggle this — connect/disconnect/refresh must never blank the page.
   */
  setInitialLoading: (loading: boolean) => void
  /** In-place "Refreshing..." badge; keeps the existing page/card layout visible. */
  setRefreshing: (refreshing: boolean) => void
  addConnectingPage: (pageId: string) => void
  removeConnectingPage: (pageId: string) => void
  addDisconnectingPage: (pageId: string) => void
  removeDisconnectingPage: (pageId: string) => void
  onSuccess: (message: string) => void
  onError: (message: string) => void
}

/**
 * Owns the Publishing Assets load / connect / disconnect wiring so the "connect a
 * Facebook Page" flow is unit-testable and the global-loading bug can't regress.
 *
 * Contract:
 *  - `loadInitial` is the ONLY method that may show the full-page "Loading
 *    assets..." state (`setInitialLoading`). It runs once on mount.
 *  - `connectPage` / `disconnectPage` / the auto-repair reload refresh IN PLACE:
 *    they only toggle the per-row (connecting/disconnecting) and "Refreshing..."
 *    states, so the existing Facebook/Instagram sections stay visible.
 *  - The per-row loading state is always cleared in `finally`, even if the
 *    in-place refresh throws after the connect/disconnect itself succeeded.
 */
export function createAssetsPageController(deps: AssetsPageControllerDeps) {
  const api: AssetsPageApi = deps.api ?? {
    getConnection: metaApi.getConnection,
    getAvailablePages: metaApi.getAvailablePages,
    getInstagramEligibility: metaApi.getInstagramEligibility,
    updateConnection: metaApi.updateConnection,
    refreshAssets: metaApi.refreshAssets,
  }

  // Guards a single auto-repair attempt per controller lifetime (i.e. per page
  // mount) so we never loop if the backend repair can't promote an IG (e.g. a
  // transient discovery failure).
  let repairAttempted = false

  const loadAvailablePages = async (connection: MetaConnection): Promise<void> => {
    try {
      deps.setRefreshing(true)
      const { pages } = await api.getAvailablePages()
      deps.setAvailablePages(pages)

      // Instagram eligibility (per-page breakdown) is supplementary — never let a
      // failure here abort the page load.
      try {
        const eligibility = await api.getInstagramEligibility()
        deps.setIgEligibility(eligibility.pages)

        // Self-heal: a connected Page whose Meta-linked IG (eligibility "Connected")
        // is missing from the connected IG asset list is the production bug — IG was
        // discovered but never promoted to a connected publishable asset, which blocks
        // the composer. Trigger the idempotent backend repair ONCE, then reload IN
        // PLACE so the promoted IG shows up everywhere (Assets, SchedulePost, validation).
        if (!repairAttempted) {
          const needsRepair = hasUnpromotedLinkedInstagram(
            connection.pages,
            connection.instagramAccounts,
            eligibility.pages,
          )
          if (needsRepair) {
            repairAttempted = true
            try {
              await api.refreshAssets()
            } catch (repairErr) {
              console.error('Failed to auto-repair linked Instagram accounts:', repairErr)
            }
            // Reload connection state regardless; on success the IG is now connected.
            // Background self-heal, so swallow any failure rather than blanking the page.
            try {
              await fetchAndApplyConnection()
            } catch (reloadErr) {
              console.error('Failed to reload after auto-repair:', reloadErr)
            }
            return
          }
        }
      } catch {
        // Non-critical: eligibility info is supplementary
      }
    } catch (err) {
      console.error('Failed to load available pages:', err)
    } finally {
      deps.setRefreshing(false)
    }
  }

  /**
   * Fetches the connection and applies it (plus available pages / eligibility).
   * THROWS on connection-fetch failure so callers can decide how to present it —
   * `loadInitial` shows the not-connected state, `connectPage` surfaces an error
   * toast while keeping the existing UI visible.
   */
  const fetchAndApplyConnection = async (): Promise<void> => {
    const response = await api.getConnection()
    if (response.isConnected && response.connection) {
      deps.setMetaConnection(response.connection)
      await loadAvailablePages(response.connection)
    } else {
      deps.setMetaConnection(null)
    }
  }

  return {
    /**
     * First mount only: may show the full-page "Loading assets..." state.
     * Swallows failures into the not-connected state so the app never crashes.
     */
    async loadInitial(): Promise<void> {
      try {
        deps.setInitialLoading(true)
        await fetchAndApplyConnection()
      } catch (err) {
        console.error('Failed to load Meta connection:', err)
        deps.setMetaConnection(null)
      } finally {
        deps.setInitialLoading(false)
      }
    },

    /**
     * Background, in-place refresh that keeps the existing page/card layout
     * visible. Swallows failures so a transient blip doesn't wipe the page.
     */
    async refresh(): Promise<void> {
      try {
        await fetchAndApplyConnection()
      } catch (err) {
        console.error('Failed to refresh Meta connection:', err)
      }
    },

    /**
     * Connect a single Facebook Page. Only the clicked row enters a loading state;
     * the page is refreshed IN PLACE (never the global loading state) so the
     * existing sections stay visible and the newly connected Page — plus any linked
     * Instagram account — appears. The per-row state is always cleared in `finally`.
     */
    async connectPage(page: FacebookPage, connection: MetaConnection): Promise<void> {
      deps.addConnectingPage(page.id)
      try {
        const currentPageIds = connection.pages.map(p => p.pageId)
        const currentIgIds = connection.instagramAccounts.map(ig => ig.igBusinessId)

        await api.updateConnection({
          selectedPageIds: [...currentPageIds, page.id],
          selectedInstagramIds: currentIgIds,
        })

        // In-place refresh — never `setInitialLoading` — so the card layout stays put.
        await fetchAndApplyConnection()
        deps.onSuccess(`${page.name} connected.`)
      } catch (err) {
        console.error('Failed to connect page:', err)
        deps.onError(CONNECT_PAGE_ERROR)
      } finally {
        // Always clear the per-row loading state, even if the refresh above threw
        // after the connect itself succeeded.
        deps.removeConnectingPage(page.id)
      }
    },

    /**
     * Disconnect a connected Facebook Page (and any Instagram accounts linked to it).
     * Same in-place semantics as {@link connectPage}.
     */
    async disconnectPage(page: ConnectedPage, connection: MetaConnection): Promise<void> {
      deps.addDisconnectingPage(page.pageId)
      try {
        const currentPageIds = connection.pages
          .filter(p => p.pageId !== page.pageId)
          .map(p => p.pageId)
        // Also remove Instagram accounts linked to this page.
        const currentIgIds = connection.instagramAccounts
          .filter(ig => ig.pageId !== page.pageId)
          .map(ig => ig.igBusinessId)

        await api.updateConnection({
          selectedPageIds: currentPageIds,
          selectedInstagramIds: currentIgIds,
        })

        await fetchAndApplyConnection()
        deps.onSuccess(`${page.name} disconnected.`)
      } catch (err) {
        console.error('Failed to disconnect page:', err)
        deps.onError(DISCONNECT_PAGE_ERROR)
      } finally {
        deps.removeDisconnectingPage(page.pageId)
      }
    },
  }
}

export type AssetsPageController = ReturnType<typeof createAssetsPageController>
