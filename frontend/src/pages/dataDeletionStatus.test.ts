import { describe, it, expect } from 'vitest'
import { describeDeletionStatus } from './dataDeletionStatus'

describe('describeDeletionStatus', () => {
  it('maps Processing to a pending tone', () => {
    const v = describeDeletionStatus('Processing')
    expect(v.tone).toBe('pending')
    expect(v.title).toMatch(/progress/i)
  })

  it('maps Completed and AlreadyDeleted to success', () => {
    expect(describeDeletionStatus('Completed').tone).toBe('success')
    expect(describeDeletionStatus('AlreadyDeleted').tone).toBe('success')
  })

  it('maps Failed to an error tone', () => {
    const v = describeDeletionStatus('Failed')
    expect(v.tone).toBe('error')
    expect(v.message).toMatch(/support/i)
  })

  it('falls back to an error tone for an unknown status', () => {
    expect(describeDeletionStatus('Weird').tone).toBe('error')
  })
})
