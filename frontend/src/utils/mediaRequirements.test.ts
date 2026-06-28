import { describe, expect, it } from 'vitest'
import {
  getMediaRequirementHint,
  resolveClientMediaError,
  resolveClientDimensionError,
} from './mediaRequirements'

const f = (type: string, size = 1000) => ({ type, size })

describe('getMediaRequirementHint', () => {
  it('uses identical, placement-driven copy for Facebook and Instagram', () => {
    expect(getMediaRequirementHint('facebook', 'Feed')).toBe('Photo or video supported')
    expect(getMediaRequirementHint('instagram', 'Feed')).toBe('Photo or video supported')
    expect(getMediaRequirementHint('facebook', 'Story')).toBe('1 photo or 1 video — vertical 9:16 recommended')
    // FB Story and IG Story read the same.
    expect(getMediaRequirementHint('instagram', 'Story')).toBe(getMediaRequirementHint('facebook', 'Story'))
  })
})

describe('resolveClientMediaError — friendly, specific copy', () => {
  it('rejects an unsupported Instagram Story image type with "JPG or PNG"', () => {
    expect(resolveClientMediaError(f('image/webp'), 'instagram', 'Story')).toBe('Images must be JPG or PNG.')
  })

  it('reports the exact image size limit (Instagram = 8MB)', () => {
    expect(resolveClientMediaError(f('image/jpeg', 9 * 1024 * 1024), 'instagram', 'Story')).toBe(
      'Image is larger than 8MB.',
    )
  })

  it('reports the exact video size limit (Instagram = 100MB)', () => {
    expect(resolveClientMediaError(f('video/mp4', 101 * 1024 * 1024), 'instagram', 'Story')).toBe(
      'Video is larger than 100MB.',
    )
  })

  it('names the allowed video type ("MP4") where only MP4 is allowed', () => {
    expect(resolveClientMediaError(f('video/webm'), 'twitter', 'Feed')).toBe('Videos must be MP4.')
  })

  it('rejects a non-media file', () => {
    expect(resolveClientMediaError(f('application/pdf'), 'instagram', 'Feed')).toBe(
      'Unsupported file type. Upload a photo or video.',
    )
  })

  it('passes a valid file (null)', () => {
    expect(resolveClientMediaError(f('image/jpeg', 1000), 'instagram', 'Story')).toBeNull()
  })
})

describe('resolveClientDimensionError — Story 9:16 and friends', () => {
  it('gives the recognizable vertical 9:16 message for a non-vertical Instagram Story image', () => {
    // Square image (aspect 1.0) is outside the Story 9:16 (0.5625) requirement.
    expect(resolveClientDimensionError(1080, 1080, 'instagram', 'Story')).toBe(
      'Story media should be vertical 9:16.',
    )
  })

  it('uses the same Story message for Facebook (FB and IG aligned)', () => {
    expect(resolveClientDimensionError(1080, 1080, 'facebook', 'Story')).toBe(
      'Story media should be vertical 9:16.',
    )
  })

  it('reports too-small images with the minimum size', () => {
    expect(resolveClientDimensionError(100, 100, 'instagram', 'Feed')).toBe(
      'Image is too small. Use at least 320×320px.',
    )
  })

  it('passes a correctly-sized vertical Story image (null)', () => {
    expect(resolveClientDimensionError(1080, 1920, 'instagram', 'Story')).toBeNull()
  })
})
