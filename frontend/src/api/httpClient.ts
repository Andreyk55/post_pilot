import { config } from '../config/appConfig'

/**
 * Installs a one-time fetch interceptor so every request to the PostPilot
 * API automatically carries the private-access cookie. Without this, each
 * scattered `fetch(...)` callsite would have to remember `credentials:
 * "include"`. The interceptor only adds credentials for same-origin or
 * API-base-URL requests so direct uploads to MinIO / signed S3 URLs (which
 * would fail CORS preflight with credentials) are left untouched.
 */
let installed = false

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
    return originalFetch(input, merged)
  }
}
