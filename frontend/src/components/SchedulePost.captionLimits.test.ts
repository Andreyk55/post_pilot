import { describe, expect, it } from 'vitest'
// Source-level guarantees for the Instagram Feed caption hashtag/@mention caps in the composer.
// The cap VALUES, counting, and messages are tested in utils/instagramCaption.test.ts and
// constants/validationLimits.test.ts (the shared rule/parser, mirrored from the backend); these
// pins prove the composer derives its counters from that shared parser, renders `current/cap`,
// shows the blocking error, and gates both submit buttons — the same source-pin approach as
// SchedulePost.textLimit.test.ts (this project configures no DOM harness).
import schedulePostSource from './SchedulePost.tsx?raw'

describe('SchedulePost - Instagram Feed caption hashtag/mention caps', () => {
  it('derives counts from the shared instagramCaption parser (no competing inline regex)', () => {
    expect(schedulePostSource).toContain(
      "import { countHashtags, countMentions } from '../utils/instagramCaption'",
    )
    expect(schedulePostSource).toContain('mentionCount: countMentions(content)')
    expect(schedulePostSource).toContain('hashtagCount: countHashtags(content)')
    // The old inline, de-duplicating regexes must be gone.
    expect(schedulePostSource).not.toMatch(/mentionRegex|hashtagRegex/)
  })

  it('imports the caps and messages from the shared rule table (not hardcoded)', () => {
    for (const symbol of [
      'InstagramFeedMaxHashtags',
      'InstagramFeedMaxMentions',
      'InstagramFeedTooManyHashtagsMessage',
      'InstagramFeedTooManyMentionsMessage',
    ]) {
      expect(schedulePostSource).toContain(symbol)
    }
  })

  it('renders the live counters as current/cap for mentions and hashtags', () => {
    expect(schedulePostSource).toContain('{captionSummary.mentionCount}/{InstagramFeedMaxMentions}')
    expect(schedulePostSource).toContain('{captionSummary.hashtagCount}/{InstagramFeedMaxHashtags}')
  })

  it('keeps media tags counted and displayed SEPARATELY from caption mentions', () => {
    // Media tags come from mediaTags.length, never merged into the mention count.
    expect(schedulePostSource).toContain('mentionCount: countMentions(content)')
    expect(schedulePostSource).toContain('const mediaTagCount = mediaTags.length')
    expect(schedulePostSource).toContain('Media tags: {captionSummary.mediaTagCount}')
  })

  it('treats the caps as blocking only for Instagram Feed captions', () => {
    expect(schedulePostSource).toMatch(
      /const isTooManyHashtags = isInstagramSelected && !isStory && captionSummary\.hashtagCount > InstagramFeedMaxHashtags/,
    )
    expect(schedulePostSource).toMatch(
      /const isTooManyMentions = isInstagramSelected && !isStory && captionSummary\.mentionCount > InstagramFeedMaxMentions/,
    )
    expect(schedulePostSource).toContain(
      'const hasCaptionTagError = isTooManyHashtags || isTooManyMentions',
    )
  })

  it('gates Schedule and Publish Now on the caption cap flag (both feed branches)', () => {
    const occurrences = schedulePostSource.match(/!hasCaptionTagError/g) ?? []
    expect(occurrences).toHaveLength(2)
  })

  it('shows a distinct inline error for each exceeded cap', () => {
    expect(schedulePostSource).toMatch(
      /isTooManyHashtags && \(\s*<span className="char-error">\s*\{InstagramFeedTooManyHashtagsMessage\}/,
    )
    expect(schedulePostSource).toMatch(
      /isTooManyMentions && \(\s*<span className="char-error">\s*\{InstagramFeedTooManyMentionsMessage\}/,
    )
  })
})
