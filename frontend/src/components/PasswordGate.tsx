import { useEffect, useState, type ReactNode, type FormEvent } from 'react'
import { privateAccessApi } from '../api/privateAccess'
import './PasswordGate.css'

type GateState = 'loading' | 'locked' | 'unlocked' | 'error'

interface PasswordGateProps {
  children: ReactNode
}

export function PasswordGate({ children }: PasswordGateProps) {
  const [state, setState] = useState<GateState>('loading')
  const [password, setPassword] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    privateAccessApi
      .me()
      .then(({ hasAccess }) => {
        if (cancelled) return
        setState(hasAccess ? 'unlocked' : 'locked')
      })
      .catch(() => {
        if (cancelled) return
        setState('error')
      })
    return () => {
      cancelled = true
    }
  }, [])

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    if (submitting || !password) return
    setSubmitting(true)
    setErrorMessage(null)
    try {
      const { hasAccess } = await privateAccessApi.login(password)
      if (hasAccess) {
        setState('unlocked')
        setPassword('')
      } else {
        setErrorMessage('Incorrect password.')
      }
    } catch {
      setErrorMessage('Login failed. Please try again.')
    } finally {
      setSubmitting(false)
    }
  }

  if (state === 'unlocked') {
    return <>{children}</>
  }

  if (state === 'loading') {
    return (
      <div className="private-gate">
        <div className="private-gate__card">
          <div className="private-gate__loading">Loading…</div>
        </div>
      </div>
    )
  }

  if (state === 'error') {
    return (
      <div className="private-gate">
        <div className="private-gate__card">
          <p className="private-gate__message">
            Cannot reach the server. Check your connection and refresh.
          </p>
        </div>
      </div>
    )
  }

  return (
    <div className="private-gate">
      <form className="private-gate__card" onSubmit={onSubmit}>
        <p className="private-gate__message">This app is private. Enter the access password.</p>
        <input
          type="password"
          className="private-gate__input"
          placeholder="Password"
          autoFocus
          autoComplete="current-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          disabled={submitting}
        />
        {errorMessage && <div className="private-gate__error">{errorMessage}</div>}
        <button
          type="submit"
          className="private-gate__button"
          disabled={submitting || !password}
        >
          {submitting ? 'Checking…' : 'Enter'}
        </button>
      </form>
    </div>
  )
}
