import { renderToStaticMarkup } from 'react-dom/server'
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { MediaValidationBadge, MediaValidationCard, MediaRequirementHint, MediaValidationOverlay } from './MediaValidationStatus'
import { getMediaValidationBadgeDescriptor } from '../utils/mediaValidationBadge'
import type { MediaValidationView } from '../utils/mediaValidationView'

const mediaValidationStatusCss = readFileSync(new URL('./MediaValidationStatus.css', import.meta.url), 'utf8')

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
      icon: '...',
    })
    expect(getMediaValidationBadgeDescriptor(true, null, false)).toEqual({
      label: 'Validating...',
      variant: 'validating',
      icon: '...',
    })
  })

  it('maps each terminal status to its badge', () => {
    expect(getMediaValidationBadgeDescriptor(false, 'Valid', false)).toEqual({ label: 'Valid', variant: 'valid', icon: '✓' })
    expect(getMediaValidationBadgeDescriptor(false, 'Invalid', false)).toEqual({ label: 'Invalid', variant: 'invalid', icon: '✕' })
    expect(getMediaValidationBadgeDescriptor(false, 'Warning', false)).toEqual({ label: 'Warning', variant: 'warning', icon: '!' })
  })

  it('shows Pending only when the surface opts in (single media with a platform selected)', () => {
    expect(getMediaValidationBadgeDescriptor(false, 'Pending', true)).toEqual({ label: 'Pending', variant: 'pending', icon: '...' })
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
    expect(markup).toContain('class="validation-badge__icon"')
    expect(markup).toContain('Valid')
  })

  it('renders the invalid state', () => {
    const markup = renderToStaticMarkup(<MediaValidationBadge status="Invalid" />)
    expect(markup).toContain('class="validation-badge invalid"')
    expect(markup).toContain('class="validation-badge__icon"')
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

describe('MediaValidationOverlay', () => {
  it('renders a centered validating pill over the thumbnail when shown', () => {
    const markup = renderToStaticMarkup(<MediaValidationOverlay show />)
    expect(markup).toContain('class="validation-thumbnail-overlay"')
    expect(markup).toContain('role="status"')
    expect(markup).toContain('class="validation-thumbnail-overlay__pill"')
    expect(markup).toContain('class="validation-thumbnail-overlay__spinner"')
    expect(markup).toContain('Validating...')
  })

  it('renders nothing when hidden', () => {
    expect(renderToStaticMarkup(<MediaValidationOverlay />)).toBe('')
  })

  it('uses high-contrast badge and overlay CSS that remains readable on bright thumbnails', () => {
    expect(mediaValidationStatusCss).toMatch(/\.validation-badge \{[\s\S]*box-shadow:/)
    expect(mediaValidationStatusCss).toMatch(/\.validation-badge\.valid \{[\s\S]*background: rgba\([^;]+0\.96\);/)
    expect(mediaValidationStatusCss).toMatch(/\.validation-badge\.warning \{[\s\S]*background: rgba\([^;]+0\.96\);/)
    expect(mediaValidationStatusCss).toMatch(/\.validation-badge\.invalid \{[\s\S]*background: rgba\([^;]+0\.96\);/)
    expect(mediaValidationStatusCss).toMatch(/\.validation-thumbnail-overlay \{[\s\S]*inset: 0;[\s\S]*z-index: 5;[\s\S]*background: rgba/)
    expect(mediaValidationStatusCss).toMatch(/\.validation-thumbnail-overlay__pill \{[\s\S]*background: rgba\([^;]+0\.94\);/)
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
        view={view({ status: 'invalid', title: 'Media cannot be published', blocking: true, messages: ['This video is too large. Instagram videos can be up to 50MB.'] })}
      />,
    )
    expect(markup).toContain('class="media-validation-card invalid"')
    expect(markup).toContain('Media cannot be published')
    expect(markup).toContain('This video is too large. Instagram videos can be up to 50MB.')
    expect(markup).toContain('role="alert"')
  })
})

describe('MediaRequirementHint', () => {
  it('renders the placement-driven requirement copy through one shared component', () => {
    const storyMarkup = renderToStaticMarkup(<MediaRequirementHint platform="facebook" placement="Story" />)
    expect(storyMarkup).toContain('class="media-requirement-hint"')
    expect(storyMarkup).toContain('Supported: JPG/PNG images (≤10 MB)')
    expect(storyMarkup).toContain('≤50 MB, 3–90 s')

    const feedMarkup = renderToStaticMarkup(<MediaRequirementHint platform="instagram" placement="Feed" />)
    expect(feedMarkup).toContain('JPG/PNG images')
    expect(feedMarkup).toContain('≤8 MB')
    expect(feedMarkup).toContain('MP4/MOV videos')
    expect(feedMarkup).toContain('≤50 MB')
    expect(feedMarkup).toContain('3–180 s')
  })
})
