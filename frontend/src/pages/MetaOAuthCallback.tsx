import { useEffect, useState, useRef } from 'react'
import { useSearchParams } from 'react-router-dom'
import { metaApi } from '../api/meta'
import './MetaOAuthCallback.css'

export function MetaOAuthCallback() {
  const [searchParams] = useSearchParams()
  const [status, setStatus] = useState<'processing' | 'success' | 'error'>('processing')
  const [errorMessage, setErrorMessage] = useState<string>('')
  const hasProcessed = useRef(false)

  useEffect(() => {
    // Prevent duplicate calls (React StrictMode in dev calls useEffect twice)
    if (hasProcessed.current) return
    hasProcessed.current = true
    handleCallback()
  }, [])

  const handleCallback = async () => {
    const code = searchParams.get('code')
    const state = searchParams.get('state')
    const error = searchParams.get('error')
    const errorDescription = searchParams.get('error_description')

    // Check for OAuth errors
    if (error) {
      setStatus('error')
      setErrorMessage(errorDescription || 'Authorization was denied or cancelled.')
      notifyOpener('META_OAUTH_ERROR', { error, errorDescription })
      return
    }

    // Validate required params
    if (!code || !state) {
      setStatus('error')
      setErrorMessage('Missing authorization code or state parameter.')
      notifyOpener('META_OAUTH_ERROR', { error: 'missing_params' })
      return
    }

    try {
      // Complete OAuth and save connection immediately (identity-level only)
      await metaApi.completeOAuth(code, state)

      setStatus('success')

      // Notify the opener window that OAuth completed successfully
      notifyOpener('META_OAUTH_SUCCESS', {})

      // Close popup after a brief delay
      setTimeout(() => {
        window.close()
      }, 1500)
    } catch (err) {
      console.error('OAuth callback error:', err)
      setStatus('error')
      const errorMsg = err instanceof Error ? err.message : 'Unknown error'
      setErrorMessage(`Failed to complete authorization: ${errorMsg}`)
      notifyOpener('META_OAUTH_ERROR', { error: 'callback_failed' })
    }
  }

  const notifyOpener = (type: string, data: Record<string, unknown>) => {
    if (window.opener) {
      window.opener.postMessage({ type, ...data }, window.location.origin)
    }
  }

  const handleClose = () => {
    window.close()
  }

  return (
    <div className="oauth-callback-page">
      <div className="callback-card">
        {status === 'processing' && (
          <>
            <div className="callback-spinner"></div>
            <h2>Connecting to Meta...</h2>
            <p>Please wait while we complete the authorization.</p>
          </>
        )}

        {status === 'success' && (
          <>
            <div className="callback-icon success">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3">
                <polyline points="20 6 9 17 4 12" />
              </svg>
            </div>
            <h2>Connected Successfully!</h2>
            <p>This window will close automatically...</p>
          </>
        )}

        {status === 'error' && (
          <>
            <div className="callback-icon error">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3">
                <line x1="18" y1="6" x2="6" y2="18" />
                <line x1="6" y1="6" x2="18" y2="18" />
              </svg>
            </div>
            <h2>Connection Failed</h2>
            <p>{errorMessage}</p>
            <button className="close-btn" onClick={handleClose}>
              Close Window
            </button>
          </>
        )}
      </div>
    </div>
  )
}
