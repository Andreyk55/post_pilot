import { describe, expect, it } from 'vitest'
// Source-level guarantees (no DOM test env in this project) that the single-media
// uploader (Facebook/Instagram Stories + the generic single feed) drops the previous
// media's validation the instant a new upload starts — synchronously, before it has
// uploaded or re-validated — and notifies the parent with a Pending update carrying
// the new owner key. This is the producer side of the SchedulePost stale-error fix.
import mediaUploadSource from './MediaUpload.tsx?raw'

describe('MediaUpload — a new upload start clears the previous validation immediately', () => {
  it('resets validation to a neutral Pending state and notifies the parent with the new owner key', () => {
    // setNeutralValidationState clears local errors/warnings and reports Pending
    // upstream so the parent (SchedulePost) wipes its mirror of the error too.
    expect(mediaUploadSource).toMatch(
      /const setNeutralValidationState = \(ownerKey: string\) => \{\s*setValidationStatus\('Pending'\)\s*setValidationErrors\(\[\]\)\s*setValidationWarnings\(\[\]\)\s*onValidationChange\?\.\('Pending', \[\], \[\], ownerKey\)\s*\}/,
    )
  })

  it('clears the previous result before awaiting the new file validation, not after', () => {
    // The neutral reset must run synchronously at the top of handleFileSelect —
    // after starting a fresh upload session but before the first `await`. This is
    // what makes the old error disappear the moment a new upload begins.
    expect(mediaUploadSource).toMatch(
      /const uploadOwnerKey = beginUploadSession\(\)\s*setProgress\(0\)\s*setNeutralValidationState\(uploadOwnerKey\)[\s\S]*?const error = await validateFile\(file\)/,
    )
  })

  it('starts a fresh upload-ownership session on every new file selection', () => {
    // Each selection adopts a new owner key, which both tags the new validation and
    // invalidates any in-flight result from the previous file.
    expect(mediaUploadSource).toMatch(/const beginUploadSession = \(\): string => \{[\s\S]*?activeUploadOwnerKeyRef\.current = uploadOwnerKey/)
    expect(mediaUploadSource).toMatch(/const isStaleUploadOwner = \(uploadOwnerKey: string\) => activeUploadOwnerKeyRef\.current !== uploadOwnerKey/)
  })

  it('re-validation on platform change also resets to neutral before the request', () => {
    // Switching platform with media already present must not flash the previous
    // platform's error while the new validation is pending.
    expect(mediaUploadSource).toMatch(
      /const revalidateMedia = async \(\) => \{[\s\S]*?const uploadOwnerKey = beginUploadSession\(\)[\s\S]*?setNeutralValidationState\(uploadOwnerKey\)/,
    )
  })

  it('still drops a superseded validation result (stale-async guard intact)', () => {
    expect(mediaUploadSource).toMatch(/A newer upload superseded this one/)
    expect(mediaUploadSource).toMatch(/if \(!isStaleUploadOwner\(uploadOwnerKey\)\) \{[\s\S]*?setValidationStatus/)
  })
})
