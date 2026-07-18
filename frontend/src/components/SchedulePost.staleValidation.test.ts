import { describe, expect, it } from 'vitest'
// Source-level guarantees (no DOM test env in this project) for the stale
// media-validation UX fix at the SchedulePost level. The single-media uploader
// (Stories + generic single feed) reports a new upload session to the parent via an
// `onValidationChange('Pending', …)` call. SchedulePost must use that signal to wipe
// *both* halves of the media-error UI — the owner-keyed server validation state and
// the transient client-side upload-error banner — so the previous media's error can
// never linger under a new, still-pending upload.
import schedulePostSource from './SchedulePost.tsx?raw'

describe('SchedulePost — a new upload start clears the previous media error', () => {
  it('clears the transient upload-error banner when a new upload session begins (Pending)', () => {
    // The Pending branch of the validation handler must reset the upload-error
    // banner (which holds client-side pre-validation messages with no owner key of
    // their own, e.g. unsupported type, too-large file, or Feed aspect ratio).
    expect(schedulePostSource).toMatch(
      /const handleMediaValidationChange = \([\s\S]*?if \(status === 'Pending'\) \{\s*setUploadError\(null\)\s*\}/,
    )
  })

  it('still folds every validation update through the owner-keyed reducer', () => {
    // The stale-async protection (late results from a superseded upload are dropped)
    // must remain — the upload-error clear is additive, not a replacement.
    expect(schedulePostSource).toMatch(
      /setMediaValidation\(prev => applySchedulePostMediaValidationUpdate\(prev, status, errors, ownerKey\)\)/,
    )
  })

  it('delegates server validation detail to the uploader card (no duplicate summary)', () => {
    // The server validation error/warning detail is now rendered once, by the
    // uploader's shared MediaValidationCard. SchedulePost must not hand-roll its own
    // duplicate summary, and must not render off raw `mediaValidation.status`.
    expect(schedulePostSource).not.toMatch(/media-validation-summary/)
    expect(schedulePostSource).not.toMatch(/mediaValidation\.status === 'Invalid' &&\s*\(/)
    // It still derives the owner-aware blocking flag to gate the buttons.
    expect(schedulePostSource).toMatch(/const hasBlockingMediaValidation = hasBlockingSchedulePostMediaValidation\(mediaUrl, mediaValidation\.status\)/)
  })

  it('shows the pre-upload requirement via the one shared component', () => {
    expect(schedulePostSource).toMatch(/<MediaRequirementHint platform=\{selectedPlatformId\} placement=\{isStory \? 'Story' : 'Feed'\} \/>/)
    // The three bespoke per-platform hint blocks are gone.
    expect(schedulePostSource).not.toMatch(/ig-media-hint/)
  })

  it('wires both single-media uploaders (Story + generic feed) to the validation + error handlers', () => {
    // Story placement uploader and the generic single-media uploader must both report
    // validation changes and upload errors so the clear-on-start behavior is uniform.
    const validationWires = schedulePostSource.match(/onValidationChange=\{handleMediaValidationChange\}/g) ?? []
    expect(validationWires.length).toBeGreaterThanOrEqual(2)
    const errorWires = schedulePostSource.match(/onUploadError=\{\(error\) => setUploadError\(error\)\}/g) ?? []
    expect(errorWires.length).toBeGreaterThanOrEqual(2)
    // The Story uploader validates with Story placement.
    expect(schedulePostSource).toMatch(/placement="Story"/)
  })
})
