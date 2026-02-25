import { describe, it, expect } from 'vitest'
import { canShowInstagramTags, canShowCarouselTags, buildPlacedTags, buildCarouselMediaTags } from './instagramTagging'
import type { MediaTag } from './instagramTagging'
import type { MediaType } from '../api/media'

describe('canShowInstagramTags', () => {
  // IG + image => visible
  it('shows tags for IG Feed single image', () => {
    expect(canShowInstagramTags(true, false, 'Image', false, true)).toBe(true)
  })

  // IG + video => visible
  it('shows tags for IG Feed single video', () => {
    expect(canShowInstagramTags(true, false, 'Video', false, true)).toBe(true)
  })

  // Non-IG => hidden
  it('hides tags for Facebook', () => {
    expect(canShowInstagramTags(false, false, 'Image', false, true)).toBe(false)
  })

  it('hides tags for Facebook video', () => {
    expect(canShowInstagramTags(false, false, 'Video', false, true)).toBe(false)
  })

  // Story => hidden
  it('hides tags for IG Story image', () => {
    expect(canShowInstagramTags(true, true, 'Image', false, true)).toBe(false)
  })

  it('hides tags for IG Story video', () => {
    expect(canShowInstagramTags(true, true, 'Video', false, true)).toBe(false)
  })

  // Carousel (multi-media) => hidden
  it('hides tags for IG carousel', () => {
    expect(canShowInstagramTags(true, false, 'Image', true, true)).toBe(false)
  })

  it('hides tags for IG video carousel', () => {
    expect(canShowInstagramTags(true, false, 'Video', true, true)).toBe(false)
  })

  // No media => hidden
  it('hides tags when no media uploaded', () => {
    expect(canShowInstagramTags(true, false, 'Image', false, false)).toBe(false)
  })

  it('hides tags when no media type', () => {
    expect(canShowInstagramTags(true, false, null, false, true)).toBe(false)
  })

  it('hides tags for None media type', () => {
    expect(canShowInstagramTags(true, false, 'None', false, true)).toBe(false)
  })
})

describe('canShowCarouselTags', () => {
  it('shows tags for IG Feed carousel', () => {
    expect(canShowCarouselTags(true, false, true)).toBe(true)
  })

  it('hides tags for non-IG carousel', () => {
    expect(canShowCarouselTags(false, false, true)).toBe(false)
  })

  it('hides tags for IG Story carousel', () => {
    expect(canShowCarouselTags(true, true, true)).toBe(false)
  })

  it('hides tags when not carousel', () => {
    expect(canShowCarouselTags(true, false, false)).toBe(false)
  })
})

describe('buildPlacedTags', () => {
  const unplacedTags: MediaTag[] = [
    { username: 'nike' },
    { username: 'adidas' },
  ]

  const placedTags: MediaTag[] = [
    { username: 'nike', x: 0.2, y: 0.3 },
    { username: 'adidas', x: 0.8, y: 0.9 },
  ]

  const mixedTags: MediaTag[] = [
    { username: 'nike', x: 0.2, y: 0.3 },
    { username: 'adidas' },
  ]

  // Video: auto-place at center
  it('auto-places unplaced video tags at center (0.5, 0.5)', () => {
    const result = buildPlacedTags(unplacedTags, true)
    expect(result).toEqual([
      { username: 'nike', x: 0.5, y: 0.5 },
      { username: 'adidas', x: 0.5, y: 0.5 },
    ])
  })

  it('preserves existing coordinates for video tags if already set', () => {
    const result = buildPlacedTags(placedTags, true)
    expect(result).toEqual([
      { username: 'nike', x: 0.2, y: 0.3 },
      { username: 'adidas', x: 0.8, y: 0.9 },
    ])
  })

  it('uses center fallback for unplaced video tags in mixed set', () => {
    const result = buildPlacedTags(mixedTags, true)
    expect(result).toEqual([
      { username: 'nike', x: 0.2, y: 0.3 },
      { username: 'adidas', x: 0.5, y: 0.5 },
    ])
  })

  // Image: only placed tags
  it('includes only placed image tags', () => {
    const result = buildPlacedTags(mixedTags, false)
    expect(result).toEqual([
      { username: 'nike', x: 0.2, y: 0.3 },
    ])
  })

  it('returns all placed image tags', () => {
    const result = buildPlacedTags(placedTags, false)
    expect(result).toEqual([
      { username: 'nike', x: 0.2, y: 0.3 },
      { username: 'adidas', x: 0.8, y: 0.9 },
    ])
  })

  it('returns empty for unplaced image tags', () => {
    const result = buildPlacedTags(unplacedTags, false)
    expect(result).toEqual([])
  })

  // Empty input
  it('returns empty array for empty tags', () => {
    expect(buildPlacedTags([], true)).toEqual([])
    expect(buildPlacedTags([], false)).toEqual([])
  })
})

describe('buildCarouselMediaTags', () => {
  it('builds per-item tags for image carousel', () => {
    const carouselTags = new Map<number, MediaTag[]>([
      [0, [{ username: 'nike', x: 0.2, y: 0.3 }]],
      [1, [{ username: 'adidas', x: 0.8, y: 0.9 }]],
    ])
    const mediaTypes = new Map<number, MediaType>([
      [0, 'Image'],
      [1, 'Image'],
    ])

    const result = buildCarouselMediaTags(carouselTags, mediaTypes)
    expect(result).toEqual({
      0: [{ username: 'nike', x: 0.2, y: 0.3 }],
      1: [{ username: 'adidas', x: 0.8, y: 0.9 }],
    })
  })

  it('auto-places video tags at center', () => {
    const carouselTags = new Map<number, MediaTag[]>([
      [0, [{ username: 'nike' }]],
    ])
    const mediaTypes = new Map<number, MediaType>([
      [0, 'Video'],
    ])

    const result = buildCarouselMediaTags(carouselTags, mediaTypes)
    expect(result).toEqual({
      0: [{ username: 'nike', x: 0.5, y: 0.5 }],
    })
  })

  it('skips items with no tags', () => {
    const carouselTags = new Map<number, MediaTag[]>([
      [0, [{ username: 'nike', x: 0.2, y: 0.3 }]],
      [1, []], // empty
    ])
    const mediaTypes = new Map<number, MediaType>([
      [0, 'Image'],
      [1, 'Image'],
    ])

    const result = buildCarouselMediaTags(carouselTags, mediaTypes)
    expect(result).toEqual({
      0: [{ username: 'nike', x: 0.2, y: 0.3 }],
    })
  })

  it('skips unplaced image tags', () => {
    const carouselTags = new Map<number, MediaTag[]>([
      [0, [{ username: 'nike' }]], // unplaced image tag
    ])
    const mediaTypes = new Map<number, MediaType>([
      [0, 'Image'],
    ])

    const result = buildCarouselMediaTags(carouselTags, mediaTypes)
    expect(result).toBeUndefined() // no valid tags
  })

  it('returns undefined for empty map', () => {
    const result = buildCarouselMediaTags(new Map(), new Map())
    expect(result).toBeUndefined()
  })

  it('handles mixed image+video carousel', () => {
    const carouselTags = new Map<number, MediaTag[]>([
      [0, [{ username: 'nike', x: 0.2, y: 0.3 }]], // image - placed
      [1, [{ username: 'adidas' }]], // video - auto-place
    ])
    const mediaTypes = new Map<number, MediaType>([
      [0, 'Image'],
      [1, 'Video'],
    ])

    const result = buildCarouselMediaTags(carouselTags, mediaTypes)
    expect(result).toEqual({
      0: [{ username: 'nike', x: 0.2, y: 0.3 }],
      1: [{ username: 'adidas', x: 0.5, y: 0.5 }],
    })
  })

  it('handles multiple tags per item', () => {
    const carouselTags = new Map<number, MediaTag[]>([
      [0, [
        { username: 'nike', x: 0.2, y: 0.3 },
        { username: 'adidas', x: 0.8, y: 0.9 },
      ]],
    ])
    const mediaTypes = new Map<number, MediaType>([
      [0, 'Image'],
    ])

    const result = buildCarouselMediaTags(carouselTags, mediaTypes)
    expect(result).toEqual({
      0: [
        { username: 'nike', x: 0.2, y: 0.3 },
        { username: 'adidas', x: 0.8, y: 0.9 },
      ],
    })
  })
})
