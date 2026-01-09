import './Dashboard.css'

export function Dashboard() {
  return (
    <div className="dashboard">
      <h1>Dashboard</h1>
      <p className="page-subtitle">Welcome to Post Pilot</p>

      <div className="stats-grid">
        <div className="stat-card">
          <span className="stat-icon">📅</span>
          <div className="stat-info">
            <span className="stat-value">0</span>
            <span className="stat-label">Scheduled Posts</span>
          </div>
        </div>

        <div className="stat-card">
          <span className="stat-icon">✅</span>
          <div className="stat-info">
            <span className="stat-value">0</span>
            <span className="stat-label">Published</span>
          </div>
        </div>

        <div className="stat-card">
          <span className="stat-icon">📱</span>
          <div className="stat-info">
            <span className="stat-value">0</span>
            <span className="stat-label">Connected Accounts</span>
          </div>
        </div>

        <div className="stat-card">
          <span className="stat-icon">👀</span>
          <div className="stat-info">
            <span className="stat-value">0</span>
            <span className="stat-label">Total Reach</span>
          </div>
        </div>
      </div>

      <div className="quick-actions">
        <h2>Quick Actions</h2>
        <div className="actions-grid">
          <button className="action-card">
            <span className="action-icon">📝</span>
            <span className="action-label">Create Post</span>
          </button>
          <button className="action-card">
            <span className="action-icon">📅</span>
            <span className="action-label">Schedule Post</span>
          </button>
          <button className="action-card">
            <span className="action-icon">🔗</span>
            <span className="action-label">Connect Account</span>
          </button>
        </div>
      </div>
    </div>
  )
}