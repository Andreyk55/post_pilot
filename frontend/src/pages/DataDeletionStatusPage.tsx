import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { dataDeletionApi, type DataDeletionStatus } from '../api/dataDeletion'
import { describeDeletionStatus } from './dataDeletionStatus'
import './DataDeletionStatusPage.css'

type LoadState = 'loading' | 'loaded' | 'not-found' | 'error'

/**
 * Public, ungated page that shows the outcome of a data-deletion request by its
 * confirmation code. Route: /data-deletion/status/:confirmationCode
 */
export function DataDeletionStatusPage() {
  const { confirmationCode } = useParams<{ confirmationCode: string }>()
  // A missing code is decided up front (no synchronous setState in the effect).
  const [state, setState] = useState<LoadState>(confirmationCode ? 'loading' : 'not-found')
  const [status, setStatus] = useState<DataDeletionStatus | null>(null)

  useEffect(() => {
    if (!confirmationCode) return
    let cancelled = false
    dataDeletionApi
      .getStatus(confirmationCode)
      .then((result) => {
        if (cancelled) return
        if (result === null) {
          setState('not-found')
          return
        }
        setStatus(result)
        setState('loaded')
      })
      .catch(() => {
        if (!cancelled) setState('error')
      })
    return () => {
      cancelled = true
    }
  }, [confirmationCode])

  return (
    <div className="deletion-status-page">
      <div className="deletion-status-card">
        <h1>Data deletion status</h1>

        {state === 'loading' && <p className="deletion-status-muted">Checking status…</p>}

        {state === 'not-found' && (
          <p className="deletion-status-muted">
            We could not find a deletion request for this confirmation code.
          </p>
        )}

        {state === 'error' && (
          <p className="deletion-status-tone-error">
            We could not load the status right now. Please try again later.
          </p>
        )}

        {state === 'loaded' && status && (
          <StatusBody status={status} />
        )}

        <p className="deletion-status-links">
          <Link to="/data-deletion">About data deletion</Link>
        </p>
      </div>
    </div>
  )
}

function StatusBody({ status }: { status: DataDeletionStatus }) {
  const view = describeDeletionStatus(status.status)
  return (
    <div className={`deletion-status-body deletion-status-tone-${view.tone}`}>
      <h2>{view.title}</h2>
      <p>{view.message}</p>
      <dl className="deletion-status-meta">
        <div>
          <dt>Confirmation code</dt>
          <dd>{status.confirmationCode}</dd>
        </div>
        <div>
          <dt>Provider</dt>
          <dd>{status.provider}</dd>
        </div>
        <div>
          <dt>Requested</dt>
          <dd>{new Date(status.requestedAt).toLocaleString()}</dd>
        </div>
        {status.completedAt && (
          <div>
            <dt>Completed</dt>
            <dd>{new Date(status.completedAt).toLocaleString()}</dd>
          </div>
        )}
      </dl>
    </div>
  )
}
