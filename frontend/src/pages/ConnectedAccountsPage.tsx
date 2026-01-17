import { useState } from 'react'
import './ConnectedAccountsPage.css'

interface ConnectedAccount {
  id: string
  platform: 'Meta' | 'LinkedIn' | 'Twitter' | 'TikTok'
  name: string
  username: string
  connectedAt: string
  avatarUrl?: string
}

// Mock data - will be replaced with API calls
const mockAccounts: ConnectedAccount[] = []

const platforms = [
  {
    id: 'meta',
    name: 'Meta',
    description: 'Connect Facebook Pages & Instagram Business accounts',
    icon: (
      <svg viewBox="0 0 24 24" fill="currentColor" className="platform-svg">
        <path d="M12 2C6.477 2 2 6.477 2 12c0 4.991 3.657 9.128 8.438 9.879V14.89h-2.54V12h2.54V9.797c0-2.506 1.492-3.89 3.777-3.89 1.094 0 2.238.195 2.238.195v2.46h-1.26c-1.243 0-1.63.771-1.63 1.562V12h2.773l-.443 2.89h-2.33v6.989C18.343 21.129 22 16.99 22 12c0-5.523-4.477-10-10-10z"/>
      </svg>
    ),
    color: '#1877F2',
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

  const handleConnect = async (platformId: string) => {
    setConnecting(platformId)
    // TODO: Implement OAuth flow
    // For now, simulate a connection delay
    setTimeout(() => {
      setConnecting(null)
      // In real implementation, this would redirect to OAuth
      alert(`OAuth flow for ${platformId} will be implemented soon!`)
    }, 1000)
  }

  const handleDisconnect = (accountId: string) => {
    // TODO: Add confirmation dialog
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

  return (
    <div className="connected-accounts-page">
      <h1>Connected Accounts</h1>
      <p className="page-subtitle">
        Connect your social media accounts to start scheduling posts
      </p>

      <div className="platforms-grid">
        {platforms.map(platform => {
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
                        ×
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
        })}
      </div>

      {accounts.length > 0 && (
        <div className="accounts-summary">
          <h2>Your Connected Accounts</h2>
          <p className="summary-count">
            {accounts.length} account{accounts.length !== 1 ? 's' : ''} connected
          </p>
        </div>
      )}
    </div>
  )
}
