/**
 * Shared, authoritative Instagram caption parsing/counting utility.
 *
 * This is the single frontend implementation that counts the entities inside a caption —
 * hashtags and @mentions — so the composer counter, the blocking validation, and the tests
 * all agree. It mirrors the backend `InstagramCaptionParser` / `PostContentRules` exactly so
 * the UI never accepts a caption the backend would reject.
 *
 * Counting rules (identical on both layers):
 * - Every occurrence counts, including duplicates: `#a #a` = 2 hashtags, `@a @a` = 2 mentions.
 * - A lone `#` or `@` with no entity text does not count.
 * - Hashtags are Unicode-aware (letters/numbers/underscore in any language) and must start at a
 *   non-word boundary, so `word#tag` is not counted.
 * - Mentions use the Instagram username alphabet (ASCII letters/digits/dot/underscore) and must
 *   start at a boundary that is not a word char or dot, so `person@example.com` is not counted.
 *
 * Caption @mentions (parsed here) are SEPARATE from Instagram media tags (usernames attached to
 * the image/video). Media tags are never counted by this utility.
 */

import {
  InstagramFeedMaxHashtags,
  InstagramFeedMaxMentions,
  InstagramFeedTooManyHashtagsMessage,
  InstagramFeedTooManyMentionsMessage,
} from '../constants/validationLimits'

/**
 * Source patterns for the two caption entities. Capture group 1 is the entity text without the
 * leading `#` / `@`. Kept as strings so callers can build fresh (stateless) RegExp instances and
 * so the same pattern can be reused by other caption consumers (e.g. mention extraction) instead
 * of re-declaring a competing regex.
 */
export const HASHTAG_PATTERN = '(?<![\\p{L}\\p{N}_])#([\\p{L}\\p{N}_]+)'
export const MENTION_PATTERN = '(?<![A-Za-z0-9_.])@([A-Za-z0-9._]{1,30})'

/** Counts hashtag occurrences in the caption (duplicates included). */
export function countHashtags(caption: string | null | undefined): number {
  if (!caption) return 0
  // 'u' flag is required for \p{...} Unicode property escapes.
  return (caption.match(new RegExp(HASHTAG_PATTERN, 'gu')) ?? []).length
}

/** Counts @mention occurrences in the caption (duplicates included). */
export function countMentions(caption: string | null | undefined): number {
  if (!caption) return 0
  return (caption.match(new RegExp(MENTION_PATTERN, 'g')) ?? []).length
}

export interface InstagramCaptionCounts {
  /** UTF-16 code-unit length (JS `.length`, matching the backend's `string.Length`). */
  charCount: number
  hashtagCount: number
  mentionCount: number
}

/** Derives all caption entity counts in one pass-through (pure; never mutates the caption). */
export function getInstagramCaptionCounts(caption: string | null | undefined): InstagramCaptionCounts {
  const text = caption ?? ''
  return {
    charCount: text.length,
    hashtagCount: countHashtags(text),
    mentionCount: countMentions(text),
  }
}

/**
 * Returns the Instagram Feed caption ENTITY-cap violations — too many hashtags and/or too many
 * @mentions — in a stable order (hashtags before mentions), or an empty array when acceptable.
 * Null/empty captions are always valid. Text-length is validated separately by the composer's
 * existing placement-specific limit (see constants/validationLimits `getPostTextMaxChars`), so
 * this focuses on the hashtag/mention caps to avoid a second, competing length message.
 */
export function getInstagramCaptionTagErrors(caption: string | null | undefined): string[] {
  const errors: string[] = []
  if (!caption) return errors

  if (countHashtags(caption) > InstagramFeedMaxHashtags) {
    errors.push(InstagramFeedTooManyHashtagsMessage)
  }
  if (countMentions(caption) > InstagramFeedMaxMentions) {
    errors.push(InstagramFeedTooManyMentionsMessage)
  }
  return errors
}
