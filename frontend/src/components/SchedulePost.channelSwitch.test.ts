import { describe, expect, it } from 'vitest'
// Import the component source as a raw string (Vite `?raw`) so these guarantees run
// in the project's Node test environment without a DOM/interaction harness — the
// same approach used by SchedulePost.workspace.test.ts and
// MultiMediaUpload.validation.test.ts. They pin the channel-switch behavior:
// switching the Meta channel (Facebook <-> Instagram) clears the composer draft and
// drops any in-flight upload/validation from the previous channel, while leaving the
// connected accounts, available pages/IG assets, and workspace untouched.
import schedulePostSource from './SchedulePost.tsx?raw'

/** Slice out the body of the `resetComposerDraft` helper so "what the reset clears"
 *  and "what it must NOT clear" are asserted against the reset itself, not the whole
 *  file (which legitimately sets connected pages/accounts elsewhere on load). */
function resetComposerDraftBody(): string {
  const start = schedulePostSource.indexOf('const resetComposerDraft = ({')
  expect(start).toBeGreaterThan(-1)
  const end = schedulePostSource.indexOf('const applyChannelSwitch = (platformId: string) => {', start)
  expect(end).toBeGreaterThan(start)
  return schedulePostSource.slice(start, end)
}

describe('SchedulePost — Meta channel switch clears the draft', () => {
  it('routes the platform buttons through the channel-switch-aware handler', () => {
    expect(schedulePostSource).toMatch(/onClick=\{\(\) => handlePlatformClick\(platform\.id\)\}/)
    // The handler decides a switch via the pure helper and only resets on a real
    // cross-channel switch — first selection / deselect fall back to selectPlatform.
    expect(schedulePostSource).toMatch(/isMetaChannelSwitch\(selectedPlatforms, platformId\)/)
    expect(schedulePostSource).toMatch(/return\s*\n\s*\}\s*\n\s*selectPlatform\(platformId\)/)
  })

  it('applies a switch by clearing the draft then selecting the new channel', () => {
    expect(schedulePostSource).toMatch(
      /const applyChannelSwitch = \(platformId: string\) => \{\s*resetComposerDraft\(\)\s*setSelectedPlatforms\(\[platformId\]\)/,
    )
  })

  // Scenario 2: text entered on one channel is cleared on switch.
  it('clears the caption/content on reset', () => {
    expect(resetComposerDraftBody()).toMatch(/setContent\(''\)/)
  })

  // Scenario 1: media selected on one channel is cleared on switch (single + carousel).
  it('clears single and carousel media on reset', () => {
    const body = resetComposerDraftBody()
    expect(body).toMatch(/setMediaUrl\(null\)/)
    expect(body).toMatch(/setMediaType\(null\)/)
    expect(body).toMatch(/setCarouselItems\(\[\]\)/)
    expect(body).toMatch(/setSelectedThumbnailUrl\(null\)/)
    expect(body).toMatch(/setMediaTags\(\[\]\)/)
    expect(body).toMatch(/setCarouselMediaTags\(new Map\(\)\)/)
  })

  it('clears media validation, schedule, post type, targets, and AI results on reset', () => {
    const body = resetComposerDraftBody()
    expect(body).toMatch(/clearSingleMediaValidationState\(\)/)
    expect(body).toMatch(/setScheduledDate\(''\)/)
    expect(body).toMatch(/setScheduledTime\(''\)/)
    expect(body).toMatch(/nextPostType = 'Feed'/)
    expect(body).toMatch(/setPostType\(nextPostType\)/)
    expect(body).toMatch(/setSelectedPageId\(''\)/)
    expect(body).toMatch(/setSelectedInstagramAccountId\(''\)/)
    expect(body).toMatch(/setUploadError\(null\)/)
    expect(body).toMatch(/setIsUploading\(false\)/)
    // AI Assist generated results + suggested times are remounted (fresh, empty).
    expect(body).toMatch(/setAiPanelKey\(k => k \+ 1\)/)
    expect(body).toMatch(/setSuggestedTimesKey\(k => k \+ 1\)/)
  })

  // Scenarios 3 & 4: remounting the upload components invalidates their in-flight
  // ownership tokens (see MediaUpload / MultiMediaUpload activeUploadOwnerKeyRef
  // cleanup), so a late validation error or upload completion from the previous
  // channel is dropped instead of re-populating the cleared draft.
  it('remounts the upload components on reset so stale upload/validation results are ignored', () => {
    expect(resetComposerDraftBody()).toMatch(/setUploadKey\(k => k \+ 1\)/)
    // The upload key is the remount token on both single- and multi-media uploaders.
    expect(schedulePostSource).toMatch(/<MediaUpload\s*\n\s*key=\{uploadKey\}/)
    expect(schedulePostSource).toMatch(/<MultiMediaUpload\s*\n\s*key=\{uploadKey\}/)
  })

  // Scenario 5: the reset is draft-only — connected accounts, available pages/IG
  // assets, and the workspace must survive a channel switch.
  it('does not clear connected accounts, available pages/assets, or workspace on reset', () => {
    const body = resetComposerDraftBody()
    expect(body).not.toMatch(/setConnectedPages\(/)
    expect(body).not.toMatch(/setConnectedInstagramAccounts\(/)
    expect(body).not.toMatch(/setIsAccountConnected\(/)
    // The reset also never touches the selected platforms (the caller sets those).
    expect(body).not.toMatch(/setSelectedPlatforms\(/)
  })
})

describe('SchedulePost — channel switch confirmation UX', () => {
  it('prompts before discarding a dirty draft and clears it only on confirm', () => {
    // A dirty draft defers the switch behind the confirm dialog instead of clearing.
    expect(schedulePostSource).toMatch(/if \(isDirty\) \{\s*setPendingChannelSwitch\(platformId\)/)
    expect(schedulePostSource).toMatch(/isComposerDraftDirty\(getComposerDraftSnapshot\(\)\)/)
    // The dialog confirms into the switch, and cancel leaves channel + draft intact.
    expect(schedulePostSource).toMatch(/Switching channels will clear your current draft\. Continue\?/)
    expect(schedulePostSource).toMatch(/onConfirm=\{\(\) => \{[\s\S]*?applyChannelSwitch\(pendingChannelSwitch\)/)
    expect(schedulePostSource).toMatch(/onCancel=\{\(\) => setPendingChannelSwitch\(null\)\}/)
  })

  it('does not switch immediately when the draft is dirty (no direct applyChannelSwitch before confirm)', () => {
    // In the dirty branch we must return after opening the dialog, never fall through
    // to applyChannelSwitch.
    expect(schedulePostSource).toMatch(/setPendingChannelSwitch\(platformId\)\s*\n\s*return/)
  })
})
