import { describe, expect, it } from 'vitest'
// Import the component source as a raw string (Vite `?raw`) so these guarantees run
// in the project's Node test environment without a DOM/interaction harness. They
// pin the stale-validation fix for Facebook/Instagram multi-media uploads: a new
// upload must hide the previous validation error and show the incoming media as
// pending, mirroring the single-media MediaUpload fix.
import multiMediaUploadSource from './MultiMediaUpload.tsx?raw'

describe('MultiMediaUpload — stale validation does not survive a new upload', () => {
  it('gates the validation-error panel on showValidationErrors, not raw invalid items', () => {
    // The bottom error panel and the status-bar warning must both render through
    // the upload-aware flag so they disappear the instant a new upload starts.
    expect(multiMediaUploadSource).toMatch(/\{showValidationErrors && \(\s*<div className="carousel-validation-errors">/)
    expect(multiMediaUploadSource).toMatch(/\{showValidationErrors && \(\s*<span className="carousel-warning">/)
    // The old, always-on condition must be gone from those render sites.
    expect(multiMediaUploadSource).not.toMatch(/\{hasInvalidItems && \(\s*<div className="carousel-validation-errors">/)
  })

  it('suppresses prior results while media is uploading or validating', () => {
    expect(multiMediaUploadSource).toMatch(/const isUploadingMedia = uploading \|\| pendingUploads\.length > 0/)
    expect(multiMediaUploadSource).toMatch(/const showValidationErrors = !isUploadingMedia && hasInvalidItems/)
  })

  it('renders optimistic pending cards for in-flight uploads', () => {
    expect(multiMediaUploadSource).toMatch(/setPendingUploads\(/)
    expect(multiMediaUploadSource).toMatch(/pendingUploads\.map\(pending =>/)
    expect(multiMediaUploadSource).toMatch(/carousel-validating-badge/)
    // Pending placeholders must clear once the upload settles.
    expect(multiMediaUploadSource).toMatch(/setPendingUploads\(\[\]\)/)
  })

  it('merges completed uploads against the latest items so a removal is not clobbered', () => {
    expect(multiMediaUploadSource).toMatch(/itemsRef\.current = items/)
    expect(multiMediaUploadSource).toMatch(/onItemsChange\(\[\.\.\.itemsRef\.current, \.\.\.newItems\]\)/)
    // The completion merge must not read the stale closure snapshot.
    expect(multiMediaUploadSource).not.toMatch(/onItemsChange\(\[\.\.\.items, \.\.\.newItems\]\)/)
  })
})
