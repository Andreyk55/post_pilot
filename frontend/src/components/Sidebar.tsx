import { useAuth } from '../hooks/useAuth'
import { AvatarImage } from './AvatarImage'
import { BrandLogo } from './BrandLogo'
import { WorkspaceSwitcher } from './WorkspaceSwitcher'
import './Sidebar.css'

interface SidebarProps {
  currentPage: string
  onNavigate: (page: string) => void
}

const navItems = [
  { id: 'accounts', label: 'Connected Accounts', icon: '🔗' },
  { id: 'assets', label: 'Publishing Assets', icon: '📦' },
  { id: 'schedule', label: 'Schedule Posts', icon: '📅' },
  { id: 'posts', label: 'My Posts', icon: '📝' },
  { id: 'settings', label: 'Account Settings', icon: '⚙️' },
  { id: 'contact', label: 'Contact Us', icon: '✉️' },
]

export function Sidebar({ currentPage, onNavigate }: SidebarProps) {
  const { user, logout } = useAuth()

  const initial = user?.displayName?.trim().charAt(0).toUpperCase() || 'U'
  const name = user?.displayName || 'User'

  return (
    <aside className="sidebar">
      <div className="sidebar-content">
        <button className="sidebar-logo sidebar-logo--btn" onClick={() => onNavigate('home')} aria-label="Publish Harbor home">
          <div className="sidebar-logo__mark" aria-hidden>
            <BrandLogo variant="icon" alt="" className="sidebar-logo__image" />
          </div>
          <span className="sidebar-logo__name">
            <span>Publish</span>
            <span className="sidebar-logo__name-accent">Harbor</span>
          </span>
        </button>

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
      </div>

      <div className="sidebar-footer">
        <div className="user-info">
          <div className="user-avatar">
            <AvatarImage
              src={user?.avatarUrl}
              alt=""
              className="user-avatar user-avatar--img"
              fallback={initial}
            />
          </div>
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
