import { describe, expect, it } from 'vitest'
// Source-level guarantees that the composer's text limit is placement-specific and blocking.
// The limit VALUES and boundary behavior are tested in constants/validationLimits.test.ts
// (shared rule table, mirrored from the backend's ValidationLimits.cs); these pins prove the
// composer actually resolves its maximum from that table for the selected platform, renders
// the `current/max` counter, and gates both submit buttons on the blocking flag. The project
// does not configure a DOM harness, so these pin the source the same way as
// SchedulePost.storyContent.test.ts.
import schedulePostSource from './SchedulePost.tsx?raw'

describe('SchedulePost - placement-specific text limit wiring', () => {
  it('resolves the maximum from the shared rule table for the selected platform', () => {
    // Facebook selected → 5000, Instagram selected → 2200; recomputed on every render, so
    // switching the platform immediately re-resolves the limit for the SAME content string
    // (a 3000-char draft is valid for Facebook, blocking for Instagram, valid again after
    // switching back — nothing mutates the text).
    expect(schedulePostSource).toContain(
      'const maxChars = getPostTextMaxChars(selectedPlatformId ?? null)',
    )
    expect(schedulePostSource).toContain(
      'const isTextTooLong = content.length > maxChars',
    )
  })

  it('does not hardcode the 5000/2200 limits in the component', () => {
    // The numbers live only in constants/validationLimits.ts (mirroring the backend).
    expect(schedulePostSource).not.toMatch(/5000|5_000|2200|2_200/)
    expect(schedulePostSource).toMatch(/from '\.\.\/constants\/validationLimits'/)
  })

  it('renders the live counter as current/max (0/5000 for Facebook, 0/2200 for Instagram)', () => {
    expect(schedulePostSource).toMatch(/\{content\.length\}\/\{maxChars\}/)
  })

  it('shows the blocking platform-specific error when over the limit', () => {
    expect(schedulePostSource).toMatch(
      /\{isTextTooLong && \(\s*<span className="char-error">\s*Text is too long for \{platformDisplayName\}\. Max \{maxChars\} characters\./,
    )
  })

  it('never silently truncates: the textarea has no maxLength attribute', () => {
    // Over-limit text stays visible with the error shown; submission is blocked instead.
    const textareaStart = schedulePostSource.indexOf('<textarea')
    const textareaEnd = schedulePostSource.indexOf('/>', textareaStart)
    expect(textareaStart).toBeGreaterThan(-1)
    expect(schedulePostSource.slice(textareaStart, textareaEnd)).not.toContain('maxLength')
  })

  it('gates Schedule and Publish Now on the blocking flag (all four validity branches)', () => {
    // isFormValid and isPublishNowValid each require !isTextTooLong in both their story and
    // feed branches.
    const occurrences = schedulePostSource.match(/!isTextTooLong/g) ?? []
    expect(occurrences).toHaveLength(4)
    expect(schedulePostSource).toMatch(/className="submit-btn"\s*disabled=\{!isComposerEnabled \|\| !isFormValid\}/)
    expect(schedulePostSource).toMatch(/className="publish-now-btn"\s*disabled=\{!isComposerEnabled \|\| !isPublishNowValid \|\| isPublishingNow\}/)
  })

  it('routes AI-generated and translated text through the same content state and validation', () => {
    // AiAssistPanel writes via setContent only — over-limit AI output therefore trips the
    // same isTextTooLong blocking flag as typed text; no separate AI limit exists.
    expect(schedulePostSource).toMatch(/onApplyText=\{\(newText, newLanguageCode\) => \{\s*\/\/ Only update if content actually changes\s*if \(content !== newText\) \{\s*setContent\(newText\)/)
    expect(schedulePostSource).toMatch(/onAppendText=\{\(text\) => setContent\(\(prev\) => prev \+ text\)\}/)
  })
})
