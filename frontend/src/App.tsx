import { useState } from 'react'
import { BrowserRouter, Routes, Route } from 'react-router-dom'
import { Sidebar } from './components/Sidebar'
import { Dashboard } from './pages/Dashboard'
import { SchedulePostsPage } from './pages/SchedulePostsPage'
import { ConnectedAccountsPage } from './pages/ConnectedAccountsPage'
import { AssetsPage } from './pages/AssetsPage'
import { PlaceholderPage } from './pages/PlaceholderPage'
import { MetaOAuthCallback } from './pages/MetaOAuthCallback'
import './App.css'

function MainApp() {
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
      case 'assets':
        return <AssetsPage onNavigate={setCurrentPage} />
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

function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/oauth/meta/callback" element={<MetaOAuthCallback />} />
        <Route path="/*" element={<MainApp />} />
      </Routes>
    </BrowserRouter>
  )
}

export default App