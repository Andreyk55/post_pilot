import { describe, expect, it } from 'vitest'

import type { MediaValidationError } from '../api/media'
import {
  applySchedulePostMediaValidationUpdate,
  clearSchedulePostMediaValidation,
  hasBlockingSchedulePostMediaValidation,
  shouldRenderSchedulePostMediaValidationError,
  startSchedulePostMediaValidation,
  type SchedulePostMediaValidationState,
} from './schedulePostMediaValidation'

const aspectError: MediaValidationError = {
  code: 'aspect_ratio_invalid',
  field: 'aspectRatio',
  message: 'Aspect ratio is invalid.',
  expected: '1.91:1',
  actual: '4:3',
}

const invalidStateForOwner = (ownerKey: string): SchedulePostMediaValidationState => ({
  status: 'Invalid',
  errors: [aspectError],
  ownerKey,
})

describe('schedulePostMediaValidation', () => {
  describe('owner-key lifecycle', () => {
    it('clears the rendered error the instant a new upload starts — not after it finishes', () => {
      const stale = invalidStateForOwner('session-A')
      // Precondition: the red panel is showing for the previous (invalid) media.
      expect(shouldRenderSchedulePostMediaValidationError(stale, 'media/old.jpg')).toBe(true)

      // A new upload session begins: a Pending update arrives *before* the new
      // media has finished uploading/validating.
      const onStart = applySchedulePostMediaValidationUpdate(stale, 'Pending', [], 'session-B')

      expect(onStart).toEqual({ status: 'Pending', errors: [], ownerKey: 'session-B' })
      // The old red panel is gone immediately, while the new media is still pending.
      expect(shouldRenderSchedulePostMediaValidationError(onStart, 'media/new.jpg')).toBe(false)
      // ...and submission stays blocked while the new media is pending.
      expect(hasBlockingSchedulePostMediaValidation('media/new.jpg', onStart.status)).toBe(true)
    })

    it('ignores a late validation result from a superseded upload session', () => {
      const pendingB = startSchedulePostMediaValidation('session-B')

      // The previous upload (session-A) finally resolves as Invalid *after* B began.
      const afterStale = applySchedulePostMediaValidationUpdate(
        pendingB,
        'Invalid',
        [aspectError],
        'session-A',
      )

      expect(afterStale).toBe(pendingB) // unchanged — stale result dropped
      expect(shouldRenderSchedulePostMediaValidationError(afterStale, 'media/new.jpg')).toBe(false)
    })

    it('applies a terminal result that belongs to the active session', () => {
      const pendingB = startSchedulePostMediaValidation('session-B')
      const resolved = applySchedulePostMediaValidationUpdate(
        pendingB,
        'Invalid',
        [aspectError],
        'session-B',
      )

      expect(resolved).toEqual({ status: 'Invalid', errors: [aspectError], ownerKey: 'session-B' })
      expect(shouldRenderSchedulePostMediaValidationError(resolved, 'media/new.jpg')).toBe(true)
    })

    it('adopts the incoming result when no session is active yet (after a clear)', () => {
      const cleared = clearSchedulePostMediaValidation()
      const resolved = applySchedulePostMediaValidationUpdate(cleared, 'Valid', [], 'session-C')

      expect(resolved).toEqual({ status: 'Valid', errors: [], ownerKey: 'session-C' })
    })
  })

  it('treats pending validation as blocking while media is selected', () => {
    expect(hasBlockingSchedulePostMediaValidation('media/key.jpg', 'Pending')).toBe(true)
    expect(hasBlockingSchedulePostMediaValidation('media/key.jpg', 'Invalid')).toBe(true)
    expect(hasBlockingSchedulePostMediaValidation('media/key.jpg', 'Warning')).toBe(false)
    expect(hasBlockingSchedulePostMediaValidation('media/key.jpg', 'Valid')).toBe(false)
    expect(hasBlockingSchedulePostMediaValidation(null, 'Pending')).toBe(false)
  })

  it('clears validation state when media is removed or the form is reset', () => {
    expect(clearSchedulePostMediaValidation()).toEqual({
      status: null,
      errors: [],
      ownerKey: null,
    })
    expect(
      shouldRenderSchedulePostMediaValidationError(clearSchedulePostMediaValidation(), 'media/key.jpg'),
    ).toBe(false)
  })

  it('never renders the error panel once media is removed, even if the last result was Invalid', () => {
    const invalid = invalidStateForOwner('session-A')
    expect(shouldRenderSchedulePostMediaValidationError(invalid, null)).toBe(false)
  })

  // The reducer is the single source of truth for the single-media uploader, which
  // backs every Facebook/Instagram surface that uses MediaUpload (Stories) and the
  // generic single-media feed. These cases pin the "stale error vanishes the instant
  // a new upload starts" guarantee across each surface + media type the bug report
  // calls out, so a regression on any one of them fails loudly. Each surface differs
  // only in its realistic error payload + media key; the behavior must be identical.
  describe('a new upload start clears the previous media error on every surface', () => {
    const surfaces: { label: string; error: MediaValidationError; oldKey: string; newKey: string }[] = [
      {
        // Facebook Story has no dimension/aspect validation, so its realistic blocking error is
        // a supported-type or file-size failure — not a 9:16 aspect error.
        label: 'Facebook Story (image, file size)',
        error: { code: 'file_too_large', field: 'sizeBytes', message: 'This image is too large. Facebook images can be up to 10MB.', expected: '10MB', actual: '12.0MB' },
        oldKey: 'fb-story:1',
        newKey: 'fb-story:2',
      },
      {
        label: 'Instagram Story (image, file size)',
        error: { code: 'file_too_large', field: 'sizeBytes', message: 'This image is too large. Instagram images can be up to 8MB.', expected: '8MB', actual: '9.0MB' },
        oldKey: 'ig-story:1',
        newKey: 'ig-story:2',
      },
      {
        label: 'Facebook Reel (video, duration)',
        error: { code: 'duration_too_long', field: 'duration', message: 'Reel video is too long.', expected: '<= 90s', actual: '120s' },
        oldKey: 'fb-reel:1',
        newKey: 'fb-reel:2',
      },
      {
        label: 'Instagram Reel (video, duration)',
        error: { code: 'duration_too_long', field: 'duration', message: 'Reel video is too long.', expected: '<= 90s', actual: '180s' },
        oldKey: 'ig-reel:1',
        newKey: 'ig-reel:2',
      },
      {
        label: 'Single image replacement (dimensions)',
        error: { code: 'dimensions_too_small', field: 'dimensions', message: 'Image is too small.', expected: '>= 320px', actual: '100px' },
        oldKey: 'single-image:1',
        newKey: 'single-image:2',
      },
      {
        label: 'Single video replacement (codec)',
        error: { code: 'codec_unsupported', field: 'codec', message: 'Video codec is not supported.', expected: 'h264', actual: 'hevc' },
        oldKey: 'single-video:1',
        newKey: 'single-video:2',
      },
    ]

    it.each(surfaces)('$label: invalid error disappears immediately while the new upload is pending', ({ error, oldKey, newKey }) => {
      const invalid: SchedulePostMediaValidationState = { status: 'Invalid', errors: [error], ownerKey: oldKey }
      // The red panel is showing for the previous (invalid) media.
      expect(shouldRenderSchedulePostMediaValidationError(invalid, 'media/old')).toBe(true)

      // A new upload begins — Pending arrives before the new media has validated.
      const onStart = applySchedulePostMediaValidationUpdate(invalid, 'Pending', [], newKey)

      expect(onStart).toEqual({ status: 'Pending', errors: [], ownerKey: newKey })
      // Old error gone immediately, even though the new media is still pending...
      expect(shouldRenderSchedulePostMediaValidationError(onStart, 'media/new')).toBe(false)
      // ...and submission stays blocked while the new media is pending.
      expect(hasBlockingSchedulePostMediaValidation('media/new', onStart.status)).toBe(true)
    })

    it.each(surfaces)('$label: a late invalid result from the superseded upload never reappears', ({ error, oldKey, newKey }) => {
      const pendingNew = startSchedulePostMediaValidation(newKey)
      // The previous upload finally resolves Invalid *after* the new upload began.
      const afterStale = applySchedulePostMediaValidationUpdate(pendingNew, 'Invalid', [error], oldKey)

      expect(afterStale).toBe(pendingNew) // unchanged — stale result dropped
      expect(shouldRenderSchedulePostMediaValidationError(afterStale, 'media/new')).toBe(false)
    })
  })
})
