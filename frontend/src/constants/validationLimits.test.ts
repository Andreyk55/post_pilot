import { describe, expect, it } from 'vitest'
import {
  PostTextMaxCharsByPlatform,
  PostTextMaxLengthFacebook,
  PostTextMaxLengthInstagram,
  getPostTextMaxChars,
} from './validationLimits'
// Source pin (no DOM harness in this project — same pattern as SchedulePost.textLimit.test.ts).
import metaApiSource from '../api/meta.ts?raw'

/**
 * The composer's validity rule (SchedulePost): text is blocking when
 * `content.length > getPostTextMaxChars(platform)`. These tests pin the rule table to the
 * backend's ValidationLimits.cs values and exercise that exact comparison at the
 * boundaries, so the limits and the counting convention (UTF-16 code units — JS .length,
 * matching .NET string.Length) stay aligned across both layers.
 */
const isTextTooLong = (content: string, platformId: string | null) =>
  content.length > getPostTextMaxChars(platformId)

describe('placement-specific post text limits', () => {
  it('matches the backend rule table (Facebook Feed 5000, Instagram Feed 2200)', () => {
    expect(PostTextMaxLengthFacebook).toBe(5000)
    expect(PostTextMaxLengthInstagram).toBe(2200)
    expect(PostTextMaxCharsByPlatform.facebook).toBe(5000)
    expect(PostTextMaxCharsByPlatform.instagram).toBe(2200)
    expect(getPostTextMaxChars('facebook')).toBe(5000)
    expect(getPostTextMaxChars('instagram')).toBe(2200)
  })

  it('accepts exactly 5000 characters for Facebook and rejects 5001', () => {
    expect(isTextTooLong('x'.repeat(5000), 'facebook')).toBe(false)
    expect(isTextTooLong('x'.repeat(5001), 'facebook')).toBe(true)
  })

  it('accepts exactly 2200 characters for Instagram and rejects 2201', () => {
    expect(isTextTooLong('x'.repeat(2200), 'instagram')).toBe(false)
    expect(isTextTooLong('x'.repeat(2201), 'instagram')).toBe(true)
  })

  it('accepts ordinary short text and empty text on both platforms', () => {
    for (const platform of ['facebook', 'instagram']) {
      expect(isTextTooLong('', platform)).toBe(false)
      expect(isTextTooLong('an ordinary post #tag', platform)).toBe(false)
    }
  })
})

describe('placement isolation when switching platforms', () => {
  it('re-resolves the limit from the selected platform without touching the text', () => {
    // The same 3000-char draft: valid for Facebook Feed, invalid after switching the
    // selection to Instagram Feed, valid again after switching back — the content string
    // itself is never mutated by the limit logic.
    const content = 'x'.repeat(3000)

    expect(isTextTooLong(content, 'facebook')).toBe(false)
    expect(isTextTooLong(content, 'instagram')).toBe(true)
    expect(isTextTooLong(content, 'facebook')).toBe(false)
    expect(content).toHaveLength(3000)
  })
})

describe('counting convention (UTF-16 code units, aligned with the backend)', () => {
  it('counts line breaks and spaces toward the limit', () => {
    const atLimit = 'x'.repeat(2198) + '\n '
    expect(atLimit).toHaveLength(2200)
    expect(isTextTooLong(atLimit, 'instagram')).toBe(false)
    expect(isTextTooLong(atLimit + ' ', 'instagram')).toBe(true)
  })

  it('counts an emoji as two code units, same as .NET string.Length', () => {
    const emoji = '😀'
    expect(emoji).toHaveLength(2)

    // Instagram: 2198 + 2 = 2200 → valid; 2199 + 2 = 2201 → blocking.
    expect(isTextTooLong('x'.repeat(2198) + emoji, 'instagram')).toBe(false)
    expect(isTextTooLong('x'.repeat(2199) + emoji, 'instagram')).toBe(true)

    // Facebook: 4998 + 2 = 5000 → valid; 4999 + 2 = 5001 → blocking.
    expect(isTextTooLong('x'.repeat(4998) + emoji, 'facebook')).toBe(false)
    expect(isTextTooLong('x'.repeat(4999) + emoji, 'facebook')).toBe(true)
  })

  it('counts non-ASCII BMP characters as one code unit each', () => {
    expect(isTextTooLong('é'.repeat(2200), 'instagram')).toBe(false)
    expect(isTextTooLong('é'.repeat(2200) + 'й', 'instagram')).toBe(true)
  })
})

describe('limits are compile-time constants, never fetched over HTTP', () => {
  it('has no wrapper for the removed GET /api/meta/limits endpoint', () => {
    // The advisory endpoint was removed as unused (July 2026); the Meta API module must not
    // grow a limits call back — this rule table is the frontend's only source of limits.
    expect(metaApiSource).not.toContain('meta/limits')
    expect(metaApiSource).not.toContain('getLimits')
    expect(metaApiSource).not.toContain('ValidationLimitsResponse')
  })
})
