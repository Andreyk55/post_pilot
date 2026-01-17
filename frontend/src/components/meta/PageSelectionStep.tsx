import { useState, useEffect } from 'react'
import type { FacebookPage } from '../../types/meta'

interface PageSelectionStepProps {
  pages: FacebookPage[]
  selectedPageIds: string[]
  onContinue: (selectedPageIds: string[]) => void
  loading: boolean
}

export function PageSelectionStep({
  pages,
  selectedPageIds: initialSelectedIds,
  onContinue,
  loading,
}: PageSelectionStepProps) {
  const [selectedIds, setSelectedIds] = useState<string[]>(initialSelectedIds)

  // Update local state when prop changes
  useEffect(() => {
    setSelectedIds(initialSelectedIds)
  }, [initialSelectedIds])

  const togglePage = (pageId: string) => {
    setSelectedIds(prev =>
      prev.includes(pageId)
        ? prev.filter(id => id !== pageId)
        : [...prev, pageId]
    )
  }

  const selectAll = () => {
    setSelectedIds(pages.map(p => p.id))
  }

  const deselectAll = () => {
    setSelectedIds([])
  }

  const handleContinue = () => {
    onContinue(selectedIds)
  }

  const canContinue = selectedIds.length > 0

  return (
    <div className="selection-step">
      <div className="step-description">
        <h3>Select Facebook Pages</h3>
        <p>Choose which Facebook Pages you want to connect for scheduling posts.</p>
      </div>

      {pages.length === 0 ? (
        <div className="empty-state">
          <div className="empty-icon">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
            </svg>
          </div>
          <p>No Facebook Pages found.</p>
          <span className="empty-hint">
            Make sure your Meta account has admin access to at least one Facebook Page.
          </span>
        </div>
      ) : (
        <>
          <div className="selection-actions">
            <button
              type="button"
              className="text-btn"
              onClick={selectAll}
              disabled={selectedIds.length === pages.length}
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
              {selectedIds.length} of {pages.length} selected
            </span>
          </div>

          <div className="selection-list">
            {pages.map(page => (
              <label
                key={page.id}
                className={`selection-item ${selectedIds.includes(page.id) ? 'selected' : ''}`}
              >
                <input
                  type="checkbox"
                  checked={selectedIds.includes(page.id)}
                  onChange={() => togglePage(page.id)}
                />
                <div className="item-avatar">
                  {page.pictureUrl ? (
                    <img src={page.pictureUrl} alt={page.name} />
                  ) : (
                    <span className="avatar-placeholder">
                      {page.name.charAt(0).toUpperCase()}
                    </span>
                  )}
                </div>
                <div className="item-details">
                  <span className="item-name">{page.name}</span>
                  {page.category && (
                    <span className="item-meta">{page.category}</span>
                  )}
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

      <div className="step-actions">
        <button
          className="primary-btn"
          onClick={handleContinue}
          disabled={!canContinue || loading}
        >
          {loading ? (
            <>
              <span className="spinner" />
              Discovering Instagram...
            </>
          ) : (
            'Continue'
          )}
        </button>
        {!canContinue && pages.length > 0 && (
          <p className="validation-hint">Select at least one page to continue</p>
        )}
      </div>
    </div>
  )
}
