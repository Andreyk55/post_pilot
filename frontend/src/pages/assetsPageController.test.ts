import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import {
  createAssetsPageController,
  CONNECT_PAGE_ERROR,
  DISCONNECT_PAGE_ERROR,
  type AssetsPageApi,
} from './assetsPageController'
import type {
  MetaConnection,
  ConnectedPage,
  FacebookPage,
  MetaConnectionResponse,
  InstagramDiscoveryResponse,
} from '../types/meta'

/** A minimal connected connection with one existing Page and no linked IG. */
const baseConnection: MetaConnection = {
  id: 'conn-1',
  userId: 'user-1',
  accessToken: 'tok',
  tokenExpiresAt: '2099-01-01T00:00:00Z',
  connectedAt: '2024-01-01T00:00:00Z',
  pages: [
    { id: 'row-1', pageId: 'PAGE_1', name: 'Existing Page', accessToken: 'pt-1' },
  ],
  instagramAccounts: [],
}

/** The page the user clicks "Connect" on (available, not yet connected). */
const newPage: FacebookPage = { id: 'PAGE_2', name: 'Second Page' }

/** The already-connected page the user clicks "Disconnect" on. */
const connectedPage: ConnectedPage = baseConnection.pages[0]

const connectionResponse = (connection: MetaConnection): MetaConnectionResponse => ({
  isConnected: true,
  connection,
})

const emptyEligibility: InstagramDiscoveryResponse = {
  pages: [],
  totalPages: 0,
  linkedCount: 0,
}

/** A controllable promise so tests can assert in-flight state before resolution. */
function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

function makeApi(overrides: Partial<AssetsPageApi> = {}): AssetsPageApi {
  return {
    getConnection: vi.fn().mockResolvedValue(connectionResponse(baseConnection)),
    getAvailablePages: vi.fn().mockResolvedValue({ pages: [newPage] }),
    getInstagramEligibility: vi.fn().mockResolvedValue(emptyEligibility),
    updateConnection: vi.fn().mockResolvedValue({ connection: baseConnection }),
    refreshAssets: vi.fn().mockResolvedValue({ connection: baseConnection }),
    ...overrides,
  }
}

function makeController(api: AssetsPageApi) {
  const deps = {
    api,
    setMetaConnection: vi.fn(),
    setAvailablePages: vi.fn(),
    setIgEligibility: vi.fn(),
    setInitialLoading: vi.fn(),
    setRefreshing: vi.fn(),
    addConnectingPage: vi.fn(),
    removeConnectingPage: vi.fn(),
    addDisconnectingPage: vi.fn(),
    removeDisconnectingPage: vi.fn(),
    onSuccess: vi.fn(),
    onError: vi.fn(),
  }
  return { controller: createAssetsPageController(deps), deps }
}

beforeEach(() => {
  // Controller logs to console.error on swallowed failures; keep test output clean.
  vi.spyOn(console, 'error').mockImplementation(() => {})
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('createAssetsPageController — initial load', () => {
  it('shows the GLOBAL loading state on the first page load', async () => {
    const { controller, deps } = makeController(makeApi())

    await controller.loadInitial()

    // The full-page "Loading assets..." state is driven by setInitialLoading.
    expect(deps.setInitialLoading).toHaveBeenCalledWith(true)
    expect(deps.setInitialLoading).toHaveBeenLastCalledWith(false)
    expect(deps.setMetaConnection).toHaveBeenCalledWith(baseConnection)
  })

  it('clears the global loading state even when the connection fetch fails', async () => {
    const api = makeApi({ getConnection: vi.fn().mockRejectedValue(new Error('boom')) })
    const { controller, deps } = makeController(api)

    await controller.loadInitial()

    expect(deps.setInitialLoading).toHaveBeenLastCalledWith(false)
    // Falls back to the not-connected state instead of crashing.
    expect(deps.setMetaConnection).toHaveBeenCalledWith(null)
  })
})

describe('createAssetsPageController — connectPage keeps the page visible', () => {
  it('never toggles the GLOBAL loading state during connect', async () => {
    const { controller, deps } = makeController(makeApi())

    await controller.connectPage(newPage, baseConnection)

    // The existing Facebook/Instagram sections must stay mounted, so the full-page
    // loader is never triggered by a connect action.
    expect(deps.setInitialLoading).not.toHaveBeenCalled()
    // And the connection is never cleared to null on success (sections remain).
    expect(deps.setMetaConnection).not.toHaveBeenCalledWith(null)
  })

  it('enters a per-row loading state only for the clicked page while in flight', async () => {
    const update = deferred<unknown>()
    const api = makeApi({ updateConnection: vi.fn().mockReturnValue(update.promise) })
    const { controller, deps } = makeController(api)

    const pending = controller.connectPage(newPage, baseConnection)

    // Button flips to "Connecting..." immediately (before the request resolves)…
    expect(deps.addConnectingPage).toHaveBeenCalledWith('PAGE_2')
    expect(deps.removeConnectingPage).not.toHaveBeenCalled()

    // …and returns to "Connect" once the whole flow settles.
    update.resolve({})
    await pending
    expect(deps.removeConnectingPage).toHaveBeenCalledWith('PAGE_2')
  })

  it('refreshes assets in place and shows a success toast after connecting', async () => {
    const api = makeApi()
    const { controller, deps } = makeController(api)

    await controller.connectPage(newPage, baseConnection)

    // Sends the current pages plus the newly connected one.
    expect(api.updateConnection).toHaveBeenCalledWith({
      selectedPageIds: ['PAGE_1', 'PAGE_2'],
      selectedInstagramIds: [],
    })
    // Refreshes in place (re-reads the connection) rather than blanking the page.
    expect(api.getConnection).toHaveBeenCalledTimes(1)
    expect(deps.setMetaConnection).toHaveBeenCalledWith(baseConnection)
    expect(deps.onSuccess).toHaveBeenCalledWith('Second Page connected.')
    expect(deps.onError).not.toHaveBeenCalled()
  })

  it('clears the per-row state and shows an error when the connect call fails', async () => {
    const api = makeApi({
      updateConnection: vi.fn().mockRejectedValue(new Error('network')),
    })
    const { controller, deps } = makeController(api)

    await controller.connectPage(newPage, baseConnection)

    expect(deps.onError).toHaveBeenCalledWith(CONNECT_PAGE_ERROR)
    expect(deps.onSuccess).not.toHaveBeenCalled()
    // Button returns to "Connect".
    expect(deps.removeConnectingPage).toHaveBeenCalledWith('PAGE_2')
    // Never blanks the page.
    expect(deps.setInitialLoading).not.toHaveBeenCalled()
    // No refresh attempted after the connect call itself failed.
    expect(api.getConnection).not.toHaveBeenCalled()
  })

  it('clears the per-row state even when the refresh fails AFTER connect succeeds', async () => {
    // updateConnection succeeds, but the follow-up in-place refresh throws.
    const api = makeApi({
      getConnection: vi.fn().mockRejectedValue(new Error('refresh failed')),
    })
    const { controller, deps } = makeController(api)

    await controller.connectPage(newPage, baseConnection)

    // The finally block still runs — the button doesn't get stuck on "Connecting...".
    expect(deps.removeConnectingPage).toHaveBeenCalledWith('PAGE_2')
    // A visible error is surfaced and no false success toast is shown.
    expect(deps.onError).toHaveBeenCalledWith(CONNECT_PAGE_ERROR)
    expect(deps.onSuccess).not.toHaveBeenCalled()
    // Still never blanks the existing UI.
    expect(deps.setInitialLoading).not.toHaveBeenCalled()
  })
})

describe('createAssetsPageController — disconnectPage keeps the page visible', () => {
  it('never renders the full-page "Loading assets..." state during disconnect', async () => {
    const { controller, deps } = makeController(makeApi())

    await controller.disconnectPage(connectedPage, baseConnection)

    // The full-page loader is driven solely by setInitialLoading; disconnect must
    // never touch it, so the Publishing Assets layout stays mounted throughout.
    expect(deps.setInitialLoading).not.toHaveBeenCalled()
    // And the connection is never cleared to null on success (sections remain).
    expect(deps.setMetaConnection).not.toHaveBeenCalledWith(null)
  })

  it('enters a per-row disconnecting state only for the clicked page while in flight', async () => {
    const update = deferred<unknown>()
    const api = makeApi({ updateConnection: vi.fn().mockReturnValue(update.promise) })
    const { controller, deps } = makeController(api)

    const pending = controller.disconnectPage(connectedPage, baseConnection)

    // Only the clicked row shows a spinner immediately (before the request resolves)…
    expect(deps.addDisconnectingPage).toHaveBeenCalledWith('PAGE_1')
    expect(deps.removeDisconnectingPage).not.toHaveBeenCalled()
    // …and the global loader is never involved.
    expect(deps.setInitialLoading).not.toHaveBeenCalled()

    // …and the row clears once the whole flow settles.
    update.resolve({})
    await pending
    expect(deps.removeDisconnectingPage).toHaveBeenCalledWith('PAGE_1')
  })

  it('refreshes assets in place and shows a success toast after disconnecting', async () => {
    const api = makeApi()
    const { controller, deps } = makeController(api)

    await controller.disconnectPage(connectedPage, baseConnection)

    // Sends the remaining pages (the disconnected one removed).
    expect(api.updateConnection).toHaveBeenCalledWith({
      selectedPageIds: [],
      selectedInstagramIds: [],
    })
    // Refreshes in place (re-reads the connection) rather than blanking the page.
    expect(api.getConnection).toHaveBeenCalledTimes(1)
    expect(deps.onSuccess).toHaveBeenCalledWith('Existing Page disconnected.')
    expect(deps.onError).not.toHaveBeenCalled()
  })

  it('clears the per-row state and shows an error when the disconnect call fails', async () => {
    const api = makeApi({
      updateConnection: vi.fn().mockRejectedValue(new Error('network')),
    })
    const { controller, deps } = makeController(api)

    await controller.disconnectPage(connectedPage, baseConnection)

    expect(deps.onError).toHaveBeenCalledWith(DISCONNECT_PAGE_ERROR)
    expect(deps.onSuccess).not.toHaveBeenCalled()
    expect(deps.removeDisconnectingPage).toHaveBeenCalledWith('PAGE_1')
    expect(deps.setInitialLoading).not.toHaveBeenCalled()
    expect(api.getConnection).not.toHaveBeenCalled()
  })

  it('clears the per-row state even when the refresh fails AFTER disconnect succeeds', async () => {
    const api = makeApi({
      getConnection: vi.fn().mockRejectedValue(new Error('refresh failed')),
    })
    const { controller, deps } = makeController(api)

    await controller.disconnectPage(connectedPage, baseConnection)

    // The finally block still runs — the row doesn't get stuck spinning.
    expect(deps.removeDisconnectingPage).toHaveBeenCalledWith('PAGE_1')
    expect(deps.onError).toHaveBeenCalledWith(DISCONNECT_PAGE_ERROR)
    expect(deps.onSuccess).not.toHaveBeenCalled()
    expect(deps.setInitialLoading).not.toHaveBeenCalled()
  })
})

describe('createAssetsPageController — setInitialLoading is exclusive to loadInitial', () => {
  it('only loadInitial toggles the global loading flag', async () => {
    // loadInitial DOES drive the global loader…
    const first = makeController(makeApi())
    await first.controller.loadInitial()
    expect(first.deps.setInitialLoading).toHaveBeenCalledWith(true)

    // …but no other entry point does. Run every non-initial path on a fresh
    // controller and assert the global loader was never touched.
    const refreshCtl = makeController(makeApi())
    await refreshCtl.controller.refresh()
    expect(refreshCtl.deps.setInitialLoading).not.toHaveBeenCalled()

    const connectCtl = makeController(makeApi())
    await connectCtl.controller.connectPage(newPage, baseConnection)
    expect(connectCtl.deps.setInitialLoading).not.toHaveBeenCalled()

    const disconnectCtl = makeController(makeApi())
    await disconnectCtl.controller.disconnectPage(connectedPage, baseConnection)
    expect(disconnectCtl.deps.setInitialLoading).not.toHaveBeenCalled()
  })
})

describe('createAssetsPageController — auto-repair', () => {
  it('runs the idempotent backend repair at most once per load', async () => {
    const pageId = 'PAGE_1'
    const igUserId = 'IG_1'
    // Connected Page with a Meta-linked IG that was never promoted to a connected
    // asset — the production bug the self-heal targets.
    const eligibility: InstagramDiscoveryResponse = {
      pages: [
        {
          pageId,
          pageName: 'Existing Page',
          igUserId,
          igUsername: 'shop',
          igDisplayName: 'Shop',
          igProfilePictureUrl: null,
          eligibilityStatus: 'Connected',
          reason: '',
        },
      ],
      totalPages: 1,
      linkedCount: 1,
    }
    const api = makeApi({
      getInstagramEligibility: vi.fn().mockResolvedValue(eligibility),
    })
    const { controller } = makeController(api)

    await controller.loadInitial()

    // Repair fires once; the reload re-reads the connection but the guard prevents
    // a second repair (and any infinite loop).
    expect(api.refreshAssets).toHaveBeenCalledTimes(1)
    expect(api.getConnection).toHaveBeenCalledTimes(2)
  })
})
