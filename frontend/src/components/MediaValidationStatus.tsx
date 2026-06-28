import type { ValidationStatus } from '../api/media'
import type { MediaValidationView, MediaValidationViewStatus } from '../utils/mediaValidationView'
import { getMediaValidationBadgeDescriptor } from '../utils/mediaValidationBadge'
import { getMediaRequirementHint } from '../utils/mediaRequirements'
import type { Placement } from '../constants/mediaValidationRules'
import type { PlatformId } from '../constants/validationLimits'
import './MediaValidationStatus.css'

/**
 * Shared media-validation status UI — the single source of truth for how every
 * Schedule Post media surface presents validation. The single-media uploader
 * (Facebook/Instagram Stories + generic single feed) and the carousel/multi-photo
 * uploader both render these pieces, so the badge, the status card, and the
 * requirement hint look and behave identically across platform, placement, and media
 * type. Stale-result gating stays in the consumers; this module is purely
 * presentational. The pure state→view mapping lives in utils/mediaValidationView and
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

const CARD_ICONS: Record<MediaValidationViewStatus, string> = {
  valid: '✓',
  warning: '⚠',
  invalid: '✕',
  pending: '⏳',
}

interface MediaValidationCardProps {
  view: MediaValidationView | null
}

/**
 * Shared validation card rendered below the media area. One look for every surface:
 * green/neutral "Media is ready", yellow "…recommendations" (non-blocking, never
 * confused with a failure), red "Media cannot be published". Driven entirely by the
 * normalized MediaValidationView so Facebook and Instagram are byte-for-byte identical.
 */
export function MediaValidationCard({ view }: MediaValidationCardProps) {
  if (!view) return null

  return (
    <div
      className={`media-validation-card ${view.status}`}
      role={view.status === 'invalid' ? 'alert' : 'status'}
    >
      <div className="media-validation-card__title">
        <span className="media-validation-card__icon" aria-hidden="true">
          {CARD_ICONS[view.status]}
        </span>
        {view.title}
      </div>
      {view.messages.length > 0 && (
        <ul className="media-validation-card__list">
          {view.messages.map((message, i) => (
            <li key={i}>{message}</li>
          ))}
        </ul>
      )}
      {view.recommendations.length > 0 && (
        <ul className="media-validation-card__list media-validation-card__list--recommendation">
          {view.recommendations.map((recommendation, i) => (
            <li key={i}>{recommendation}</li>
          ))}
        </ul>
      )}
    </div>
  )
}

interface MediaRequirementHintProps {
  platform?: PlatformId | string | null
  placement?: Placement | string
}

/**
 * Shared "what's allowed here" hint shown before upload. Same component + styling for
 * every platform/placement so Facebook Story and Instagram Story (etc.) read alike.
 */
export function MediaRequirementHint({ platform = null, placement = 'Feed' }: MediaRequirementHintProps) {
  return <div className="media-requirement-hint">{getMediaRequirementHint(platform, placement)}</div>
}
