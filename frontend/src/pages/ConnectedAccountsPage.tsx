import { useState, useEffect } from 'react'
import './ConnectedAccountsPage.css'
import metaLogo from '../assets/meta-logo.svg'
import { metaApi } from '../api/meta'
import type { MetaConnection } from '../types/meta'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { Toast } from '../components/Toast'
import { buildProviderDisconnectMessage } from '../components/providerDisconnectMessage'
import { WorkspaceContextBadge } from '../components/WorkspaceContextBadge'
import { useAuth } from '../hooks/useAuth'
import { guardWorkspaceAction, NO_WORKSPACE_ACTION_MESSAGE } from '../api/workspaceGuard'

const metaPlatform = {
  id: 'meta',
  name: 'Meta',
  description: 'Connect your Meta account to manage Facebook Pages and linked Instagram accounts.',
  icon: <img src={metaLogo} alt="Meta" className="platform-svg" />,
  color: '#0081FB',
}

interface ConnectedAccountsPageProps {
  /** Optional callback for navigating to other pages (e.g., Publishing Assets) */
  onNavigate?: (page: string) => void
}

export function ConnectedAccountsPage({ onNavigate }: ConnectedAccountsPageProps = {}) {
  const { hasWorkspace } = useAuth()
  const [connecting, setConnecting] = useState<string | null>(null)

  // Meta-specific state (identity-level only)
  const [metaConnection, setMetaConnection] = useState<MetaConnection | null>(null)
  const [metaLoading, setMetaLoading] = useState(false)
  const [disconnecting, setDisconnecting] = useState(false)
  const [showDisconnectDialog, setShowDisconnectDialog] = useState(false)
  const [showToast, setShowToast] = useState(false)
  const [toastMessage, setToastMessage] = useState('')
  const [toastType, setToastType] = useState<'success' | 'error' | 'info'>('success')

  const showErrorToast = (message: string) => {
    setToastMessage(message)
    setToastType('error')
    setShowToast(true)
  }

  // Load Meta connection status on mount
  useEffect(() => {
    loadMetaConnection()
  }, [])

  // Listen for OAuth popup callback
  useEffect(() => {
    const handleMessage = async (event: MessageEvent) => {
      if (event.origin !== window.location.origin) return

      if (event.data?.type === 'META_OAUTH_SUCCESS') {
        // OAuth completed successfully, reload connection
        setConnecting(null)
        await loadMetaConnection()
      } else if (event.data?.type === 'META_OAUTH_ERROR') {
        setConnecting(null)
        // 409 = workspace already has an active provider connection.
        // Surface the server's exact message ("This workspace already has a
        // connected Meta account. Disconnect it before connecting another one.")
        // instead of a generic failure dialog.
        const status = event.data?.status as number | undefined
        const message = event.data?.message as string | undefined
        if (status === 409 && message) {
          showErrorToast(message)
        } else {
          showErrorToast('Failed to connect to Meta. Please try again.')
        }
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
      } else {
        setMetaConnection(null)
      }
    } catch (err) {
      console.error('Failed to load Meta connection:', err)
      setMetaConnection(null)
    } finally {
      setMetaLoading(false)
    }
  }

  const handleConnectMeta = async () => {
    // If already connected, do nothing
    if (metaConnection) {
      return
    }

    setConnecting('meta')
    setMetaLoading(true)

    // First check if already connected
    try {
      const response = await metaApi.getConnection()
      if (response.isConnected && response.connection) {
        setMetaConnection(response.connection)
        setConnecting(null)
        setMetaLoading(false)
        return
      }
    } catch (err) {
      console.error('Failed to check Meta connection:', err)
    }

    setMetaLoading(false)

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
          setConnecting(null)
        }
      }, 500)
    } catch (err) {
      console.error('Failed to start Meta OAuth:', err)
      setConnecting(null)
      showErrorToast('Failed to start Meta connection. Please try again.')
    }
  }

  const handleDisconnectMeta = () => {
    // Disconnect is workspace-scoped too; don't even open the confirm dialog
    // without a selected workspace.
    if (!guardWorkspaceAction(hasWorkspace, { notify: showErrorToast })) return
    setShowDisconnectDialog(true)
  }

  const confirmDisconnectMeta = async () => {
    setDisconnecting(true)
    try {
      await metaApi.disconnect()
      setMetaConnection(null)
      setShowDisconnectDialog(false)
      setToastMessage('Meta disconnected successfully')
      setToastType('success')
      setShowToast(true)
    } catch (err) {
      console.error('Failed to disconnect Meta:', err)
      showErrorToast('Failed to disconnect Meta. Please try again.')
    } finally {
      setDisconnecting(false)
    }
  }

  const handleConnect = async (platformId: string) => {
    // Defense-in-depth: provider connections are workspace-scoped. Without a
    // selected workspace the backend would reject the OAuth save, so block here
    // and steer the user to pick a workspace first. (No auto-select.)
    if (!guardWorkspaceAction(hasWorkspace, { notify: showErrorToast })) return

    if (platformId === 'meta') {
      handleConnectMeta()
    }
  }

  const renderMetaCard = () => {
    const isConnecting = connecting === 'meta'
    const isConnected = !!metaConnection

    return (
      <div key="meta" className={`platform-card ${isConnected ? 'connected' : ''}`}>
        <div className="platform-header">
          <div
            className="platform-icon"
            style={{ backgroundColor: metaPlatform.color }}
          >
            {metaPlatform.icon}
          </div>
          <div className="platform-info">
            <h3>{metaPlatform.name}</h3>
            <p>{metaPlatform.description}</p>
          </div>
        </div>

        {metaLoading ? (
          <div className="loading-state">
            <span className="spinner"></span>
            <span>Loading connection...</span>
          </div>
        ) : isConnected ? (
          <div className="meta-connected-state">
            <div className="connected-status">
              <span className="connected-badge">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                  <polyline points="20 6 9 17 4 12" />
                </svg>
                Connected
              </span>
              {metaConnection?.providerAccountName && (
                <span className="connected-as">
                  Connected as: <strong>{metaConnection.providerAccountName}</strong>
                </span>
              )}
            </div>
            {/* Clarify what publishing access this identity-level connection grants,
                and point to the detailed asset list rather than duplicating it here. */}
            <p className="connection-access-hint">
              This connection gives Publish Harbor access to the Facebook Pages you
              allowed and any linked Instagram professional accounts.
            </p>
            {onNavigate && (
              <button
                type="button"
                className="view-assets-link"
                onClick={() => onNavigate('assets')}
              >
                View Publishing Assets →
              </button>
            )}
            {metaConnection?.status === 'ReauthRequired' && (
              // Token went invalid: the account is still owned/connected and posts
              // remain visible, but publishing will fail until the user reconnects
              // in THIS workspace. Offer a reconnect action (re-runs OAuth for the
              // same account, which clears the reauth flag and refreshes the token).
              <div className="reauth-banner" role="alert">
                <span>
                  Your Meta connection needs to be reauthorized. Reconnect to keep
                  publishing — your posts and history are safe.
                </span>
                <button
                  className="connect-btn"
                  onClick={() => handleConnect('meta')}
                  disabled={connecting === 'meta' || !hasWorkspace}
                  title={!hasWorkspace ? NO_WORKSPACE_ACTION_MESSAGE : undefined}
                >
                  {connecting === 'meta' ? 'Reconnecting...' : 'Reconnect'}
                </button>
              </div>
            )}
            <button
              className="disconnect-btn"
              onClick={handleDisconnectMeta}
              disabled={disconnecting || !hasWorkspace}
              title={!hasWorkspace ? NO_WORKSPACE_ACTION_MESSAGE : undefined}
            >
              {disconnecting ? (
                <>
                  <span className="spinner"></span>
                  Disconnecting...
                </>
              ) : (
                <>
                  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                    <path d="M18.36 6.64a9 9 0 1 1-12.73 0" />
                    <line x1="12" y1="2" x2="12" y2="12" />
                  </svg>
                  Disconnect
                </>
              )}
            </button>
          </div>
        ) : (
          <button
            className="connect-btn"
            onClick={() => handleConnect('meta')}
            disabled={isConnecting || disconnecting || !hasWorkspace}
            title={!hasWorkspace ? NO_WORKSPACE_ACTION_MESSAGE : undefined}
          >
            {isConnecting ? (
              <>
                <span className="spinner"></span>
                Connecting...
              </>
            ) : (
              <>Connect to Meta</>
            )}
          </button>
        )}
      </div>
    )
  }

  return (
    <div className="connected-accounts-page">
      <h1>Connected Accounts</h1>
      <p className="page-subtitle">
        Connect your Meta account to manage Facebook Pages and linked Instagram accounts.
      </p>
      {/* Connections belong to the current workspace — make that explicit, since a
          different workspace can have a different connected account. */}
      <div className="connected-accounts-workspace">
        <WorkspaceContextBadge action="Connecting for" />
      </div>

      <p className="page-subtitle">
        Facebook Pages and linked Instagram accounts
      </p>

      <div className="platforms-grid">
        {renderMetaCard()}
      </div>

      <ConfirmDialog
        isOpen={showDisconnectDialog}
        title="Disconnect Meta account?"
        message={buildProviderDisconnectMessage('Meta')}
        confirmText="Disconnect"
        cancelText="Cancel"
        confirmVariant="danger"
        onConfirm={confirmDisconnectMeta}
        onCancel={() => setShowDisconnectDialog(false)}
        isLoading={disconnecting}
      />

      <Toast
        message={toastMessage}
        type={toastType}
        isVisible={showToast}
        onClose={() => setShowToast(false)}
      />
    </div>
  )
}

