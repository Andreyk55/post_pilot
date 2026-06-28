import { describe, expect, it } from 'vitest'
// Source-level guarantees for the in-platform post type switch UX. The project
// does not configure a DOM harness, so these assertions pin the handler/reset flow
// that prevents Feed media/validation state from leaking into Story, and vice versa.
import schedulePostSource from './SchedulePost.tsx?raw'

function resetComposerDraftBody(): string {
  const start = schedulePostSource.indexOf('const resetComposerDraft = ({')
  expect(start).toBeGreaterThan(-1)
  const end = schedulePostSource.indexOf('const applyChannelSwitch = (platformId: string) => {', start)
  expect(end).toBeGreaterThan(start)
  return schedulePostSource.slice(start, end)
}

describe('SchedulePost - post type switch confirmation UX', () => {
  it('routes post type buttons through the guarded post-type handler', () => {
    expect(schedulePostSource).toMatch(/onClick=\{\(\) => handlePostTypeChange\('Feed'\)\}/)
    expect(schedulePostSource).toMatch(/onClick=\{\(\) => handlePostTypeChange\('Story'\)\}/)
    expect(schedulePostSource).not.toMatch(/onClick=\{\(\) => \{\s*setPostType\('Feed'\)/)
    expect(schedulePostSource).not.toMatch(/onClick=\{\(\) => \{\s*setPostType\('Story'\)/)
  })

  it('does nothing when re-selecting the current post type', () => {
    expect(schedulePostSource).toMatch(/if \(!isPostTypeSwitch\(postType, nextPostType\)\) return/)
  })

  it('prompts only when actual draft data would be lost', () => {
    expect(schedulePostSource).toMatch(
      /isComposerDraftDirty\(getComposerDraftSnapshot\(\), \{ includePostType: false \}\)/,
    )
    expect(schedulePostSource).toMatch(/if \(isDirty\) \{\s*setPendingPostTypeSwitch\(nextPostType\)\s*return/)
    expect(schedulePostSource).toMatch(/setPostType\(nextPostType\)/)
  })

  it('confirms into the captured next post type and cancel leaves the draft untouched', () => {
    expect(schedulePostSource).toMatch(/Changing the post type will clear your current draft details, uploaded media, and validation results\. Continue\?/)
    expect(schedulePostSource).toMatch(/onConfirm=\{\(\) => \{[\s\S]*?applyPostTypeSwitch\(pendingPostTypeSwitch\)/)
    expect(schedulePostSource).toMatch(/onCancel=\{\(\) => setPendingPostTypeSwitch\(null\)\}/)
  })

  it('resets through the shared helper while applying the requested post type', () => {
    expect(schedulePostSource).toMatch(
      /const applyPostTypeSwitch = \(nextPostType: PostType\) => \{\s*resetComposerDraft\(\{ nextPostType, clearTargets: false \}\)/,
    )

    const body = resetComposerDraftBody()
    expect(body).toMatch(/nextPostType = 'Feed'/)
    expect(body).toMatch(/setPostType\(nextPostType\)/)
    expect(body).toMatch(/setContent\(''\)/)
    expect(body).toMatch(/setScheduledDate\(''\)/)
    expect(body).toMatch(/setScheduledTime\(''\)/)
    expect(body).toMatch(/setMediaUrl\(null\)/)
    expect(body).toMatch(/setMediaType\(null\)/)
    expect(body).toMatch(/setUploadError\(null\)/)
    expect(body).toMatch(/clearSingleMediaValidationState\(\)/)
    expect(body).toMatch(/setCarouselItems\(\[\]\)/)
    expect(body).toMatch(/setMediaTags\(\[\]\)/)
    expect(body).toMatch(/setCarouselMediaTags\(new Map\(\)\)/)
    expect(body).toMatch(/setUploadKey\(k => k \+ 1\)/)
    expect(body).toMatch(/setAiPanelKey\(k => k \+ 1\)/)
    expect(body).toMatch(/setSuggestedTimesKey\(k => k \+ 1\)/)
  })

  it('preserves selected Facebook Page and Instagram asset for post-type switches', () => {
    expect(schedulePostSource).toMatch(/resetComposerDraft\(\{ nextPostType, clearTargets: false \}\)/)
    const body = resetComposerDraftBody()
    expect(body).toMatch(/if \(clearTargets\) \{[\s\S]*?setSelectedPageId\(''\)[\s\S]*?setSelectedInstagramAccountId\(''\)[\s\S]*?\}/)
  })

  it('invalidates stale upload and validation ownership state on confirmed switch', () => {
    const body = resetComposerDraftBody()
    expect(body).toMatch(/setUploadKey\(k => k \+ 1\)/)
    expect(body).toMatch(/setIsUploading\(false\)/)
    expect(body).toMatch(/clearSingleMediaValidationState\(\)/)
  })

  it('counts upload and validation errors in the dirty snapshot', () => {
    expect(schedulePostSource).toMatch(/hasUploadError: uploadError !== null/)
    expect(schedulePostSource).toMatch(/hasSingleMediaValidationState: mediaValidation\.status !== null \|\| mediaValidation\.errors\.length > 0/)
    expect(schedulePostSource).toMatch(/carouselValidationIssueCount/)
  })
})
