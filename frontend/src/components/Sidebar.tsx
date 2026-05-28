import { useAuth } from '../hooks/useAuth'
import { WorkspaceSwitcher } from './WorkspaceSwitcher'
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
  const { user, logout } = useAuth()

  const initial = user?.displayName?.trim().charAt(0).toUpperCase() || 'U'
  const name = user?.displayName || 'User'

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

      {user && <WorkspaceSwitcher />}

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
          <div className="user-meta">
            <span className="user-name">{name}</span>
            {user?.email && <span className="user-email">{user.email}</span>}
          </div>
        </div>
        <button
          type="button"
          className="logout-btn"
          onClick={() => { void logout() }}
          aria-label="Log out"
        >
          <svg
            className="logout-btn__icon"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden
          >
            <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4" />
            <polyline points="16 17 21 12 16 7" />
            <line x1="21" y1="12" x2="9" y2="12" />
          </svg>
          <span>Log out</span>
        </button>
      </div>
    </aside>
  )
}
