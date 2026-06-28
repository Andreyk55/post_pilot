import type { MediaValidationError, ValidationStatus } from '../api/media'

export interface SchedulePostMediaValidationState {
  status: ValidationStatus | null
  errors: MediaValidationError[]
}

export function startSchedulePostMediaValidation(): SchedulePostMediaValidationState {
  return {
    status: 'Pending',
    errors: [],
  }
}

export function clearSchedulePostMediaValidation(): SchedulePostMediaValidationState {
  return {
    status: null,
    errors: [],
  }
}

export function hasBlockingSchedulePostMediaValidation(
  mediaUrl: string | null,
  status: ValidationStatus | null,
): boolean {
  return !!mediaUrl && (status === 'Invalid' || status === 'Pending')
}