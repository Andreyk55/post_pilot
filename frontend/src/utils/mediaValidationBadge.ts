import type { ValidationStatus } from '../api/media'

/**
 * Pure mapping from media-validation state to the badge that represents it. Lives
 * apart from the MediaValidationStatus component so it can be unit-tested directly
 * (and so the component file only exports components — required for fast refresh).
 */

export type MediaValidationBadgeVariant =
  | 'validating'
  | 'valid'
  | 'invalid'
  | 'warning'
  | 'pending'

export interface MediaValidationBadgeDescriptor {
  label: string
  variant: MediaValidationBadgeVariant
}

/**
 * `validating` always wins: an in-flight check is shown as such regardless of any
 * previous status. A `Pending` status only produces a badge when `showPending` is
 * set — single media shows it once a platform is selected; carousel thumbnails stay
 * blank until there is a terminal result.
 */
export function getMediaValidationBadgeDescriptor(
  validating: boolean,
  status: ValidationStatus | null | undefined,
  showPending: boolean,
): MediaValidationBadgeDescriptor | null {
  if (validating) {
    return { label: 'Validating...', variant: 'validating' }
  }

  switch (status) {
    case 'Valid':
      return { label: 'Valid', variant: 'valid' }
    case 'Invalid':
      return { label: 'Invalid', variant: 'invalid' }
    case 'Warning':
      return { label: 'Warning', variant: 'warning' }
    case 'Pending':
      return showPending ? { label: 'Pending', variant: 'pending' } : null
    default:
      return null
  }
}
