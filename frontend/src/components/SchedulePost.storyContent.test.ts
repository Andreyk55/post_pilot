import { describe, expect, it } from 'vitest'
// Source-level guarantees that Story posts can never carry post text/caption content.
// The backend is authoritative (PostContentRules rejects non-empty Story content), but the
// composer must also never *send* it: the Story UI has no text field, and a Feed draft's
// text must not leak into a Story payload after a post-type switch. The project does not
// configure a DOM harness, so these pin the source the same way as
// SchedulePost.postTypeSwitch.test.ts.
import schedulePostSource from './SchedulePost.tsx?raw'
import schedulePostsPageSource from '../pages/SchedulePostsPage.tsx?raw'

/**
 * The caption/editor block: everything between the "hidden entirely for stories" comment
 * and the media form-group that follows it. Every text-editing affordance (textarea,
 * character counter, AI assist, IG mention helper) must live inside this block, and the
 * block itself must be gated on `!isStory`.
 */
function captionEditorBlock(): string {
  const start = schedulePostSource.indexOf('{/* Caption / Post Content — hidden entirely for stories */}')
  expect(start).toBeGreaterThan(-1)
  const end = schedulePostSource.indexOf("{isStory ? 'Media (required)'", start)
  expect(end).toBeGreaterThan(start)
  return schedulePostSource.slice(start, end)
}

describe('SchedulePost - Story posts have no text field', () => {
  it('gates the entire caption/editor block on !isStory (Facebook and Instagram alike)', () => {
    const block = captionEditorBlock()
    // The block opens with the !isStory guard — Story mode renders none of it. The guard is
    // placement-based (isStory), not platform-based, so FB Story and IG Story behave the same.
    expect(block).toMatch(/\{\/\* Caption \/ Post Content — hidden entirely for stories \*\/\}\s*\{!isStory && \(/)
    expect(schedulePostSource).toMatch(/const isStory = postType === 'Story'/)
  })

  it('renders the only textarea inside the !isStory block — no Story textarea exists', () => {
    const occurrences = schedulePostSource.match(/<textarea/g) ?? []
    expect(occurrences).toHaveLength(1)
    expect(captionEditorBlock()).toContain('<textarea')
  })

  it('renders the only character counter inside the !isStory block — no Story counter exists', () => {
    const occurrences = schedulePostSource.match(/char-count\b/g) ?? []
    expect(occurrences).toHaveLength(1)
    expect(captionEditorBlock()).toMatch(/char-count/)
  })

  it('keeps AI Content Assist unavailable for Stories (rendered only inside !isStory)', () => {
    const occurrences = schedulePostSource.match(/<AiAssistPanel/g) ?? []
    expect(occurrences).toHaveLength(1)
    expect(captionEditorBlock()).toContain('<AiAssistPanel')
  })
})

describe('SchedulePost - Story payloads never carry text', () => {
  it('clears content in the schedule payload when the post type is Story', () => {
    expect(schedulePostSource).toMatch(/onSchedule\(\{\s*content: isStory \? '' : content,/)
  })

  it('clears content in the publish-now payload when the post type is Story', () => {
    expect(schedulePostSource).toMatch(/await onPublishNow\(\{\s*content: isStory \? '' : content,/)
  })

  it('passes the composer content through verbatim at the page level (no re-injection)', () => {
    // Both request builders forward formData.content unchanged — the composer's Story
    // clearing above is therefore what reaches the API. No other content source exists.
    const occurrences = schedulePostsPageSource.match(/content: formData\.content,/g) ?? []
    expect(occurrences).toHaveLength(2)
    // No request builder assigns content from anything but formData.content (the remaining
    // `content:` occurrences in the page are the two handler type annotations).
    const assignments = schedulePostsPageSource.match(/content: (?!string\b)[^,\n]+,/g) ?? []
    expect(assignments).toEqual(['content: formData.content,', 'content: formData.content,'])
  })
})

describe('SchedulePost - Feed draft text cannot leak into a Story', () => {
  it('clears the draft text when a post-type switch is applied', () => {
    // Feed → Story goes through applyPostTypeSwitch → resetComposerDraft, which resets
    // content to '' (after the user confirms losing the draft — see
    // SchedulePost.postTypeSwitch.test.ts for the confirmation UX pins).
    expect(schedulePostSource).toMatch(
      /const applyPostTypeSwitch = \(nextPostType: PostType\) => \{\s*resetComposerDraft\(\{ nextPostType, clearTargets: false \}\)/,
    )
    const start = schedulePostSource.indexOf('const resetComposerDraft = ({')
    const end = schedulePostSource.indexOf('const applyChannelSwitch', start)
    expect(schedulePostSource.slice(start, end)).toMatch(/setContent\(''\)/)
  })

  it('only switches post type without a reset when the draft holds no text or other work', () => {
    // The non-reset path (plain setPostType) is reachable only when isComposerDraftDirty is
    // false — and any non-empty content marks the draft dirty, forcing the confirm+reset path.
    expect(schedulePostSource).toMatch(
      /const isDirty = isComposerDraftDirty\(getComposerDraftSnapshot\(\), \{ includePostType: false \}\)\s*if \(isDirty\) \{\s*setPendingPostTypeSwitch\(nextPostType\)\s*return\s*\}\s*setPostType\(nextPostType\)/,
    )
  })

  it('does not keep a hidden Feed-text backup to restore after switching back (current UX: draft is cleared on confirm)', () => {
    // Switching back to Feed starts from an empty draft; there is no snapshot/restore of the
    // previous Feed text anywhere in the composer.
    expect(schedulePostSource).not.toMatch(/backup|savedContent|previousContent|restoreContent/i)
  })
})

describe('SchedulePost - Feed text editing is unchanged', () => {
  it('keeps the Feed textarea wired to content state with the platform character counter', () => {
    const block = captionEditorBlock()
    expect(block).toMatch(/value=\{content\}/)
    expect(block).toMatch(/onChange=\{\(e\) => setContent\(e\.target\.value\)\}/)
    expect(block).toMatch(/\{content\.length\}\/\{maxChars\}/)
  })

  it('keeps Feed submissions sending the typed content', () => {
    // The same payload expression that clears Story content sends the draft verbatim for Feed.
    expect(schedulePostSource).toMatch(/content: isStory \? '' : content,/)
  })
})
