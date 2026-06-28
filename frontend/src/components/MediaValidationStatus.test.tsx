import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { MediaValidationBadge, MediaValidationCard, MediaRequirementHint } from './MediaValidationStatus'
import { getMediaValidationBadgeDescriptor } from '../utils/mediaValidationBadge'
import type { MediaValidationView } from '../utils/mediaValidationView'

const view = (overrides: Partial<MediaValidationView>): MediaValidationView => ({
  status: 'valid',
  blocking: false,
  title: 'Media is publishable.',
  messages: [],
  recommendations: [],
  ...overrides,
})

describe('getMediaValidationBadgeDescriptor', () => {
  it('always reports the in-flight state while validating, regardless of last status', () => {
    expect(getMediaValidationBadgeDescriptor(true, 'Valid', true)).toEqual({
      label: 'Validating...',
      variant: 'validating',
    })
    expect(getMediaValidationBadgeDescriptor(true, null, false)).toEqual({
      label: 'Validating...',
      variant: 'validating',
    })
  })

  it('maps each terminal status to its badge', () => {
    expect(getMediaValidationBadgeDescriptor(false, 'Valid', false)).toEqual({ label: 'Valid', variant: 'valid' })
    expect(getMediaValidationBadgeDescriptor(false, 'Invalid', false)).toEqual({ label: 'Invalid', variant: 'invalid' })
    expect(getMediaValidationBadgeDescriptor(false, 'Warning', false)).toEqual({ label: 'Warning', variant: 'warning' })
  })

  it('shows Pending only when the surface opts in (single media with a platform selected)', () => {
    expect(getMediaValidationBadgeDescriptor(false, 'Pending', true)).toEqual({ label: 'Pending', variant: 'pending' })
    expect(getMediaValidationBadgeDescriptor(false, 'Pending', false)).toBeNull()
  })

  it('renders no badge when there is nothing to show', () => {
    expect(getMediaValidationBadgeDescriptor(false, null, true)).toBeNull()
    expect(getMediaValidationBadgeDescriptor(false, undefined, true)).toBeNull()
  })
})

describe('MediaValidationBadge', () => {
  it('renders the validating state consistently (same class + animation hook as Story)', () => {
    const markup = renderToStaticMarkup(<MediaValidationBadge validating />)
    expect(markup).toContain('class="validation-badge validating"')
    expect(markup).toContain('Validating...')
  })

  it('renders the positive valid/success state', () => {
    const markup = renderToStaticMarkup(<MediaValidationBadge status="Valid" />)
    expect(markup).toContain('class="validation-badge valid"')
    expect(markup).toContain('Valid')
  })

  it('renders the invalid state', () => {
    const markup = renderToStaticMarkup(<MediaValidationBadge status="Invalid" />)
    expect(markup).toContain('class="validation-badge invalid"')
    expect(markup).toContain('Invalid')
  })

  it('renders nothing for an idle Pending status unless showPending is set', () => {
    expect(renderToStaticMarkup(<MediaValidationBadge status="Pending" />)).toBe('')
    expect(renderToStaticMarkup(<MediaValidationBadge status="Pending" showPending />)).toContain(
      'class="validation-badge pending"',
    )
  })

  it('forwards the tooltip and extra positioning class', () => {
    const markup = renderToStaticMarkup(
      <MediaValidationBadge status="Invalid" title="Aspect ratio is invalid." className="carousel-item-badge" />,
    )
    expect(markup).toContain('class="validation-badge invalid carousel-item-badge"')
    expect(markup).toContain('title="Aspect ratio is invalid."')
  })
})

describe('MediaValidationCard', () => {
  it('renders nothing for a null view', () => {
    expect(renderToStaticMarkup(<MediaValidationCard view={null} />)).toBe('')
  })

  it('renders the neutral/green ready state', () => {
    const markup = renderToStaticMarkup(<MediaValidationCard view={view({ status: 'valid' })} />)
    expect(markup).toContain('class="media-validation-card valid"')
    expect(markup).toContain('Media is publishable.')
    expect(markup).toContain('role="status"')
  })

  it('renders a non-blocking yellow warning card with recommendations (not an alert)', () => {
    const markup = renderToStaticMarkup(
      <MediaValidationCard
        view={view({
          status: 'warning',
          title: 'Media is publishable.',
          recommendations: ['For best quality, use a higher-resolution image.'],
        })}
      />,
    )
    expect(markup).toContain('class="media-validation-card warning"')
    expect(markup).toContain('Media is publishable.')
    expect(markup).toContain('For best quality, use a higher-resolution image.')
    // A warning is a status, never a blocking alert.
    expect(markup).toContain('role="status"')
    expect(markup).not.toContain('role="alert"')
  })

  it('renders the red invalid card as an alert listing the blocking messages', () => {
    const markup = renderToStaticMarkup(
      <MediaValidationCard
        view={view({ status: 'invalid', title: 'Media cannot be published', blocking: true, messages: ['Story media should be vertical 9:16.'] })}
      />,
    )
    expect(markup).toContain('class="media-validation-card invalid"')
    expect(markup).toContain('Media cannot be published')
    expect(markup).toContain('Story media should be vertical 9:16.')
    expect(markup).toContain('role="alert"')
  })
})

describe('MediaRequirementHint', () => {
  it('renders the placement-driven requirement copy through one shared component', () => {
    expect(renderToStaticMarkup(<MediaRequirementHint platform="facebook" placement="Story" />)).toBe(
      '<div class="media-requirement-hint">1 photo or 1 video — vertical 9:16 recommended</div>',
    )
    expect(renderToStaticMarkup(<MediaRequirementHint platform="instagram" placement="Feed" />)).toContain(
      'Photo or video supported',
    )
  })
})
