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
    // their own, e.g. an invalid Story aspect ratio).
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

  it('renders the server validation summary only through the strict, owner-aware gate', () => {
    // Never render off raw `mediaValidation.status === 'Invalid'`; the gate
    // (shouldRenderSchedulePostMediaValidationError) only passes a current-session
    // Invalid result, so a stale error can never sit under a new pending upload.
    expect(schedulePostSource).toMatch(/const showMediaValidationError = shouldRenderSchedulePostMediaValidationError\(mediaValidation, mediaUrl\)/)
    expect(schedulePostSource).toMatch(/\{showMediaValidationError && \(\s*<div className="media-validation-summary">/)
    expect(schedulePostSource).not.toMatch(/mediaValidation\.status === 'Invalid' &&\s*\(/)
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
