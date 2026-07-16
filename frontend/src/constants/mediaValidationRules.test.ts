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

  it('video size caps are unchanged: Facebook 200MB, Instagram 100MB', () => {
    expect(getClientValidationRule('facebook', 'Feed', 'Video')?.maxBytes).toBe(200 * 1024 * 1024)
    expect(getClientValidationRule('facebook', 'Story', 'Video')?.maxBytes).toBe(200 * 1024 * 1024)
    expect(getClientValidationRule('instagram', 'Feed', 'Video')?.maxBytes).toBe(100 * 1024 * 1024)
    expect(getClientValidationRule('instagram', 'Story', 'Video')?.maxBytes).toBe(100 * 1024 * 1024)
  })

  it('feed videos are 3–180 seconds (product/MVP cap) on both platforms', () => {
    for (const platform of ['facebook', 'instagram']) {
      const rule = getClientValidationRule(platform, 'Feed', 'Video')
      expect(rule?.durationMinSeconds).toBe(3)
      expect(rule?.durationMaxSeconds).toBe(180)
    }
  })

  it('story videos are 3–60 seconds (Meta limit) on both platforms', () => {
    for (const platform of ['facebook', 'instagram']) {
      const rule = getClientValidationRule(platform, 'Story', 'Video')
      expect(rule?.durationMinSeconds).toBe(3)
      expect(rule?.durationMaxSeconds).toBe(60)
    }
  })

  it('Instagram Feed images use the 4:5–1.91:1 aspect window (9:16 is video-only)', () => {
    const rule = getClientValidationRule('instagram', 'Feed', 'Image')
    expect(rule?.aspectRatioMin).toBe(0.8)
    expect(rule?.aspectRatioMax).toBe(1.91)
  })

  it('Instagram Feed videos (Reels) allow vertical 9:16', () => {
    const rule = getClientValidationRule('instagram', 'Feed', 'Video')
    expect(rule?.aspectRatioMin).toBeLessThanOrEqual(0.5625)
    expect(rule?.aspectRatioMax).toBe(1.91)
  })

  it('Facebook Story videos mirror the Meta minimum resolution (540x960)', () => {
    const rule = getClientValidationRule('facebook', 'Story', 'Video')
    expect(rule?.minWidth).toBe(540)
    expect(rule?.minHeight).toBe(960)
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

  it('keeps 9:16 valid for Facebook Feed images', () => {
    expect(preValidateImageDimensions(1080, 1920, 'facebook', 'Feed')).toEqual([])
  })
})
