import { describe, expect, it } from 'vitest'
import { getClientValidationRule, preValidateFile, preValidateImageDimensions } from './mediaValidationRules'

/**
 * Mirror-rule tests: the client table must match backend MediaValidationRules.cs
 * (backend is authoritative). Video duration/aspect cannot be probed client-side at
 * selection time — the mirrored values below are what the backend enforces and what
 * the server-driven validation card blocks on, so pinning them here keeps the two
 * tables from drifting.
 */
describe('client rule table mirrors the backend MVP limits', () => {
  it('Facebook images: 10MB max (was 4MB) on Feed and Story', () => {
    expect(getClientValidationRule('facebook', 'Feed', 'Image')?.maxBytes).toBe(10 * 1024 * 1024)
    expect(getClientValidationRule('facebook', 'Story', 'Image')?.maxBytes).toBe(10 * 1024 * 1024)
  })

  it('Instagram images: stay at the 8MB platform limit on Feed and Story', () => {
    expect(getClientValidationRule('instagram', 'Feed', 'Image')?.maxBytes).toBe(8 * 1024 * 1024)
    expect(getClientValidationRule('instagram', 'Story', 'Image')?.maxBytes).toBe(8 * 1024 * 1024)
  })

  it('video size caps: Facebook and Instagram Feed + Story are all 50MB (52,428,800 bytes)', () => {
    expect(getClientValidationRule('facebook', 'Feed', 'Video')?.maxBytes).toBe(50 * 1024 * 1024)
    expect(getClientValidationRule('facebook', 'Feed', 'Video')?.maxBytes).toBe(52_428_800)
    expect(getClientValidationRule('facebook', 'Story', 'Video')?.maxBytes).toBe(50 * 1024 * 1024)
    expect(getClientValidationRule('facebook', 'Story', 'Video')?.maxBytes).toBe(52_428_800)
    expect(getClientValidationRule('instagram', 'Feed', 'Video')?.maxBytes).toBe(50 * 1024 * 1024)
    expect(getClientValidationRule('instagram', 'Feed', 'Video')?.maxBytes).toBe(52_428_800)
    expect(getClientValidationRule('instagram', 'Story', 'Video')?.maxBytes).toBe(50 * 1024 * 1024)
    expect(getClientValidationRule('instagram', 'Story', 'Video')?.maxBytes).toBe(52_428_800)
  })

  it('feed videos are 3–180 seconds (product/MVP cap) on both platforms', () => {
    for (const platform of ['facebook', 'instagram']) {
      const rule = getClientValidationRule(platform, 'Feed', 'Video')
      expect(rule?.durationMinSeconds).toBe(3)
      expect(rule?.durationMaxSeconds).toBe(180)
    }
  })

  it('Facebook Story videos are 3–90 seconds; Instagram Story videos stay 3–60 seconds', () => {
    const fb = getClientValidationRule('facebook', 'Story', 'Video')
    expect(fb?.durationMinSeconds).toBe(3)
    expect(fb?.durationMaxSeconds).toBe(90)

    const ig = getClientValidationRule('instagram', 'Story', 'Video')
    expect(ig?.durationMinSeconds).toBe(3)
    expect(ig?.durationMaxSeconds).toBe(60)
  })

  it('Instagram Feed images use the 4:5–1.91:1 aspect window (9:16 is video-only)', () => {
    const rule = getClientValidationRule('instagram', 'Feed', 'Image')
    expect(rule?.aspectRatioMin).toBe(0.8)
    expect(rule?.aspectRatioMax).toBe(1.91)
  })

  it('Instagram Feed videos have NO aspect-ratio rule (any orientation, incl. 9:16, passes)', () => {
    const rule = getClientValidationRule('instagram', 'Feed', 'Video')
    expect(rule?.aspectRatioMin).toBeUndefined()
    expect(rule?.aspectRatioMax).toBeUndefined()
  })

  it('Instagram Feed IMAGE keeps only 8MB + aspect (no dimension/advisory/quality fields)', () => {
    // Finalized policy: mirror the backend removal of every dimension/advisory/quality field.
    const rule = getClientValidationRule('instagram', 'Feed', 'Image')
    expect(rule?.minWidth).toBeUndefined()
    expect(rule?.minHeight).toBeUndefined()
    expect(rule?.maxWidth).toBeUndefined()
    expect(rule?.maxHeight).toBeUndefined()
    expect(rule?.maxWidthIsAdvisory).toBeUndefined()
    // Kept:
    expect(rule?.maxBytes).toBe(8 * 1024 * 1024)
    expect(rule?.aspectRatioMin).toBe(0.8)
    expect(rule?.aspectRatioMax).toBe(1.91)
  })

  it('Instagram Feed VIDEO keeps only 50MB + 3–180s (no dimension/aspect fields)', () => {
    const rule = getClientValidationRule('instagram', 'Feed', 'Video')
    expect(rule?.minWidth).toBeUndefined()
    expect(rule?.minHeight).toBeUndefined()
    expect(rule?.maxWidth).toBeUndefined()
    expect(rule?.maxHeight).toBeUndefined()
    // Kept:
    expect(rule?.maxBytes).toBe(50 * 1024 * 1024)
    expect(rule?.durationMinSeconds).toBe(3)
    expect(rule?.durationMaxSeconds).toBe(180)
  })

  it('Instagram Feed CAROUSEL video is capped at 60s, single video at 180s (distinct)', () => {
    const single = getClientValidationRule('instagram', 'Feed', 'Video')
    const carousel = getClientValidationRule('instagram', 'Feed', 'Video', { carousel: true })

    expect(single?.durationMaxSeconds).toBe(180)
    expect(carousel?.durationMaxSeconds).toBe(60)
    expect(carousel?.durationMinSeconds).toBe(3)
    // Carousel images have no override — same rule object as a single image.
    expect(getClientValidationRule('instagram', 'Feed', 'Image', { carousel: true }))
      .toEqual(getClientValidationRule('instagram', 'Feed', 'Image'))
  })

  it('Facebook Story media has NO dimension or aspect-ratio rules (type + size + duration only)', () => {
    const image = getClientValidationRule('facebook', 'Story', 'Image')
    expect(image?.minWidth).toBeUndefined()
    expect(image?.minHeight).toBeUndefined()
    expect(image?.maxWidth).toBeUndefined()
    expect(image?.maxHeight).toBeUndefined()
    expect(image?.aspectRatioMin).toBeUndefined()
    expect(image?.aspectRatioMax).toBeUndefined()
    expect(image?.preferredAspectRatio).toBeUndefined()

    const video = getClientValidationRule('facebook', 'Story', 'Video')
    expect(video?.minWidth).toBeUndefined()
    expect(video?.minHeight).toBeUndefined()
    expect(video?.maxWidth).toBeUndefined()
    expect(video?.maxHeight).toBeUndefined()
    expect(video?.aspectRatioMin).toBeUndefined()
    expect(video?.aspectRatioMax).toBeUndefined()
    // Kept: supported duration range (Facebook Story is 3–90 s).
    expect(video?.durationMinSeconds).toBe(3)
    expect(video?.durationMaxSeconds).toBe(90)
  })

  it('Instagram Story media has NO dimension, aspect, FPS, codec, or advisory rules', () => {
    const image = getClientValidationRule('instagram', 'Story', 'Image')
    expect(image?.minWidth).toBeUndefined()
    expect(image?.minHeight).toBeUndefined()
    expect(image?.maxWidth).toBeUndefined()
    expect(image?.maxHeight).toBeUndefined()
    expect(image?.aspectRatioMin).toBeUndefined()
    expect(image?.aspectRatioMax).toBeUndefined()
    expect(image?.preferredAspectRatio).toBeUndefined()
    expect(image?.aspectRatioWarningTolerance).toBeUndefined()
    expect(image?.maxWidthIsAdvisory).toBeUndefined()
    expect(image?.allowedMimeTypes).toEqual(['image/jpeg', 'image/png'])
    expect(image?.maxBytes).toBe(8 * 1024 * 1024)

    const video = getClientValidationRule('instagram', 'Story', 'Video')
    expect(video?.minWidth).toBeUndefined()
    expect(video?.minHeight).toBeUndefined()
    expect(video?.maxWidth).toBeUndefined()
    expect(video?.maxHeight).toBeUndefined()
    expect(video?.aspectRatioMin).toBeUndefined()
    expect(video?.aspectRatioMax).toBeUndefined()
    expect(video?.durationMinSeconds).toBe(3)
    expect(video?.durationMaxSeconds).toBe(60)
  })
})

describe('pre-validation behavior at the new limits', () => {
  const file = (name: string, type: string, size: number): File => {
    // File constructor with a content array of the real size would be slow for 10MB+;
    // fake the size the same way the app reads it (the `size` property).
    const f = new File(['x'], name, { type })
    Object.defineProperty(f, 'size', { value: size })
    return f
  }

  it('accepts a 5MB Facebook image (over the old 4MB cap) and rejects over 10MB', () => {
    expect(preValidateFile(file('a.jpg', 'image/jpeg', 5 * 1024 * 1024), 'facebook', 'Feed')).toEqual([])
    expect(
      preValidateFile(file('a.jpg', 'image/jpeg', 11 * 1024 * 1024), 'facebook', 'Feed'),
    ).toHaveLength(1)
  })

  it('treats the Facebook 10MB image cap as inclusive (matches backend `size > max`)', () => {
    expect(
      preValidateFile(file('a.jpg', 'image/jpeg', 10 * 1024 * 1024), 'facebook', 'Feed'),
    ).toEqual([])
    expect(
      preValidateFile(file('a.jpg', 'image/jpeg', 10 * 1024 * 1024 + 1), 'facebook', 'Feed'),
    ).toHaveLength(1)
  })

  it('treats the Facebook Feed 50MB video cap as inclusive (File.size vs 52,428,800)', () => {
    expect(preValidateFile(file('a.mp4', 'video/mp4', 20 * 1024 * 1024), 'facebook', 'Feed')).toEqual([])
    expect(preValidateFile(file('a.mp4', 'video/mp4', 52_428_800), 'facebook', 'Feed')).toEqual([])
    expect(preValidateFile(file('a.mp4', 'video/mp4', 52_428_801), 'facebook', 'Feed')).toHaveLength(1)
  })

  it('treats the Facebook Story 50MB video cap as inclusive (File.size vs 52,428,800)', () => {
    // Exactly 50MB passes; one byte over is rejected. A comfortably-under file passes too.
    expect(preValidateFile(file('a.mp4', 'video/mp4', 20 * 1024 * 1024), 'facebook', 'Story')).toEqual([])
    expect(preValidateFile(file('a.mp4', 'video/mp4', 52_428_800), 'facebook', 'Story')).toEqual([])
    expect(preValidateFile(file('a.mp4', 'video/mp4', 52_428_801), 'facebook', 'Story')).toHaveLength(1)
  })

  it('treats the Instagram Feed 50MB video cap as inclusive (File.size vs 52,428,800)', () => {
    expect(preValidateFile(file('a.mp4', 'video/mp4', 52_428_800), 'instagram', 'Feed')).toEqual([])
    expect(preValidateFile(file('a.mp4', 'video/mp4', 52_428_801), 'instagram', 'Feed')).toHaveLength(1)
    expect(preValidateFile(file('a.mp4', 'video/mp4', 52_428_801), 'instagram', 'Feed')[0]).toContain('50.0MB')
  })

  it('treats the Instagram Story 50MB video cap as inclusive (File.size vs 52,428,800)', () => {
    expect(preValidateFile(file('a.mp4', 'video/mp4', 52_428_800), 'instagram', 'Story')).toEqual([])
    expect(preValidateFile(file('a.mp4', 'video/mp4', 52_428_801), 'instagram', 'Story')).toHaveLength(1)
    expect(preValidateFile(file('a.mp4', 'video/mp4', 52_428_801), 'instagram', 'Story')[0]).toContain('50.0MB')
  })

  it('rejects an Instagram image over 8MB', () => {
    expect(
      preValidateFile(file('a.jpg', 'image/jpeg', 9 * 1024 * 1024), 'instagram', 'Feed'),
    ).toHaveLength(1)
  })

  it('rejects a 9:16 Instagram Feed image but accepts 4:5 and 1.91:1', () => {
    expect(preValidateImageDimensions(1080, 1920, 'instagram', 'Feed')).toHaveLength(1)
    expect(preValidateImageDimensions(1080, 1350, 'instagram', 'Feed')).toEqual([])
    expect(preValidateImageDimensions(1337, 700, 'instagram', 'Feed')).toEqual([])
  })

  it('no longer enforces Instagram Feed image dimensions (only aspect remains)', () => {
    // Below the old 320×320 floor and above the old 1080×1350 / 1440-wide limits, but square
    // (aspect 1.0, in range) → accepted. Proves the removed min/max dimension rules are gone.
    expect(preValidateImageDimensions(100, 100, 'instagram', 'Feed')).toEqual([])
    expect(preValidateImageDimensions(2000, 2000, 'instagram', 'Feed')).toEqual([])
    expect(preValidateImageDimensions(5000, 5000, 'instagram', 'Feed')).toEqual([])
    // The only remaining rejection reason is aspect, e.g. an extreme 4:1 banner.
    expect(preValidateImageDimensions(4000, 1000, 'instagram', 'Feed')).toHaveLength(1)
  })

  it('keeps 9:16 valid for Facebook Feed images', () => {
    expect(preValidateImageDimensions(1080, 1920, 'facebook', 'Feed')).toEqual([])
  })

  it('accepts any Facebook Story image shape (no dimension or aspect pre-check)', () => {
    const shapes: [number, number][] = [
      [1080, 1080], // square
      [1920, 1080], // landscape
      [3000, 300], // extremely wide
      [300, 3000], // extremely tall
      [100, 100], // below the old 320x320 minimum
      [4000, 6000], // above the old 1080x1920 maximum
    ]
    for (const [w, h] of shapes) {
      expect(preValidateImageDimensions(w, h, 'facebook', 'Story')).toEqual([])
    }
  })

  it('accepts any Instagram Story image shape (no dimension or aspect pre-check)', () => {
    const shapes: [number, number][] = [
      [1080, 1080],
      [1920, 1080],
      [3000, 300],
      [300, 3000],
      [100, 100],
      [4000, 6000],
    ]
    for (const [w, h] of shapes) {
      expect(preValidateImageDimensions(w, h, 'instagram', 'Story')).toEqual([])
    }
  })
})
