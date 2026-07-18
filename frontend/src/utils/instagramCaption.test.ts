import { describe, expect, it } from 'vitest'
import {
  countHashtags,
  countMentions,
  getInstagramCaptionCounts,
  getInstagramCaptionTagErrors,
} from './instagramCaption'
import {
  InstagramFeedTooManyHashtagsMessage,
  InstagramFeedTooManyMentionsMessage,
} from '../constants/validationLimits'

/**
 * The shared caption parser is the single frontend implementation behind the composer's
 * hashtag/@mention counters AND its blocking validation, mirroring the backend
 * InstagramCaptionParser / PostContentRules so the UI never accepts a caption the backend
 * rejects. Occurrences are counted (duplicates included); media tags are a separate feature.
 */

const repeat = (token: string, count: number) => Array(count).fill(token).join(' ')

describe('countHashtags', () => {
  it('counts occurrences, including duplicates', () => {
    expect(countHashtags('#travel #summer')).toBe(2)
    expect(countHashtags('#travel #travel')).toBe(2)
    expect(countHashtags('#a #b #c')).toBe(3)
  })

  it('does not count a lone # or a mid-word #', () => {
    expect(countHashtags('#')).toBe(0)
    expect(countHashtags('# ')).toBe(0)
    expect(countHashtags('word#tag')).toBe(0)
  })

  it('is Unicode-aware (matches the app hashtag parser)', () => {
    expect(countHashtags('#путешествие #лето')).toBe(2)
    expect(countHashtags('#日本 #写真')).toBe(2)
    expect(countHashtags('#café')).toBe(1)
  })

  it('counts across newlines and around emojis', () => {
    expect(countHashtags('#a\n#b\n#c')).toBe(3)
    expect(countHashtags('#travel😀 more')).toBe(1)
  })

  it('handles the 30/31 boundary', () => {
    expect(countHashtags(repeat('#tag', 30))).toBe(30)
    expect(countHashtags(repeat('#tag', 31))).toBe(31)
  })

  it('returns 0 for empty/nullish input', () => {
    expect(countHashtags('')).toBe(0)
    expect(countHashtags(null)).toBe(0)
    expect(countHashtags(undefined)).toBe(0)
  })
})

describe('countMentions', () => {
  it('counts occurrences, including duplicates', () => {
    expect(countMentions('@account1 @account2')).toBe(2)
    expect(countMentions('@account @account')).toBe(2)
  })

  it('does not count a lone @ or an email address', () => {
    expect(countMentions('@')).toBe(0)
    expect(countMentions('reach me at person@example.com')).toBe(0)
    expect(countMentions('nested a@b handle')).toBe(0)
  })

  it('counts a dotted/underscored username once', () => {
    expect(countMentions('Follow @john_doe.99 today')).toBe(1)
  })

  it('handles the 20/21 boundary', () => {
    expect(countMentions(repeat('@user', 20))).toBe(20)
    expect(countMentions(repeat('@user', 21))).toBe(21)
  })

  it('returns 0 for empty/nullish input', () => {
    expect(countMentions('')).toBe(0)
    expect(countMentions(null)).toBe(0)
    expect(countMentions(undefined)).toBe(0)
  })
})

describe('getInstagramCaptionCounts', () => {
  it('derives char (UTF-16), hashtag, and mention counts from combined content', () => {
    const caption = 'Trip! @natgeo @nasa #travel #travel #space'
    const counts = getInstagramCaptionCounts(caption)
    expect(counts.charCount).toBe(caption.length)
    expect(counts.mentionCount).toBe(2)
    expect(counts.hashtagCount).toBe(3)
  })

  it('counts an emoji caption by UTF-16 code units', () => {
    // "😀" is one surrogate pair = 2 code units, matching the backend length rule.
    expect(getInstagramCaptionCounts('😀').charCount).toBe(2)
  })
})

describe('getInstagramCaptionTagErrors', () => {
  it('is empty for null/empty and within-cap captions', () => {
    expect(getInstagramCaptionTagErrors(null)).toEqual([])
    expect(getInstagramCaptionTagErrors('')).toEqual([])
    expect(getInstagramCaptionTagErrors(repeat('#tag', 30) + ' ' + repeat('@user', 20))).toEqual([])
  })

  it('flags 31 hashtags with the hashtag message', () => {
    expect(getInstagramCaptionTagErrors(repeat('#tag', 31))).toEqual([
      InstagramFeedTooManyHashtagsMessage,
    ])
  })

  it('flags 21 mentions with the mention message', () => {
    expect(getInstagramCaptionTagErrors(repeat('@user', 21))).toEqual([
      InstagramFeedTooManyMentionsMessage,
    ])
  })

  it('reports both errors (hashtag first) when both caps are exceeded', () => {
    const errors = getInstagramCaptionTagErrors(repeat('#tag', 31) + ' ' + repeat('@user', 21))
    expect(errors).toEqual([
      InstagramFeedTooManyHashtagsMessage,
      InstagramFeedTooManyMentionsMessage,
    ])
  })

  it('uses the exact preferred wording with the numeric caps', () => {
    expect(InstagramFeedTooManyHashtagsMessage).toBe(
      'Instagram Feed captions can contain at most 30 hashtags.',
    )
    expect(InstagramFeedTooManyMentionsMessage).toBe(
      'Instagram Feed captions can contain at most 20 @mentions.',
    )
  })
})

describe('parser is pure and idempotent (never mutates the caption)', () => {
  it('returns stable counts across repeated calls and leaves the caption untouched', () => {
    const caption = '@natgeo #travel #travel @natgeo'
    const before = caption
    expect(countMentions(caption)).toBe(2)
    expect(countHashtags(caption)).toBe(2)
    // Re-run: no regex lastIndex state leaks between calls.
    expect(countMentions(caption)).toBe(2)
    expect(countHashtags(caption)).toBe(2)
    expect(caption).toBe(before)
  })
})
