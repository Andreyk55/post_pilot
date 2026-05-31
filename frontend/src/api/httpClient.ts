import { config } from '../config/appConfig'
import { emitWorkspaceGuard, parseWorkspaceGuard } from './workspaceGuard'

/**
 * Installs a one-time fetch interceptor so every request to the PostPilot
 * API automatically carries the private-access cookie. Without this, each
 * scattered `fetch(...)` callsite would have to remember `credentials:
 * "include"`. The interceptor only adds credentials for same-origin or
 * API-base-URL requests so direct uploads to MinIO / signed S3 URLs (which
 * would fail CORS preflight with credentials) are left untouched.
 *
 * It also centrally detects the backend's strict workspace-resolution errors
 * (409 WORKSPACE_NOT_SELECTED / 403 WORKSPACE_FORBIDDEN) and broadcasts a
 * single app-wide event (see workspaceGuard.ts), so the app shell can react
 * (toast + re-sync + open the workspace selector) without every callsite
 * re-implementing it. The original response is passed through unchanged — the
 * in-flight call still sees the failure and stops.
 */
let installed = false

/**
 * Peeks at an API error response for a workspace-guard code without consuming
 * the body the caller will read. Clones first; never throws.
 */
function inspectForWorkspaceGuard(response: Response): void {
  if (response.status !== 409 && response.status !== 403) return
  const contentType = response.headers.get('content-type') ?? ''
  if (!contentType.includes('application/json')) return

  // Clone so the original body stays readable by the actual caller.
  response
    .clone()
    .json()
    .then((body) => {
      const detail = parseWorkspaceGuard(response.status, body)
      if (detail) emitWorkspaceGuard(detail)
    })
    .catch(() => {
      /* non-JSON or already-consumed clone — nothing to do */
    })
}

export function installCredentialedFetch(): void {
  if (installed || typeof window === 'undefined') return
  installed = true

  const originalFetch = window.fetch.bind(window)
  const apiBase = config.apiBaseUrl

  let apiOrigin: string | null = null
  try {
    apiOrigin = new URL(apiBase, window.location.origin).origin
  } catch {
    apiOrigin = null
  }

  window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string'
      ? input
      : input instanceof URL
        ? input.toString()
        : input.url

    let isApi = false
    try {
      const target = new URL(url, window.location.origin)
      isApi = target.origin === window.location.origin
          || (apiOrigin !== null && target.origin === apiOrigin)
    } catch {
      isApi = false
    }

    if (!isApi) {
      return originalFetch(input, init)
    }

    const merged: RequestInit = {
      ...(init ?? {}),
      credentials: init?.credentials ?? 'include',
    }
    return originalFetch(input, merged).then((response) => {
      inspectForWorkspaceGuard(response)
      return response
    })
  }
}
