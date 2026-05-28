import { useState, type ReactNode } from 'react'
import { BrowserRouter, Routes, Route, useLocation } from 'react-router-dom'
import { Sidebar } from './components/Sidebar'
import { Dashboard } from './pages/Dashboard'
import { SchedulePostsPage } from './pages/SchedulePostsPage'
import { MyPostsPage } from './pages/MyPostsPage'
import { ConnectedAccountsPage } from './pages/ConnectedAccountsPage'
import { AssetsPage } from './pages/AssetsPage'
import { PlaceholderPage } from './pages/PlaceholderPage'
import { MetaOAuthCallback } from './pages/MetaOAuthCallback'
import { AuthCallback } from './pages/AuthCallback'
import { PasswordGate } from './components/PasswordGate'
import { LoginScreen } from './components/LoginScreen'
import { AuthProvider, useAuth } from './hooks/useAuth'
import { WorkspacesProvider } from './hooks/useWorkspaces'
import './App.css'

function MainApp() {
  const [currentPage, setCurrentPage] = useState('dashboard')

  const renderPage = () => {
    switch (currentPage) {
      case 'dashboard':
        return <Dashboard />
      case 'schedule':
        return <SchedulePostsPage onNavigate={setCurrentPage} />
      case 'posts':
        return <MyPostsPage />
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

/**
 * Gates the inner app behind a successful Google sign-in. Sits inside
 * <PasswordGate> so the user has already cleared the global password gate
 * by the time this renders.
 */
function RequireAuth({ children }: { children: ReactNode }) {
  const { user, isLoading } = useAuth()
  const location = useLocation()

  if (isLoading) {
    return (
      <div className="auth-loading">
        <div className="auth-loading__spinner" />
      </div>
    )
  }

  if (!user) {
    // Surface error code if the user just bounced back from a failed callback.
    const params = new URLSearchParams(location.search)
    return <LoginScreen error={params.get('error')} />
  }

  return <>{children}</>
}

function App() {
  return (
    <PasswordGate>
      <BrowserRouter>
        <AuthProvider>
          <WorkspacesProvider>
            <Routes>
              <Route path="/oauth/meta/callback" element={<MetaOAuthCallback />} />
              <Route path="/auth/callback" element={<AuthCallback />} />
              <Route path="/*" element={<RequireAuth><MainApp /></RequireAuth>} />
            </Routes>
          </WorkspacesProvider>
        </AuthProvider>
      </BrowserRouter>
    </PasswordGate>
  )
}

export default App
