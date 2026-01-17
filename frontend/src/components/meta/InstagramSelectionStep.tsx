import { useState, useEffect } from 'react'
import type { InstagramAccount } from '../../types/meta'

interface InstagramSelectionStepProps {
  instagramAccounts: InstagramAccount[]
  selectedInstagramIds: string[]
  onBack: () => void
  onComplete: (selectedInstagramIds: string[]) => void
  loading: boolean
}

export function InstagramSelectionStep({
  instagramAccounts,
  selectedInstagramIds: initialSelectedIds,
  onBack,
  onComplete,
  loading,
}: InstagramSelectionStepProps) {
  const [selectedIds, setSelectedIds] = useState<string[]>(initialSelectedIds)

  // Update local state when prop changes
  useEffect(() => {
    setSelectedIds(initialSelectedIds)
  }, [initialSelectedIds])

  const toggleAccount = (accountId: string) => {
    setSelectedIds(prev =>
      prev.includes(accountId)
        ? prev.filter(id => id !== accountId)
        : [...prev, accountId]
    )
  }

  const selectAll = () => {
    setSelectedIds(instagramAccounts.map(a => a.id))
  }

  const deselectAll = () => {
    setSelectedIds([])
  }

  const handleComplete = () => {
    onComplete(selectedIds)
  }

  const hasAccounts = instagramAccounts.length > 0

  return (
    <div className="selection-step">
      <div className="step-description">
        <h3>Instagram Business Accounts</h3>
        <p>
          {hasAccounts
            ? 'We found Instagram Business accounts linked to your selected pages. Choose which ones to connect.'
            : 'No Instagram Business accounts found linked to your selected Facebook Pages.'}
        </p>
      </div>

      {!hasAccounts ? (
        <div className="empty-state info">
          <div className="empty-icon instagram">
            <svg viewBox="0 0 24 24" fill="currentColor">
              <path d="M12 2.163c3.204 0 3.584.012 4.85.07 3.252.148 4.771 1.691 4.919 4.919.058 1.265.069 1.645.069 4.849 0 3.205-.012 3.584-.069 4.849-.149 3.225-1.664 4.771-4.919 4.919-1.266.058-1.644.07-4.85.07-3.204 0-3.584-.012-4.849-.07-3.26-.149-4.771-1.699-4.919-4.92-.058-1.265-.07-1.644-.07-4.849 0-3.204.013-3.583.07-4.849.149-3.227 1.664-4.771 4.919-4.919 1.266-.057 1.645-.069 4.849-.069zm0-2.163c-3.259 0-3.667.014-4.947.072-4.358.2-6.78 2.618-6.98 6.98-.059 1.281-.073 1.689-.073 4.948 0 3.259.014 3.668.072 4.948.2 4.358 2.618 6.78 6.98 6.98 1.281.058 1.689.072 4.948.072 3.259 0 3.668-.014 4.948-.072 4.354-.2 6.782-2.618 6.979-6.98.059-1.28.073-1.689.073-4.948 0-3.259-.014-3.667-.072-4.947-.196-4.354-2.617-6.78-6.979-6.98-1.281-.059-1.69-.073-4.949-.073zm0 5.838c-3.403 0-6.162 2.759-6.162 6.162s2.759 6.163 6.162 6.163 6.162-2.759 6.162-6.163c0-3.403-2.759-6.162-6.162-6.162zm0 10.162c-2.209 0-4-1.79-4-4 0-2.209 1.791-4 4-4s4 1.791 4 4c0 2.21-1.791 4-4 4zm6.406-11.845c-.796 0-1.441.645-1.441 1.44s.645 1.44 1.441 1.44c.795 0 1.439-.645 1.439-1.44s-.644-1.44-1.439-1.44z"/>
            </svg>
          </div>
          <p>No Instagram Business accounts linked to selected pages</p>
          <span className="empty-hint">
            To connect Instagram, you need to link an Instagram Business or Creator account to your Facebook Page.
            You can do this in your Facebook Page settings.
          </span>
        </div>
      ) : (
        <>
          <div className="selection-actions">
            <button
              type="button"
              className="text-btn"
              onClick={selectAll}
              disabled={selectedIds.length === instagramAccounts.length}
            >
              Select All
            </button>
            <span className="action-separator">|</span>
            <button
              type="button"
              className="text-btn"
              onClick={deselectAll}
              disabled={selectedIds.length === 0}
            >
              Deselect All
            </button>
            <span className="selection-count">
              {selectedIds.length} of {instagramAccounts.length} selected
            </span>
          </div>

          <div className="selection-list">
            {instagramAccounts.map(account => (
              <label
                key={account.id}
                className={`selection-item ${selectedIds.includes(account.id) ? 'selected' : ''}`}
              >
                <input
                  type="checkbox"
                  checked={selectedIds.includes(account.id)}
                  onChange={() => toggleAccount(account.id)}
                />
                <div className="item-avatar instagram">
                  {account.profilePictureUrl ? (
                    <img src={account.profilePictureUrl} alt={account.username} />
                  ) : (
                    <span className="avatar-placeholder">
                      {account.username.charAt(0).toUpperCase()}
                    </span>
                  )}
                </div>
                <div className="item-details">
                  <span className="item-name">@{account.username}</span>
                  <span className="item-meta">
                    Linked to {account.pageName}
                  </span>
                </div>
                <div className="item-check">
                  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3">
                    <polyline points="20 6 9 17 4 12" />
                  </svg>
                </div>
              </label>
            ))}
          </div>
        </>
      )}

      <div className="step-actions dual">
        <button className="secondary-btn" onClick={onBack} disabled={loading}>
          Back
        </button>
        <button
          className="primary-btn"
          onClick={handleComplete}
          disabled={loading}
        >
          {loading ? (
            <>
              <span className="spinner" />
              Saving...
            </>
          ) : (
            'Finish Setup'
          )}
        </button>
      </div>

      {!hasAccounts && (
        <p className="step-note">
          You can add Instagram accounts later by connecting them to your Facebook Pages first.
        </p>
      )}
    </div>
  )
}
