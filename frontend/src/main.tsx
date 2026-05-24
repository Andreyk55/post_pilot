import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { installCredentialedFetch } from './api/httpClient'

// Must run before any API call so every fetch to the backend carries the
// private-access cookie. Idempotent — safe in StrictMode double-mount.
installCredentialedFetch()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
