import { describe, expect, it } from 'vitest'
import { getUploadErrorMessage } from './uploadError'

describe('getUploadErrorMessage', () => {
  it('surfaces a server/API Error message when one is available', () => {
    expect(getUploadErrorMessage(new Error('Upload failed (HTTP 413): file too large'))).toBe(
      'Upload failed (HTTP 413): file too large',
    )
    expect(getUploadErrorMessage(new Error('Instagram Story requires a vertical video.'))).toBe(
      'Instagram Story requires a vertical video.',
    )
  })

  it('accepts a bare string error', () => {
    expect(getUploadErrorMessage('boom')).toBe('boom')
  })

  it('falls back to the generic message only when there is no usable detail', () => {
    expect(getUploadErrorMessage(new Error(''))).toBe('Upload failed. Please try again.')
    expect(getUploadErrorMessage(new Error('   '))).toBe('Upload failed. Please try again.')
    expect(getUploadErrorMessage(undefined)).toBe('Upload failed. Please try again.')
    expect(getUploadErrorMessage({})).toBe('Upload failed. Please try again.')
  })

  it('honors a caller-supplied fallback', () => {
    expect(getUploadErrorMessage(undefined, 'Couldn’t upload photo.jpg. Please try again.')).toBe(
      'Couldn’t upload photo.jpg. Please try again.',
    )
  })
})
