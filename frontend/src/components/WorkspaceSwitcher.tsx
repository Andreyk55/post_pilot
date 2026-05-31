import { useEffect, useRef, useState } from 'react'
import { useWorkspaces } from '../hooks/workspacesContext'
import { useAuth } from '../hooks/useAuth'
import './WorkspaceSwitcher.css'

export function WorkspaceSwitcher() {
  const { user } = useAuth()
  const { workspaces, isLoading, switchTo, create, error } = useWorkspaces()
  const [open, setOpen] = useState(false)
  const [creating, setCreating] = useState(false)
  const [newName, setNewName] = useState('')
  const [busy, setBusy] = useState(false)
  const containerRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    if (!open) return
    function onDocClick(e: MouseEvent) {
      if (!containerRef.current?.contains(e.target as Node)) {
        setOpen(false)
        setCreating(false)
        setNewName('')
      }
    }
    document.addEventListener('mousedown', onDocClick)
    return () => document.removeEventListener('mousedown', onDocClick)
  }, [open])

  if (!user) return null

  const currentName = user.workspaceName?.trim() || 'Default workspace'

  async function onSwitch(id: string) {
    if (busy) return
    setBusy(true)
    try {
      await switchTo(id)
    } catch {
      // error surface comes from useWorkspaces.error
    } finally {
      setBusy(false)
    }
  }

  async function onCreate(e: React.FormEvent) {
    e.preventDefault()
    if (busy) return
    const trimmed = newName.trim()
    if (!trimmed) return
    setBusy(true)
    try {
      await create(trimmed)
      setCreating(false)
      setNewName('')
      // Reload to ensure workspace-scoped data refetches.
      window.location.reload()
    } catch {
      // error surface comes from useWorkspaces.error
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="workspace-switcher" ref={containerRef}>
      <button
        type="button"
        className="workspace-switcher__current"
        onClick={() => setOpen(o => !o)}
        aria-expanded={open}
      >
        <span className="workspace-switcher__label">Workspace</span>
        <span className="workspace-switcher__name" title={currentName}>{currentName}</span>
        <span className="workspace-switcher__caret" aria-hidden>{open ? '▴' : '▾'}</span>
      </button>

      {open && (
        <div className="workspace-switcher__menu" role="menu">
          {isLoading && <div className="workspace-switcher__loading">Loading…</div>}
          {!isLoading && workspaces.length === 0 && (
            <div className="workspace-switcher__empty">No workspaces.</div>
          )}
          {!isLoading && workspaces.map(ws => (
            <button
              key={ws.id}
              type="button"
              className={`workspace-switcher__item ${ws.isCurrent ? 'is-current' : ''}`}
              onClick={() => { if (!ws.isCurrent) void onSwitch(ws.id) }}
              disabled={busy || ws.isCurrent}
              role="menuitem"
            >
              <span className="workspace-switcher__item-name">{ws.name}</span>
              <span className="workspace-switcher__item-role">{ws.role}</span>
              {ws.isCurrent && <span className="workspace-switcher__check" aria-hidden>✓</span>}
            </button>
          ))}

          <div className="workspace-switcher__divider" />

          {!creating ? (
            <button
              type="button"
              className="workspace-switcher__create-btn"
              onClick={() => setCreating(true)}
              disabled={busy}
            >
              + New workspace
            </button>
          ) : (
            <form className="workspace-switcher__create-form" onSubmit={onCreate}>
              <input
                autoFocus
                type="text"
                className="workspace-switcher__create-input"
                placeholder="Workspace name"
                value={newName}
                onChange={e => setNewName(e.target.value)}
                maxLength={200}
                disabled={busy}
              />
              <div className="workspace-switcher__create-actions">
                <button type="submit" disabled={busy || !newName.trim()}>Create</button>
                <button
                  type="button"
                  onClick={() => { setCreating(false); setNewName('') }}
                  disabled={busy}
                >
                  Cancel
                </button>
              </div>
            </form>
          )}

          {error && <div className="workspace-switcher__error">{describeError(error)}</div>}
        </div>
      )}
    </div>
  )
}

function describeError(err: string): string {
  if (err.includes('workspaces_switch_forbidden')) {
    return 'You are not a member of that workspace.'
  }
  if (err.includes('workspaces_create_failed_400')) {
    return 'Could not create workspace. Check the name.'
  }
  return 'Something went wrong. Please try again.'
}
