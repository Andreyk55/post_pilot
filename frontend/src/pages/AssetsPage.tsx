import { useState, useEffect } from 'react'
import './AssetsPage.css'
import { metaApi } from '../api/meta'
import type { MetaConnection, FacebookPage, InstagramAccount, ConnectedPage, ConnectedInstagramAccount } from '../types/meta'

interface AssetsPageProps {
  onNavigate: (page: string) => void
}

export function AssetsPage({ onNavigate }: AssetsPageProps) {
  // Meta connection state
  const [metaConnection, setMetaConnection] = useState<MetaConnection | null>(null)
  const [loading, setLoading] = useState(true)

  // Available pages/accounts from Meta (not yet connected)
  const [availablePages, setAvailablePages] = useState<FacebookPage[]>([])
  const [availableInstagram, setAvailableInstagram] = useState<InstagramAccount[]>([])
  const [loadingPages, setLoadingPages] = useState(false)

  // Connection states
  const [connectingPageIds, setConnectingPageIds] = useState<Set<string>>(new Set())
  const [disconnectingPageIds, setDisconnectingPageIds] = useState<Set<string>>(new Set())
  const [connectingIgIds, setConnectingIgIds] = useState<Set<string>>(new Set())
  const [disconnectingIgIds, setDisconnectingIgIds] = useState<Set<string>>(new Set())

  // Load Meta connection on mount
  useEffect(() => {
    loadMetaConnection()
  }, [])

  const loadMetaConnection = async () => {
    try {
      setLoading(true)
      const response = await metaApi.getConnection()
      if (response.isConnected && response.connection) {
        setMetaConnection(response.connection)
        // Load available pages to see what else can be connected
        await loadAvailablePages()
      } else {
        setMetaConnection(null)
      }
    } catch (err) {
      console.error('Failed to load Meta connection:', err)
      setMetaConnection(null)
    } finally {
      setLoading(false)
    }
  }

  const loadAvailablePages = async () => {
    try {
      setLoadingPages(true)
      const { pages } = await metaApi.getAvailablePages()
      setAvailablePages(pages)

      // Discover Instagram accounts for all pages
      if (pages.length > 0) {
        const pageIds = pages.map(p => p.id)
        const igResponse = await metaApi.discoverInstagram({ tempToken: '', pageIds })
        setAvailableInstagram(igResponse.instagramAccounts)
      }
    } catch (err) {
      console.error('Failed to load available pages:', err)
    } finally {
      setLoadingPages(false)
    }
  }

  const handleConnectPage = async (page: FacebookPage) => {
    if (!metaConnection) return

    setConnectingPageIds(prev => new Set(prev).add(page.id))
    try {
      // Get current connected page IDs and add this one
      const currentPageIds = metaConnection.pages.map(p => p.pageId)
      const currentIgIds = metaConnection.instagramAccounts.map(ig => ig.igBusinessId)

      await metaApi.updateConnection({
        selectedPageIds: [...currentPageIds, page.id],
        selectedInstagramIds: currentIgIds
      })

      await loadMetaConnection()
    } catch (err) {
      console.error('Failed to connect page:', err)
      alert('Failed to connect page. Please try again.')
    } finally {
      setConnectingPageIds(prev => {
        const next = new Set(prev)
        next.delete(page.id)
        return next
      })
    }
  }

  const handleDisconnectPage = async (page: ConnectedPage) => {
    if (!metaConnection) return

    setDisconnectingPageIds(prev => new Set(prev).add(page.pageId))
    try {
      // Get current connected page IDs and remove this one
      const currentPageIds = metaConnection.pages
        .filter(p => p.pageId !== page.pageId)
        .map(p => p.pageId)

      // Also remove Instagram accounts linked to this page
      const currentIgIds = metaConnection.instagramAccounts
        .filter(ig => ig.pageId !== page.pageId)
        .map(ig => ig.igBusinessId)

      await metaApi.updateConnection({
        selectedPageIds: currentPageIds,
        selectedInstagramIds: currentIgIds
      })

      await loadMetaConnection()
    } catch (err) {
      console.error('Failed to disconnect page:', err)
      alert('Failed to disconnect page. Please try again.')
    } finally {
      setDisconnectingPageIds(prev => {
        const next = new Set(prev)
        next.delete(page.pageId)
        return next
      })
    }
  }

  const handleConnectInstagram = async (ig: InstagramAccount) => {
    if (!metaConnection) return

    // Check if the linked page is connected
    const linkedPageConnected = metaConnection.pages.some(p => p.pageId === ig.pageId)
    if (!linkedPageConnected) {
      alert(`Please connect the "${ig.pageName}" Facebook Page first before connecting this Instagram account.`)
      return
    }

    setConnectingIgIds(prev => new Set(prev).add(ig.id))
    try {
      const currentPageIds = metaConnection.pages.map(p => p.pageId)
      const currentIgIds = metaConnection.instagramAccounts.map(a => a.igBusinessId)

      await metaApi.updateConnection({
        selectedPageIds: currentPageIds,
        selectedInstagramIds: [...currentIgIds, ig.id]
      })

      await loadMetaConnection()
    } catch (err) {
      console.error('Failed to connect Instagram:', err)
      alert('Failed to connect Instagram account. Please try again.')
    } finally {
      setConnectingIgIds(prev => {
        const next = new Set(prev)
        next.delete(ig.id)
        return next
      })
    }
  }

  const handleDisconnectInstagram = async (ig: ConnectedInstagramAccount) => {
    if (!metaConnection) return

    setDisconnectingIgIds(prev => new Set(prev).add(ig.igBusinessId))
    try {
      const currentPageIds = metaConnection.pages.map(p => p.pageId)
      const currentIgIds = metaConnection.instagramAccounts
        .filter(a => a.igBusinessId !== ig.igBusinessId)
        .map(a => a.igBusinessId)

      await metaApi.updateConnection({
        selectedPageIds: currentPageIds,
        selectedInstagramIds: currentIgIds
      })

      await loadMetaConnection()
    } catch (err) {
      console.error('Failed to disconnect Instagram:', err)
      alert('Failed to disconnect Instagram account. Please try again.')
    } finally {
      setDisconnectingIgIds(prev => {
        const next = new Set(prev)
        next.delete(ig.igBusinessId)
        return next
      })
    }
  }

  // Get pages that are available but not yet connected
  const getUnconnectedPages = () => {
    if (!metaConnection) return availablePages
    const connectedPageIds = new Set(metaConnection.pages.map(p => p.pageId))
    return availablePages.filter(p => !connectedPageIds.has(p.id))
  }

  // Get Instagram accounts that are available but not yet connected
  const getUnconnectedInstagram = () => {
    if (!metaConnection) return availableInstagram
    const connectedIgIds = new Set(metaConnection.instagramAccounts.map(ig => ig.igBusinessId))
    return availableInstagram.filter(ig => !connectedIgIds.has(ig.id))
  }

  // Loading state
  if (loading) {
    return (
      <div className="assets-page">
        <h1>Assets</h1>
        <div className="loading-container">
          <span className="spinner"></span>
          <span>Loading assets...</span>
        </div>
      </div>
    )
  }

  // Not connected state
  if (!metaConnection) {
    return (
      <div className="assets-page">
        <h1>Assets</h1>
        <p className="page-subtitle">
          Manage your Facebook Pages and Instagram Business accounts
        </p>

        <div className="empty-state">
          <div className="empty-state-icon">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
              <path d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" strokeLinecap="round" strokeLinejoin="round"/>
            </svg>
          </div>
          <h2>Meta account not connected</h2>
          <p>Connect your Meta account first to manage Facebook Pages and Instagram Business accounts.</p>
          <button
            className="connect-meta-btn"
            onClick={() => onNavigate('accounts')}
          >
            Go to Connected Accounts
          </button>
        </div>
      </div>
    )
  }

  const unconnectedPages = getUnconnectedPages()
  const unconnectedInstagram = getUnconnectedInstagram()

  return (
    <div className="assets-page">
      <h1>Assets</h1>
      <p className="page-subtitle">
        Manage your Facebook Pages and Instagram Business accounts
      </p>

      {/* Facebook Pages Section */}
      <section className="assets-section">
        <div className="section-header">
          <div className="section-title">
            <div className="section-icon facebook">
              <svg viewBox="0 0 24 24" fill="currentColor">
                <path d="M24 12.073c0-6.627-5.373-12-12-12s-12 5.373-12 12c0 5.99 4.388 10.954 10.125 11.854v-8.385H7.078v-3.47h3.047V9.43c0-3.007 1.792-4.669 4.533-4.669 1.312 0 2.686.235 2.686.235v2.953H15.83c-1.491 0-1.956.925-1.956 1.874v2.25h3.328l-.532 3.47h-2.796v8.385C19.612 23.027 24 18.062 24 12.073z"/>
              </svg>
            </div>
            <h2>Facebook Pages</h2>
          </div>
          {loadingPages && <span className="loading-badge">Refreshing...</span>}
        </div>

        {/* Connected Pages */}
        {metaConnection.pages.length > 0 && (
          <div className="assets-list">
            <h3 className="list-subtitle">Connected Pages</h3>
            {metaConnection.pages.map(page => (
              <div key={page.id} className="asset-item connected">
                <div className="asset-avatar">
                  {page.pictureUrl ? (
                    <img src={page.pictureUrl} alt={page.name} />
                  ) : (
                    page.name.charAt(0).toUpperCase()
                  )}
                </div>
                <div className="asset-details">
                  <span className="asset-name">{page.name}</span>
                  {page.category && <span className="asset-meta">{page.category}</span>}
                </div>
                <div className="asset-status connected">
                  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                    <polyline points="20 6 9 17 4 12" />
                  </svg>
                  Connected
                </div>
                <button
                  className="disconnect-btn"
                  onClick={() => handleDisconnectPage(page)}
                  disabled={disconnectingPageIds.has(page.pageId)}
                  title="Disconnect page"
                >
                  {disconnectingPageIds.has(page.pageId) ? (
                    <span className="spinner small"></span>
                  ) : (
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                      <line x1="18" y1="6" x2="6" y2="18"></line>
                      <line x1="6" y1="6" x2="18" y2="18"></line>
                    </svg>
                  )}
                </button>
              </div>
            ))}
          </div>
        )}

        {/* Unconnected Pages */}
        {unconnectedPages.length > 0 && (
          <div className="assets-list">
            <h3 className="list-subtitle">Available Pages</h3>
            {unconnectedPages.map(page => (
              <div key={page.id} className="asset-item">
                <div className="asset-avatar">
                  {page.pictureUrl ? (
                    <img src={page.pictureUrl} alt={page.name} />
                  ) : (
                    page.name.charAt(0).toUpperCase()
                  )}
                </div>
                <div className="asset-details">
                  <span className="asset-name">{page.name}</span>
                  {page.category && <span className="asset-meta">{page.category}</span>}
                </div>
                <button
                  className="connect-btn small"
                  onClick={() => handleConnectPage(page)}
                  disabled={connectingPageIds.has(page.id)}
                >
                  {connectingPageIds.has(page.id) ? (
                    <>
                      <span className="spinner small"></span>
                      Connecting...
                    </>
                  ) : (
                    <>
                      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                        <line x1="12" y1="5" x2="12" y2="19"></line>
                        <line x1="5" y1="12" x2="19" y2="12"></line>
                      </svg>
                      Connect
                    </>
                  )}
                </button>
              </div>
            ))}
          </div>
        )}

        {/* Empty state for pages */}
        {metaConnection.pages.length === 0 && unconnectedPages.length === 0 && !loadingPages && (
          <div className="section-empty">
            <p>No Facebook Pages found. Make sure you have admin access to at least one Facebook Page.</p>
          </div>
        )}
      </section>

      {/* Instagram Business Section */}
      <section className="assets-section">
        <div className="section-header">
          <div className="section-title">
            <div className="section-icon instagram">
              <svg viewBox="0 0 24 24" fill="currentColor">
                <path d="M12 2.163c3.204 0 3.584.012 4.85.07 3.252.148 4.771 1.691 4.919 4.919.058 1.265.069 1.645.069 4.849 0 3.205-.012 3.584-.069 4.849-.149 3.225-1.664 4.771-4.919 4.919-1.266.058-1.644.07-4.85.07-3.204 0-3.584-.012-4.849-.07-3.26-.149-4.771-1.699-4.919-4.92-.058-1.265-.07-1.644-.07-4.849 0-3.204.013-3.583.07-4.849.149-3.227 1.664-4.771 4.919-4.919 1.266-.057 1.645-.069 4.849-.069zM12 0C8.741 0 8.333.014 7.053.072 2.695.272.273 2.69.073 7.052.014 8.333 0 8.741 0 12c0 3.259.014 3.668.072 4.948.2 4.358 2.618 6.78 6.98 6.98C8.333 23.986 8.741 24 12 24c3.259 0 3.668-.014 4.948-.072 4.354-.2 6.782-2.618 6.979-6.98.059-1.28.073-1.689.073-4.948 0-3.259-.014-3.667-.072-4.947-.196-4.354-2.617-6.78-6.979-6.98C15.668.014 15.259 0 12 0zm0 5.838a6.162 6.162 0 100 12.324 6.162 6.162 0 000-12.324zM12 16a4 4 0 110-8 4 4 0 010 8zm6.406-11.845a1.44 1.44 0 100 2.881 1.44 1.44 0 000-2.881z"/>
              </svg>
            </div>
            <h2>Instagram Business</h2>
          </div>
        </div>

        {/* Connected Instagram Accounts */}
        {metaConnection.instagramAccounts.length > 0 && (
          <div className="assets-list">
            <h3 className="list-subtitle">Connected Accounts</h3>
            {metaConnection.instagramAccounts.map(ig => (
              <div key={ig.id} className="asset-item connected">
                <div className="asset-avatar instagram">
                  {ig.profilePictureUrl ? (
                    <img src={ig.profilePictureUrl} alt={ig.username} />
                  ) : (
                    ig.username.charAt(0).toUpperCase()
                  )}
                </div>
                <div className="asset-details">
                  <span className="asset-name">@{ig.username}</span>
                  <span className="asset-meta">Linked to {ig.pageName}</span>
                </div>
                <div className="asset-status connected">
                  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                    <polyline points="20 6 9 17 4 12" />
                  </svg>
                  Connected
                </div>
                <button
                  className="disconnect-btn"
                  onClick={() => handleDisconnectInstagram(ig)}
                  disabled={disconnectingIgIds.has(ig.igBusinessId)}
                  title="Disconnect account"
                >
                  {disconnectingIgIds.has(ig.igBusinessId) ? (
                    <span className="spinner small"></span>
                  ) : (
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                      <line x1="18" y1="6" x2="6" y2="18"></line>
                      <line x1="6" y1="6" x2="18" y2="18"></line>
                    </svg>
                  )}
                </button>
              </div>
            ))}
          </div>
        )}

        {/* Unconnected Instagram Accounts */}
        {unconnectedInstagram.length > 0 && (
          <div className="assets-list">
            <h3 className="list-subtitle">Available Accounts</h3>
            {unconnectedInstagram.map(ig => {
              const linkedPageConnected = metaConnection.pages.some(p => p.pageId === ig.pageId)
              return (
                <div key={ig.id} className="asset-item">
                  <div className="asset-avatar instagram">
                    {ig.profilePictureUrl ? (
                      <img src={ig.profilePictureUrl} alt={ig.username} />
                    ) : (
                      ig.username.charAt(0).toUpperCase()
                    )}
                  </div>
                  <div className="asset-details">
                    <span className="asset-name">@{ig.username}</span>
                    <span className="asset-meta">
                      Linked to {ig.pageName}
                      {!linkedPageConnected && ' (Page not connected)'}
                    </span>
                  </div>
                  <button
                    className={`connect-btn small ${!linkedPageConnected ? 'disabled' : ''}`}
                    onClick={() => handleConnectInstagram(ig)}
                    disabled={connectingIgIds.has(ig.id) || !linkedPageConnected}
                    title={!linkedPageConnected ? 'Connect the linked Facebook Page first' : 'Connect account'}
                  >
                    {connectingIgIds.has(ig.id) ? (
                      <>
                        <span className="spinner small"></span>
                        Connecting...
                      </>
                    ) : (
                      <>
                        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                          <line x1="12" y1="5" x2="12" y2="19"></line>
                          <line x1="5" y1="12" x2="19" y2="12"></line>
                        </svg>
                        Connect
                      </>
                    )}
                  </button>
                </div>
              )
            })}
          </div>
        )}

        {/* Empty state for Instagram */}
        {metaConnection.instagramAccounts.length === 0 && unconnectedInstagram.length === 0 && !loadingPages && (
          <div className="section-empty">
            <p>No Instagram Business accounts found. Instagram accounts must be linked to a Facebook Page in Meta Business Suite.</p>
          </div>
        )}
      </section>
    </div>
  )
}
