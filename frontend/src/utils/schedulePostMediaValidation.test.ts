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
})
