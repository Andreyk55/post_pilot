import { describe, expect, it } from 'vitest'
import type { MediaValidationError, MediaValidationWarning } from '../api/media'
import {
  resolveMediaValidationView,
  aggregateMediaValidationViews,
  type AggregatableMediaItem,
} from './mediaValidationView'

const error = (message: string): MediaValidationError => ({
  code: 'invalid',
  field: 'aspectRatio',
  message,
  expected: null,
  actual: null,
})

const warning = (message: string): MediaValidationWarning => ({
  code: 'soft',
  field: 'dimensions',
  message,
  recommendation: null,
})

describe('resolveMediaValidationView', () => {
  it('maps Valid to a ready, non-blocking view', () => {
    expect(resolveMediaValidationView('Valid', [], [])).toEqual({
      status: 'valid',
      blocking: false,
      title: 'Media is ready',
      messages: [],
      recommendations: [],
    })
  })

  it('treats a Warning as a non-blocking recommendation, never a failure', () => {
    const view = resolveMediaValidationView('Warning', [], [warning('Quality may be reduced.')])
    expect(view).toEqual({
      status: 'warning',
      blocking: false, // <-- a warning must NOT block publish
      title: 'Media can be published, but there are recommendations',
      messages: [],
      recommendations: ['Quality may be reduced.'],
    })
  })

  it('maps Invalid to a blocking view and lets errors take precedence over warnings', () => {
    const view = resolveMediaValidationView('Invalid', [error('Story must be 9:16.')], [warning('Ignored while invalid.')])
    expect(view).toEqual({
      status: 'invalid',
      blocking: true,
      title: 'Media cannot be published',
      messages: ['Story must be 9:16.'],
      recommendations: [],
    })
  })

  it('falls back to a generic message when Invalid carries no error detail', () => {
    expect(resolveMediaValidationView('Invalid', [], [])?.messages).toEqual(['Validation failed'])
  })

  it('renders nothing for idle/Pending without an in-flight check', () => {
    expect(resolveMediaValidationView('Pending', [], [])).toBeNull()
    expect(resolveMediaValidationView(null, [], [])).toBeNull()
  })

  it('always shows the neutral checking state while validating', () => {
    expect(resolveMediaValidationView('Valid', [], [], { validating: true })).toEqual({
      status: 'pending',
      blocking: true,
      title: 'Checking media…',
      messages: [],
      recommendations: [],
    })
  })

  it('is platform-agnostic — identical view for the same status (FB and IG render the same)', () => {
    // The resolver takes no platform argument, so a Facebook and an Instagram caller
    // with the same server result get byte-for-byte the same view.
    const a = resolveMediaValidationView('Warning', [], [warning('Below recommended size.')])
    const b = resolveMediaValidationView('Warning', [], [warning('Below recommended size.')])
    expect(a).toEqual(b)
  })
})

describe('aggregateMediaValidationViews', () => {
  const item = (
    label: string,
    status: AggregatableMediaItem['status'],
    errors: MediaValidationError[] = [],
    warnings: MediaValidationWarning[] = [],
  ): AggregatableMediaItem => ({ label, status, errors, warnings })

  it('returns null for no items or all-pending items', () => {
    expect(aggregateMediaValidationViews([])).toBeNull()
    expect(aggregateMediaValidationViews([item('Image 1', 'Pending')])).toBeNull()
  })

  it('reports ready when every item is valid', () => {
    const view = aggregateMediaValidationViews([item('Image 1', 'Valid'), item('Image 2', 'Valid')])
    expect(view).toMatchObject({ status: 'valid', blocking: false, title: 'Media is ready' })
  })

  it('lets the worst status win (invalid > warning > valid) and blocks only on invalid', () => {
    const view = aggregateMediaValidationViews([
      item('Image 1', 'Valid'),
      item('Image 2', 'Warning', [], [warning('Soft issue.')]),
      item('Image 3', 'Invalid', [error('Too small.')]),
    ])
    expect(view?.status).toBe('invalid')
    expect(view?.blocking).toBe(true)
    // Each blocker message is prefixed with its item label.
    expect(view?.messages).toEqual(['Image 3: Too small.'])
  })

  it('surfaces warnings (with their labels) when nothing is invalid — does not block', () => {
    const view = aggregateMediaValidationViews([
      item('Image 1', 'Valid'),
      item('Video 2', 'Warning', [], [warning('Long video.')]),
    ])
    expect(view?.status).toBe('warning')
    expect(view?.blocking).toBe(false)
    expect(view?.recommendations).toEqual(['Video 2: Long video.'])
  })

  it('uses a per-item fallback message when an invalid item has no error detail', () => {
    const view = aggregateMediaValidationViews([item('Image 1', 'Invalid')])
    expect(view?.messages).toEqual(['Image 1: Validation failed'])
  })
})
