import { useAuth } from '../hooks/useAuth'
import './Sidebar.css'

interface SidebarProps {
  currentPage: string
  onNavigate: (page: string) => void
}

const navItems = [
  { id: 'dashboard', label: 'Dashboard', icon: '🏠' },
  { id: 'accounts', label: 'Connected Accounts', icon: '🔗' },
  { id: 'assets', label: 'Assets', icon: '📦' },
  { id: 'schedule', label: 'Schedule Posts', icon: '📅' },
  { id: 'posts', label: 'My Posts', icon: '📝' },
  { id: 'analytics', label: 'Analytics', icon: '📊' },
  { id: 'settings', label: 'Settings', icon: '⚙️' },
]

export function Sidebar({ currentPage, onNavigate }: SidebarProps) {
  const { user, isLoading, logout } = useAuth()

  const initial = user?.displayName?.trim().charAt(0).toUpperCase() || 'U'
  const name = user?.displayName || 'User'
  const workspaceLabel = user ? (user.workspaceName?.trim() || 'Default workspace') : null

  return (
    <aside className="sidebar">
      <div className="sidebar-logo">
        <div className="logo-icon">
          <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
            <path d="M12 2L2 7L12 12L22 7L12 2Z" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
            <path d="M2 17L12 22L22 17" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
            <path d="M2 12L12 17L22 12" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
          </svg>
        </div>
        <span className="logo-text">Post Pilot</span>
      </div>

      {!isLoading && workspaceLabel && (
        <div className="sidebar-workspace" title={workspaceLabel}>
          <span className="sidebar-workspace__label">Workspace</span>
          <span className="sidebar-workspace__name">{workspaceLabel}</span>
        </div>
      )}

      <nav className="sidebar-nav">
        {navItems.map(item => (
          <button
            key={item.id}
            className={`nav-item ${currentPage === item.id ? 'active' : ''}`}
            onClick={() => onNavigate(item.id)}
          >
            <span className="nav-icon">{item.icon}</span>
            <span className="nav-label">{item.label}</span>
          </button>
        ))}
      </nav>

      <div className="sidebar-footer">
        <div className="user-info">
          {user?.avatarUrl ? (
            <img className="user-avatar user-avatar--img" src={user.avatarUrl} alt="" />
          ) : (
            <div className="user-avatar">{initial}</div>
          )}
          <span className="user-name">{name}</span>
          <button
            type="button"
            className="user-logout"
            onClick={() => { void logout() }}
            title="Sign out"
            aria-label="Sign out"
          >
            ⎋
          </button>
        </div>
      </div>
    </aside>
  )
}
