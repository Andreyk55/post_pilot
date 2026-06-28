import type {
  ValidationStatus,
  MediaValidationError,
  MediaValidationWarning,
} from '../api/media'
import { getMediaValidationBadgeDescriptor } from '../utils/mediaValidationBadge'
import './MediaValidationStatus.css'

/**
 * Shared media-validation status UI.
 *
 * Originally only the single-media uploader (used by Facebook/Instagram Stories)
 * rendered a clear "Validating…" → "Valid"/"Invalid"/"Warning" progression. Feed
 * and carousel surfaces showed validation inconsistently. These pieces are the one
 * source of truth so every surface looks and behaves the same — see MediaUpload and
 * MultiMediaUpload for the consumers. Stale-result gating stays in the consumers;
 * this module is purely presentational. The pure state→badge mapping lives in
 * utils/mediaValidationBadge so it can be unit-tested on its own.
 */

interface MediaValidationBadgeProps {
  /** True while a server validation request is in flight. */
  validating?: boolean
  status?: ValidationStatus | null
  /** Render a neutral "Pending" badge when idle (single media with a platform selected). */
  showPending?: boolean
  /** Optional tooltip — carousel thumbnails use it to surface the full message. */
  title?: string
  /** Extra class for positioning (e.g. absolute placement on a thumbnail). */
  className?: string
}

export function MediaValidationBadge({
  validating = false,
  status = null,
  showPending = false,
  title,
  className,
}: MediaValidationBadgeProps) {
  const descriptor = getMediaValidationBadgeDescriptor(validating, status, showPending)
  if (!descriptor) return null

  const classes = ['validation-badge', descriptor.variant, className]
    .filter(Boolean)
    .join(' ')

  return (
    <span className={classes} title={title}>
      {descriptor.label}
    </span>
  )
}

interface MediaValidationPanelProps {
  errors?: MediaValidationError[]
  warnings?: MediaValidationWarning[]
}

/**
 * Shared error/warning detail panel. Errors take precedence over warnings, matching
 * the long-standing single-media behavior (media that fails outright shouldn't also
 * nag about softer warnings).
 */
export function MediaValidationPanel({
  errors = [],
  warnings = [],
}: MediaValidationPanelProps) {
  if (errors.length > 0) {
    return (
      <div className="validation-errors">
        {errors.map((err, i) => (
          <div key={i} className="validation-error">
            {err.message}
          </div>
        ))}
      </div>
    )
  }

  if (warnings.length > 0) {
    return (
      <div className="validation-warnings">
        {warnings.map((warn, i) => (
          <div key={i} className="validation-warning">
            {warn.message}
          </div>
        ))}
      </div>
    )
  }

  return null
}
