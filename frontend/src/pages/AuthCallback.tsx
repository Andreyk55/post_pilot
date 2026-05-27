import { useEffect, useRef, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { useAuth } from '../hooks/useAuth'
import './AuthCallback.css'

type CallbackState = 'processing' | 'success' | 'error'

export function AuthCallback() {
  const [searchParams] = useSearchParams()
  const navigate = useNavigate()
  const { refreshUser } = useAuth()
  const [state, setState] = useState<CallbackState>('processing')
  const [errorCode, setErrorCode] = useState<string | null>(null)
  const handled = useRef(false)

  useEffect(() => {
    // StrictMode double-invocation guard.
    if (handled.current) return
    handled.current = true

    const err = searchParams.get('error')
    if (err) {
      setErrorCode(err)
      setState('error')
      return
    }

    refreshUser()
      .then((user) => {
        if (!user) {
          setErrorCode('no_session')
          setState('error')
          return
        }
        const returnUrl = searchParams.get('returnUrl') || '/'
        setState('success')
        navigate(returnUrl, { replace: true })
      })
      .catch(() => {
        setErrorCode('refresh_failed')
        setState('error')
      })
  }, [navigate, refreshUser, searchParams])

  return (
    <div className="auth-callback">
      <div className="auth-callback__card">
        {state === 'processing' && (
          <>
            <div className="auth-callback__spinner" />
            <p>Finishing sign-in…</p>
          </>
        )}
        {state === 'success' && <p>Signed in. Redirecting…</p>}
        {state === 'error' && (
          <>
            <h2>Sign-in failed</h2>
            <p className="auth-callback__error-msg">{describeError(errorCode)}</p>
            <button
              type="button"
              className="auth-callback__btn"
              onClick={() => navigate('/', { replace: true })}
            >
              Back to sign in
            </button>
          </>
        )}
      </div>
    </div>
  )
}

function describeError(code: string | null): string {
  switch (code) {
    case 'google_auth_failed':
      return 'Google sign-in was cancelled or failed.'
    case 'google_claims_missing':
      return 'Google did not return the required profile information.'
    case 'no_session':
      return 'No session was established. Please try again.'
    case 'refresh_failed':
      return 'Could not reach the server. Please try again.'
    default:
      return 'Unknown error.'
  }
}
