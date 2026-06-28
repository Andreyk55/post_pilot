import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { MediaValidationBadge, MediaValidationPanel } from './MediaValidationStatus'
import { getMediaValidationBadgeDescriptor } from '../utils/mediaValidationBadge'
import type { MediaValidationError, MediaValidationWarning } from '../api/media'

const error = (message: string): MediaValidationError => ({
  code: 'invalid',
  field: 'aspectRatio',
  message,
  expected: null,
  actual: null,
})

const warning = (message: string): MediaValidationWarning => ({
  code: 'soft',
  field: 'duration',
  message,
  recommendation: null,
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

describe('MediaValidationPanel', () => {
  it('renders the error panel and hides warnings when media is invalid', () => {
    const markup = renderToStaticMarkup(
      <MediaValidationPanel errors={[error('Too small'), error('Wrong ratio')]} warnings={[warning('Long video')]} />,
    )
    expect(markup).toContain('class="validation-errors"')
    expect(markup).toContain('Too small')
    expect(markup).toContain('Wrong ratio')
    // Errors take precedence — softer warnings are suppressed.
    expect(markup).not.toContain('validation-warnings')
    expect(markup).not.toContain('Long video')
  })

  it('renders the warning panel when there are warnings but no errors', () => {
    const markup = renderToStaticMarkup(<MediaValidationPanel warnings={[warning('Long video')]} />)
    expect(markup).toContain('class="validation-warnings"')
    expect(markup).toContain('Long video')
  })

  it('renders nothing when there is no error or warning', () => {
    expect(renderToStaticMarkup(<MediaValidationPanel />)).toBe('')
    expect(renderToStaticMarkup(<MediaValidationPanel errors={[]} warnings={[]} />)).toBe('')
  })
})
