import { useState, useEffect } from 'react'
import './ConnectedAccountsPage.css'
import metaLogo from '../assets/meta-logo.svg'
import { metaApi } from '../api/meta'
import { MetaConnectionWizard, ConnectedTargetsList } from '../components/meta'
import type {
  MetaConnectionStatus,
  MetaConnection,
  ConnectedTarget,
} from '../types/meta'

interface ConnectedAccount {
  id: string
  platform: 'Meta' | 'LinkedIn' | 'Twitter' | 'TikTok'
  name: string
  username: string
  connectedAt: string
  avatarUrl?: string
}

// Mock data - will be replaced with API calls for non-Meta platforms
const mockAccounts: ConnectedAccount[] = []

const platforms = [
  {
    id: 'meta',
    name: 'Meta',
    description: 'Connect Facebook Pages & Instagram Business accounts',
    icon: <img src={metaLogo} alt="Meta" className="platform-svg" />,
    color: '#0081FB',
    available: true,
  },
  {
    id: 'linkedin',
    name: 'LinkedIn',
    description: 'Connect your LinkedIn profile or company pages',
    icon: (
      <svg viewBox="0 0 24 24" fill="currentColor" className="platform-svg">
        <path d="M20.447 20.452h-3.554v-5.569c0-1.328-.027-3.037-1.852-3.037-1.853 0-2.136 1.445-2.136 2.939v5.667H9.351V9h3.414v1.561h.046c.477-.9 1.637-1.85 3.37-1.85 3.601 0 4.267 2.37 4.267 5.455v6.286zM5.337 7.433a2.062 2.062 0 01-2.063-2.065 2.064 2.064 0 112.063 2.065zm1.782 13.019H3.555V9h3.564v11.452zM22.225 0H1.771C.792 0 0 .774 0 1.729v20.542C0 23.227.792 24 1.771 24h20.451C23.2 24 24 23.227 24 22.271V1.729C24 .774 23.2 0 22.222 0h.003z"/>
      </svg>
    ),
    color: '#0A66C2',
    available: true,
  },
  {
    id: 'twitter',
    name: 'X (Twitter)',
    description: 'Connect your X account to schedule tweets',
    icon: (
      <svg viewBox="0 0 24 24" fill="currentColor" className="platform-svg">
        <path d="M18.244 2.25h3.308l-7.227 8.26 8.502 11.24H16.17l-5.214-6.817L4.99 21.75H1.68l7.73-8.835L1.254 2.25H8.08l4.713 6.231zm-1.161 17.52h1.833L7.084 4.126H5.117z"/>
      </svg>
    ),
    color: '#000000',
    available: false,
    comingSoon: true,
  },
  {
    id: 'tiktok',
    name: 'TikTok',
    description: 'Connect your TikTok account for video posts',
    icon: (
      <svg viewBox="0 0 24 24" fill="currentColor" className="platform-svg">
        <path d="M19.59 6.69a4.83 4.83 0 01-3.77-4.25V2h-3.45v13.67a2.89 2.89 0 01-5.2 1.74 2.89 2.89 0 012.31-4.64 2.93 2.93 0 01.88.13V9.4a6.84 6.84 0 00-1-.05A6.33 6.33 0 005 20.1a6.34 6.34 0 0010.86-4.43v-7a8.16 8.16 0 004.77 1.52v-3.4a4.85 4.85 0 01-1-.1z"/>
      </svg>
    ),
    color: '#000000',
    available: false,
    comingSoon: true,
  },
]

export function ConnectedAccountsPage() {
  const [accounts, setAccounts] = useState<ConnectedAccount[]>(mockAccounts)
  const [connecting, setConnecting] = useState<string | null>(null)

  // Meta-specific state
  const [metaStatus, setMetaStatus] = useState<MetaConnectionStatus>('disconnected')
  const [metaConnection, setMetaConnection] = useState<MetaConnection | null>(null)
  const [metaTargets, setMetaTargets] = useState<ConnectedTarget[]>([])
  const [showMetaWizard, setShowMetaWizard] = useState(false)
  const [isManageMode, setIsManageMode] = useState(false)
  const [metaLoading, setMetaLoading] = useState(false)
  const [disconnecting, setDisconnecting] = useState(false)


  // Listen for OAuth popup callback
  useEffect(() => {
    const handleMessage = async (event: MessageEvent) => {
      if (event.origin !== window.location.origin) return

      if (event.data?.type === 'META_OAUTH_SUCCESS') {
        // OAuth completed successfully, show wizard
        setMetaStatus('selecting_pages')
        setShowMetaWizard(true)
        setConnecting(null)
      } else if (event.data?.type === 'META_OAUTH_ERROR') {
        setMetaStatus('disconnected')
        setConnecting(null)
        alert('Failed to connect to Meta. Please try again.')
      }
    }

    window.addEventListener('message', handleMessage)
    return () => window.removeEventListener('message', handleMessage)
  }, [])

  const loadMetaConnection = async () => {
    try {
      setMetaLoading(true)
      const response = await metaApi.getConnection()
      if (response.isConnected && response.connection) {
        setMetaConnection(response.connection)
        setMetaStatus('connected')
        setMetaTargets(buildTargetsFromConnection(response.connection))
      } else {
        setMetaStatus('disconnected')
        setMetaConnection(null)
        setMetaTargets([])
      }
    } catch (err) {
      console.error('Failed to load Meta connection:', err)
      setMetaStatus('disconnected')
    } finally {
      setMetaLoading(false)
    }
  }

  const buildTargetsFromConnection = (connection: MetaConnection): ConnectedTarget[] => {
    const targets: ConnectedTarget[] = []

    connection.pages.forEach(page => {
      targets.push({
        id: page.id,
        type: 'facebook_page',
        name: page.name,
        identifier: page.pageId,
        pictureUrl: page.pictureUrl,
      })
    })

    connection.instagramAccounts.forEach(ig => {
      targets.push({
        id: ig.id,
        type: 'instagram',
        name: ig.name || ig.username,
        identifier: `@${ig.username}`,
        pictureUrl: ig.profilePictureUrl,
      })
    })

    return targets
  }

  const handleConnectMeta = async () => {
    // If already connected, just show the current state
    if (metaStatus === 'connected') {
      return
    }

    // First check if already connected
    setConnecting('meta')
    setMetaLoading(true)

    try {
      const response = await metaApi.getConnection()
      if (response.isConnected && response.connection) {
        setMetaConnection(response.connection)
        setMetaStatus('connected')
        setMetaTargets(buildTargetsFromConnection(response.connection))
        setConnecting(null)
        setMetaLoading(false)
        return
      }
    } catch (err) {
      console.error('Failed to check Meta connection:', err)
    }

    setMetaLoading(false)
    setMetaStatus('authorizing')

    try {
      const { authUrl } = await metaApi.startOAuth()

      // Open OAuth in popup
      const width = 600
      const height = 700
      const left = window.screenX + (window.outerWidth - width) / 2
      const top = window.screenY + (window.outerHeight - height) / 2

      const popup = window.open(
        authUrl,
        'meta-oauth',
        `width=${width},height=${height},left=${left},top=${top},popup=yes`
      )

      if (!popup) {
        // Popup was blocked, fall back to redirect
        window.location.href = authUrl
        return
      }

      // Poll to check if popup is closed
      const pollTimer = setInterval(() => {
        if (popup.closed) {
          clearInterval(pollTimer)
          // If we're still in authorizing state, the user closed the popup
          if (metaStatus === 'authorizing') {
            setMetaStatus('disconnected')
            setConnecting(null)
          }
        }
      }, 500)
    } catch (err) {
      console.error('Failed to start Meta OAuth:', err)
      setMetaStatus('disconnected')
      setConnecting(null)
      alert('Failed to start Meta connection. Please try again.')
    }
  }

  const handleManagePages = () => {
    setIsManageMode(true)
    setShowMetaWizard(true)
  }

  const handleDisconnectMeta = async () => {
    if (!confirm('Are you sure you want to disconnect Meta? This will remove all connected Facebook Pages and Instagram accounts.')) {
      return
    }

    setDisconnecting(true)
    try {
      await metaApi.disconnect()
      setMetaConnection(null)
      setMetaTargets([])
      setMetaStatus('disconnected')
    } catch (err) {
      console.error('Failed to disconnect Meta:', err)
      alert('Failed to disconnect Meta. Please try again.')
    } finally {
      setDisconnecting(false)
    }
  }

  const handleWizardComplete = () => {
    setShowMetaWizard(false)
    setIsManageMode(false)
    loadMetaConnection() // Reload the connection data
  }

  const handleWizardClose = () => {
    setShowMetaWizard(false)
    setIsManageMode(false)
    // Reset to disconnected if we were in the middle of connecting
    if (metaStatus !== 'connected') {
      setMetaStatus('disconnected')
    }
    setConnecting(null)
  }

  const handleConnect = async (platformId: string) => {
    if (platformId === 'meta') {
      handleConnectMeta()
      return
    }

    setConnecting(platformId)
    // For now, simulate a connection delay for other platforms
    setTimeout(() => {
      setConnecting(null)
      alert(`OAuth flow for ${platformId} will be implemented soon!`)
    }, 1000)
  }

  const handleDisconnect = (accountId: string) => {
    setAccounts(accounts.filter(a => a.id !== accountId))
  }

  const getConnectedAccountsForPlatform = (platformId: string) => {
    const platformMap: Record<string, ConnectedAccount['platform']> = {
      meta: 'Meta',
      linkedin: 'LinkedIn',
      twitter: 'Twitter',
      tiktok: 'TikTok',
    }
    return accounts.filter(a => a.platform === platformMap[platformId])
  }

  const renderMetaCard = () => {
    const platform = platforms.find(p => p.id === 'meta')!
    const isConnecting = connecting === 'meta'
    const isConnected = metaStatus === 'connected'

    return (
      <div key="meta" className={`platform-card ${isConnected ? 'connected' : ''}`}>
        <div className="platform-header">
          <div
            className="platform-icon"
            style={{ backgroundColor: platform.color }}
          >
            {platform.icon}
          </div>
          <div className="platform-info">
            <h3>
              {platform.name}
              {isConnected && (
                <span className="status-badge connected">Connected</span>
              )}
            </h3>
            <p>{platform.description}</p>
          </div>
        </div>

        {metaLoading ? (
          <div className="loading-state">
            <span className="spinner"></span>
            <span>Loading connection...</span>
          </div>
        ) : isConnected && metaTargets.length > 0 ? (
          <ConnectedTargetsList
            targets={metaTargets}
            onManagePages={handleManagePages}
            onDisconnect={handleDisconnectMeta}
          />
        ) : (
          <button
            className="connect-btn"
            onClick={() => handleConnect('meta')}
            disabled={isConnecting || disconnecting}
          >
            {isConnecting ? (
              <>
                <span className="spinner"></span>
                Connecting...
              </>
            ) : disconnecting ? (
              <>
                <span className="spinner"></span>
                Disconnecting...
              </>
            ) : (
              <>Connect to Meta</>
            )}
          </button>
        )}
      </div>
    )
  }

  const renderOtherPlatformCard = (platform: typeof platforms[0]) => {
    const connectedAccounts = getConnectedAccountsForPlatform(platform.id)
    const isConnecting = connecting === platform.id

    return (
      <div key={platform.id} className="platform-card">
        <div className="platform-header">
          <div
            className="platform-icon"
            style={{ backgroundColor: platform.color }}
          >
            {platform.icon}
          </div>
          <div className="platform-info">
            <h3>
              {platform.name}
              {platform.comingSoon && (
                <span className="coming-soon-badge">Coming Soon</span>
              )}
            </h3>
            <p>{platform.description}</p>
          </div>
        </div>

        {connectedAccounts.length > 0 && (
          <div className="connected-accounts-list">
            {connectedAccounts.map(account => (
              <div key={account.id} className="connected-account-item">
                <div className="account-avatar">
                  {account.avatarUrl ? (
                    <img src={account.avatarUrl} alt={account.name} />
                  ) : (
                    account.name.charAt(0).toUpperCase()
                  )}
                </div>
                <div className="account-details">
                  <span className="account-name">{account.name}</span>
                  <span className="account-username">@{account.username}</span>
                </div>
                <button
                  className="disconnect-btn"
                  onClick={() => handleDisconnect(account.id)}
                  title="Disconnect account"
                >
                  x
                </button>
              </div>
            ))}
          </div>
        )}

        <button
          className={`connect-btn ${platform.comingSoon ? 'disabled' : ''}`}
          onClick={() => handleConnect(platform.id)}
          disabled={!platform.available || isConnecting}
        >
          {isConnecting ? (
            <>
              <span className="spinner"></span>
              Connecting...
            </>
          ) : connectedAccounts.length > 0 ? (
            <>+ Add Another Account</>
          ) : (
            <>Connect to {platform.name}</>
          )}
        </button>
      </div>
    )
  }

  return (
    <div className="connected-accounts-page">
      <h1>Connected Accounts</h1>
      <p className="page-subtitle">
        Connect your social media accounts to start scheduling posts
      </p>

      <div className="platforms-grid">
        {renderMetaCard()}
        {platforms.filter(p => p.id !== 'meta').map(renderOtherPlatformCard)}
      </div>

      {(accounts.length > 0 || metaTargets.length > 0) && (
        <div className="accounts-summary">
          <h2>Your Connected Accounts</h2>
          <p className="summary-count">
            {accounts.length + metaTargets.length} target{accounts.length + metaTargets.length !== 1 ? 's' : ''} connected
          </p>
        </div>
      )}

      <MetaConnectionWizard
        isOpen={showMetaWizard}
        onClose={handleWizardClose}
        onComplete={handleWizardComplete}
        existingPageIds={metaConnection?.pages.map(p => p.pageId) || []}
        existingInstagramIds={metaConnection?.instagramAccounts.map(ig => ig.igBusinessId) || []}
        isManageMode={isManageMode}
      />
    </div>
  )
}
