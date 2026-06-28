import { describe, expect, it } from 'vitest'

import type { MediaValidationError } from '../api/media'
import {
  clearSchedulePostMediaValidation,
  hasBlockingSchedulePostMediaValidation,
  startSchedulePostMediaValidation,
} from './schedulePostMediaValidation'

describe('schedulePostMediaValidation', () => {
  it('switches stale validation into a neutral pending state when a new upload starts', () => {
    const staleErrors: MediaValidationError[] = [
      {
        code: 'aspect_ratio_invalid',
        field: 'aspectRatio',
        message: 'Aspect ratio is invalid.',
        expected: '1.91:1',
        actual: '4:3',
      },
    ]

    expect(staleErrors).toHaveLength(1)

    expect(startSchedulePostMediaValidation()).toEqual({
      status: 'Pending',
      errors: [],
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
    })
  })
})