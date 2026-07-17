import { describe, expect, it } from 'vitest'
import {
  getMediaRequirementHint,
  resolveClientMediaError,
  resolveClientDimensionError,
  HEIC_NOT_SUPPORTED_MESSAGE,
} from './mediaRequirements'

const f = (type: string, size = 1000, name?: string) => ({ type, size, name })

describe('getMediaRequirementHint', () => {
  it('names the 50MB video cap for Facebook Feed only; Instagram Feed keeps duration-only wording', () => {
    expect(getMediaRequirementHint('facebook', 'Feed')).toBe(
      'Supported: JPG/PNG images (≤10 MB) • MP4/MOV videos (≤50 MB, 3–180 s)',
    )
    expect(getMediaRequirementHint('instagram', 'Feed')).toBe(
      'Supported: JPG/PNG images (≤8 MB) • MP4/MOV videos (3–180 s)',
    )
  })

  it('uses the Story video duration limit (3–60 s) and the per-platform image size', () => {
    expect(getMediaRequirementHint('facebook', 'Story')).toBe(
      'Supported: JPG/PNG images (≤10 MB) • MP4/MOV videos (3–60 s)',
    )
    expect(getMediaRequirementHint('instagram', 'Story')).toBe(
      'Supported: JPG/PNG images (≤8 MB) • MP4/MOV videos (3–60 s)',
    )
  })

  it('falls back to generic Feed limits when no platform is selected', () => {
    expect(getMediaRequirementHint(null, 'Feed')).toBe(
      'Supported: JPG/PNG images (≤10 MB) • MP4/MOV videos (3–180 s)',
    )
  })
})

describe('resolveClientMediaError — friendly, specific copy', () => {
  it('rejects an unsupported Instagram Story image type with "JPG or PNG"', () => {
    expect(resolveClientMediaError(f('image/webp'), 'instagram', 'Story')).toBe('Images must be JPG or PNG.')
  })

  it('rejects HEIC with dedicated copy (product limitation, by MIME or extension)', () => {
    expect(resolveClientMediaError(f('image/heic'), 'facebook', 'Feed')).toBe(HEIC_NOT_SUPPORTED_MESSAGE)
    expect(resolveClientMediaError(f('image/heif'), 'instagram', 'Feed')).toBe(HEIC_NOT_SUPPORTED_MESSAGE)
    // Browsers sometimes report an empty MIME type for HEIC — the extension still catches it.
    expect(resolveClientMediaError(f('', 1000, 'IMG_0001.HEIC'), 'facebook', 'Feed')).toBe(HEIC_NOT_SUPPORTED_MESSAGE)
  })

  it('reports the Instagram image limit (8MB) with resize guidance', () => {
    expect(resolveClientMediaError(f('image/jpeg', 9 * 1024 * 1024), 'instagram', 'Story')).toBe(
      'This image is too large. Instagram images can be up to 8MB. Large phone photos may need to be resized before upload.',
    )
  })

  it('reports the Facebook image limit (10MB) and accepts sizes between the old 4MB cap and 10MB', () => {
    expect(resolveClientMediaError(f('image/jpeg', 11 * 1024 * 1024), 'facebook', 'Feed')).toBe(
      'This image is too large. Facebook images can be up to 10MB. Large phone photos may need to be resized before upload.',
    )
    // 5MB was over the old 4MB Facebook cap; it must pass now.
    expect(resolveClientMediaError(f('image/jpeg', 5 * 1024 * 1024), 'facebook', 'Feed')).toBeNull()
    expect(resolveClientMediaError(f('image/jpeg', 5 * 1024 * 1024), 'facebook', 'Story')).toBeNull()
    // At the limit exactly → passes.
    expect(resolveClientMediaError(f('image/jpeg', 10 * 1024 * 1024), 'facebook', 'Feed')).toBeNull()
  })

  it('reports the exact video size limit (Instagram = 100MB)', () => {
    expect(resolveClientMediaError(f('video/mp4', 101 * 1024 * 1024), 'instagram', 'Story')).toBe(
      'This video is too large. Instagram videos can be up to 100MB.',
    )
  })

  it('reports the Facebook Feed 50MB video limit with an inclusive boundary', () => {
    expect(resolveClientMediaError(f('video/mp4', 52_428_801), 'facebook', 'Feed')).toBe(
      'This video is too large. Facebook videos can be up to 50MB.',
    )
    // Exactly 50 * 1024 * 1024 bytes passes; one byte over is rejected above.
    expect(resolveClientMediaError(f('video/mp4', 52_428_800), 'facebook', 'Feed')).toBeNull()
    expect(resolveClientMediaError(f('video/mp4', 20 * 1024 * 1024), 'facebook', 'Feed')).toBeNull()
  })

  it('names the allowed video type ("MP4") where only MP4 is allowed', () => {
    expect(resolveClientMediaError(f('video/webm'), 'twitter', 'Feed')).toBe('Videos must be MP4.')
  })

  it('says "Videos must be MP4 or MOV." for Facebook and Instagram (final policy)', () => {
    expect(resolveClientMediaError(f('video/webm'), 'facebook', 'Feed')).toBe('Videos must be MP4 or MOV.')
    expect(resolveClientMediaError(f('video/webm'), 'instagram', 'Feed')).toBe('Videos must be MP4 or MOV.')
    // MOV itself is accepted (passes type check → no error).
    expect(resolveClientMediaError(f('video/quicktime', 1000), 'facebook', 'Feed')).toBeNull()
    expect(resolveClientMediaError(f('video/quicktime', 1000), 'instagram', 'Feed')).toBeNull()
  })

  it('says "Images must be JPG or PNG." for Facebook and Instagram (final policy)', () => {
    expect(resolveClientMediaError(f('image/gif'), 'facebook', 'Feed')).toBe('Images must be JPG or PNG.')
    expect(resolveClientMediaError(f('image/webp'), 'instagram', 'Feed')).toBe('Images must be JPG or PNG.')
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

  it('does NOT flag any Facebook Story image shape (no dimension or aspect validation)', () => {
    // FB Story has no dimension/aspect rules: square, landscape, wide, tall, tiny, and huge
    // images all pass client-side (contrast with Instagram Story, which requires ~9:16 above).
    expect(resolveClientDimensionError(1080, 1080, 'facebook', 'Story')).toBeNull() // square
    expect(resolveClientDimensionError(1920, 1080, 'facebook', 'Story')).toBeNull() // landscape
    expect(resolveClientDimensionError(3000, 300, 'facebook', 'Story')).toBeNull()  // extremely wide
    expect(resolveClientDimensionError(300, 3000, 'facebook', 'Story')).toBeNull()  // extremely tall
    expect(resolveClientDimensionError(100, 100, 'facebook', 'Story')).toBeNull()   // below old 320x320
    expect(resolveClientDimensionError(4000, 6000, 'facebook', 'Story')).toBeNull() // above old 1080x1920
  })

  it('reports too-small images with the minimum size', () => {
    expect(resolveClientDimensionError(100, 100, 'instagram', 'Feed')).toBe(
      'Image is too small. Use at least 320×320px.',
    )
  })

  it('rejects Instagram Feed images taller than 4:5 with the ratio-range message', () => {
    // 9:16 (0.5625) is video-only on Instagram Feed — Meta rejects images below 4:5.
    expect(resolveClientDimensionError(1080, 1920, 'instagram', 'Feed')).toBe(
      'Instagram Feed images must use an aspect ratio between 4:5 and 1.91:1.',
    )
    expect(resolveClientDimensionError(1024, 1536, 'instagram', 'Feed')).toBe(
      'Instagram Feed images must use an aspect ratio between 4:5 and 1.91:1.',
    )
  })

  it('accepts Instagram Feed images at 4:5 and 1.91:1 exactly', () => {
    expect(resolveClientDimensionError(1080, 1350, 'instagram', 'Feed')).toBeNull() // 4:5
    expect(resolveClientDimensionError(1337, 700, 'instagram', 'Feed')).toBeNull() // 1.91:1
    expect(resolveClientDimensionError(1080, 1080, 'instagram', 'Feed')).toBeNull() // 1:1
  })

  it('keeps taller portraits valid for Facebook Feed (FB floor stays 9:16)', () => {
    expect(resolveClientDimensionError(1024, 1536, 'facebook', 'Feed')).toBeNull()
    expect(resolveClientDimensionError(1080, 1920, 'facebook', 'Feed')).toBeNull()
  })

  it('lets small-but-publishable feed images upload so the server can warn', () => {
    expect(resolveClientDimensionError(500, 500, 'facebook', 'Feed')).toBeNull()
    expect(resolveClientDimensionError(500, 500, 'instagram', 'Feed')).toBeNull()
  })

  it('lets slightly off-ratio Instagram Story images upload so the server can warn', () => {
    // Instagram Story still validates aspect (server warns near-9:16); Facebook Story has no
    // aspect check at all — its any-shape acceptance is covered by the dedicated test above.
    expect(resolveClientDimensionError(1080, 1800, 'instagram', 'Story')).toBeNull()
  })

  it('passes a correctly-sized vertical Story image (null)', () => {
    expect(resolveClientDimensionError(1080, 1920, 'instagram', 'Story')).toBeNull()
    expect(resolveClientDimensionError(1080, 1920, 'facebook', 'Story')).toBeNull()
  })
})
