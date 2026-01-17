import { useState } from 'react'
import { Sidebar } from './components/Sidebar'
import { Dashboard } from './pages/Dashboard'
import { SchedulePostsPage } from './pages/SchedulePostsPage'
import { ConnectedAccountsPage } from './pages/ConnectedAccountsPage'
import { PlaceholderPage } from './pages/PlaceholderPage'
import './App.css'

function App() {
  const [currentPage, setCurrentPage] = useState('dashboard')

  const renderPage = () => {
    switch (currentPage) {
      case 'dashboard':
        return <Dashboard />
      case 'schedule':
        return <SchedulePostsPage />
      case 'posts':
        return <PlaceholderPage title="My Posts" icon="📝" />
      case 'accounts':
        return <ConnectedAccountsPage />
      case 'analytics':
        return <PlaceholderPage title="Analytics" icon="📊" />
      case 'settings':
        return <PlaceholderPage title="Settings" icon="⚙️" />
      default:
        return <Dashboard />
    }
  }

  return (
    <div className="app">
      <Sidebar currentPage={currentPage} onNavigate={setCurrentPage} />
      <main className="main-content">
        {renderPage()}
      </main>
    </div>
  )
}

export default App