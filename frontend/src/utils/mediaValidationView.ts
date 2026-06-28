import type {
  ValidationStatus,
  MediaValidationError,
  MediaValidationWarning,
} from '../api/media'

/**
 * Normalized, platform-agnostic media-validation view model.
 *
 * Every Schedule Post media surface (single Story/Feed uploader and the
 * carousel/multi-photo uploader, for both Facebook and Instagram) renders from this
 * one shape, so the status/wording/colors are identical regardless of platform,
 * placement, or media type. The raw server result (`ValidationStatus` + error/warning
 * arrays) is mapped here once; the UI never branches on platform again.
 */

export type MediaValidationViewStatus = 'valid' | 'warning' | 'invalid' | 'pending'

export interface MediaValidationView {
  status: MediaValidationViewStatus
  /**
   * Whether this state must block Schedule/Publish. True only for hard failures
   * (invalid) and the in-flight check (pending) — never for advisory warnings.
   */
  blocking: boolean
  /** Card heading, e.g. "Media is ready" / "Media cannot be published". */
  title: string
  /** Hard problems that prevent publishing (rendered red). */
  messages: string[]
  /** Advisories that do not block publishing (rendered yellow). */
  recommendations: string[]
}

// Spec copy — kept here so both the single and multi uploaders read identically.
export const MEDIA_VIEW_TITLES: Record<MediaValidationViewStatus, string> = {
  valid: 'Media is ready',
  warning: 'Media can be published, but there are recommendations',
  invalid: 'Media cannot be published',
  pending: 'Checking media…',
}

const STATUS_SEVERITY: Record<MediaValidationViewStatus, number> = {
  invalid: 3,
  warning: 2,
  valid: 1,
  pending: 0,
}

/**
 * Maps a single item's server validation result into the shared view model.
 *
 * - `validating` (a request in flight) always wins → a neutral "Checking media…".
 * - Errors take precedence over warnings, matching the long-standing single-media
 *   behavior (media that fails outright shouldn't also nag about softer warnings).
 * - Returns `null` when there is nothing to show (idle/Pending without an in-flight
 *   check) so the card simply doesn't render.
 */
export function resolveMediaValidationView(
  status: ValidationStatus | null | undefined,
  errors: MediaValidationError[] = [],
  warnings: MediaValidationWarning[] = [],
  opts: { validating?: boolean } = {},
): MediaValidationView | null {
  if (opts.validating) {
    return { status: 'pending', blocking: true, title: MEDIA_VIEW_TITLES.pending, messages: [], recommendations: [] }
  }

  switch (status) {
    case 'Invalid':
      return {
        status: 'invalid',
        blocking: true,
        title: MEDIA_VIEW_TITLES.invalid,
        messages: errors.length > 0 ? errors.map(e => e.message) : ['Validation failed'],
        recommendations: [],
      }
    case 'Warning':
      return {
        status: 'warning',
        blocking: false,
        title: MEDIA_VIEW_TITLES.warning,
        messages: [],
        recommendations: warnings.map(w => w.message),
      }
    case 'Valid':
      return {
        status: 'valid',
        blocking: false,
        title: MEDIA_VIEW_TITLES.valid,
        messages: [],
        recommendations: [],
      }
    default:
      // Pending / null — nothing to render (the badge/placeholder covers in-flight).
      return null
  }
}

export interface AggregatableMediaItem {
  status: ValidationStatus
  errors: MediaValidationError[]
  warnings: MediaValidationWarning[]
  /** Human label for the item, e.g. "Image 2" or "Reel" — prefixes each message. */
  label: string
}

/**
 * Folds many carousel/multi-photo items into one view. The worst status wins
 * (invalid > warning > valid); each surfaced message is prefixed with its item label
 * so "Image 2: …" tells the user exactly which item is at fault. Errors take
 * precedence — when anything is invalid the card lists only blockers, so fixing them
 * (then re-validating) reveals any remaining warnings.
 */
export function aggregateMediaValidationViews(
  items: AggregatableMediaItem[],
): MediaValidationView | null {
  if (items.length === 0) return null

  const worst = items.reduce<MediaValidationViewStatus>((acc, item) => {
    const itemStatus = toViewStatus(item.status)
    return STATUS_SEVERITY[itemStatus] > STATUS_SEVERITY[acc] ? itemStatus : acc
  }, 'pending')

  if (worst === 'pending') return null

  if (worst === 'invalid') {
    const messages: string[] = []
    for (const item of items.filter(i => i.status === 'Invalid')) {
      if (item.errors.length > 0) {
        for (const err of item.errors) messages.push(`${item.label}: ${err.message}`)
      } else {
        messages.push(`${item.label}: Validation failed`)
      }
    }
    return { status: 'invalid', blocking: true, title: MEDIA_VIEW_TITLES.invalid, messages, recommendations: [] }
  }

  if (worst === 'warning') {
    const recommendations: string[] = []
    for (const item of items.filter(i => i.status === 'Warning')) {
      for (const warn of item.warnings) recommendations.push(`${item.label}: ${warn.message}`)
    }
    return { status: 'warning', blocking: false, title: MEDIA_VIEW_TITLES.warning, messages: [], recommendations }
  }

  return { status: 'valid', blocking: false, title: MEDIA_VIEW_TITLES.valid, messages: [], recommendations: [] }
}

function toViewStatus(status: ValidationStatus): MediaValidationViewStatus {
  switch (status) {
    case 'Invalid':
      return 'invalid'
    case 'Warning':
      return 'warning'
    case 'Valid':
      return 'valid'
    default:
      return 'pending'
  }
}
