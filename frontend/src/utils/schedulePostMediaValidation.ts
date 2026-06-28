import type { MediaValidationError, ValidationStatus } from '../api/media'

export interface SchedulePostMediaValidationState {
  status: ValidationStatus | null
  errors: MediaValidationError[]
  /**
   * Identifies the upload session that produced this validation state. Each new
   * single-media upload (or re-validation) starts a fresh session. A validation
   * that resolves after a newer upload has begun carries a stale key and must be
   * ignored so it can't write an old error back over the current media.
   */
  ownerKey: string | null
}

export function startSchedulePostMediaValidation(
  ownerKey: string | null = null,
): SchedulePostMediaValidationState {
  return {
    status: 'Pending',
    errors: [],
    ownerKey,
  }
}

export function clearSchedulePostMediaValidation(): SchedulePostMediaValidationState {
  return {
    status: null,
    errors: [],
    ownerKey: null,
  }
}

/**
 * Folds a validation update from MediaUpload into the current state.
 *
 * A `Pending` update marks the (re)start of an upload session: it adopts the new
 * owner key and immediately drops any stale error/result, even when the previous
 * state belonged to a different session. This is what makes the red error panel
 * disappear the instant a new upload starts — before it finishes validating.
 *
 * A terminal update (Valid/Invalid/Warning) only applies when it belongs to the
 * session that is currently active. A late result from a superseded upload carries
 * a non-matching owner key and is ignored.
 */
export function applySchedulePostMediaValidationUpdate(
  prev: SchedulePostMediaValidationState,
  status: ValidationStatus,
  errors: MediaValidationError[],
  ownerKey: string | null,
): SchedulePostMediaValidationState {
  if (status === 'Pending') {
    return { status: 'Pending', errors: [], ownerKey }
  }

  if (prev.ownerKey !== null && ownerKey !== prev.ownerKey) {
    return prev
  }

  return { status, errors, ownerKey: ownerKey ?? prev.ownerKey }
}

export function hasBlockingSchedulePostMediaValidation(
  mediaUrl: string | null,
  status: ValidationStatus | null,
): boolean {
  return !!mediaUrl && (status === 'Invalid' || status === 'Pending')
}

/**
 * Single source of truth for whether the red "Media cannot be published" panel
 * renders. It shows only for a current-session Invalid result that carries at
 * least one error message while media is still selected — never for a stale
 * result left over from a previous upload.
 */
export function shouldRenderSchedulePostMediaValidationError(
  state: SchedulePostMediaValidationState,
  mediaUrl: string | null,
): boolean {
  return !!mediaUrl && state.status === 'Invalid' && state.errors.length > 0
}
