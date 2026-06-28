import { describe, expect, it } from 'vitest'
// Source-level guarantees (no DOM test env in this project) for the carousel /
// multi-media side of the stale media-validation fix. Multi-media stores validation
// per item, so a new upload must (a) suppress every prior item error while the new
// media is in flight, and (b) when an item is removed/replaced, drop only that item's
// error — never resurrecting a stale one under a freshly added pending item.
import multiMediaUploadSource from './MultiMediaUpload.tsx?raw'

describe('MultiMediaUpload — stale per-item validation never survives a new upload', () => {
  it('suppresses all prior item errors the instant a new upload/validation is in flight', () => {
    // isUploadingMedia is true while a file is uploading OR a pending placeholder
    // card exists, and it gates both the status-bar warning and the detail panel.
    expect(multiMediaUploadSource).toMatch(/const isUploadingMedia = uploading \|\| pendingUploads\.length > 0/)
    expect(multiMediaUploadSource).toMatch(/const showValidationErrors = !isUploadingMedia && hasInvalidItems/)
  })

  it('begins a fresh upload-ownership session and shows the new media as pending right away', () => {
    expect(multiMediaUploadSource).toMatch(/const uploadFiles = async \(filesToUpload: File\[\]\) => \{\s*const uploadOwnerKey = beginUploadSession\(\)/)
    // Optimistic pending cards render immediately so the incoming media is visible
    // while the previous error panel is hidden.
    expect(multiMediaUploadSource).toMatch(/setPendingUploads\(\s*filesToUpload\.map/)
  })

  it('keeps validation state per item so removing/replacing one drops only that error', () => {
    // Each committed item carries its own validation status + errors; the detail
    // panel maps over the *invalid* items only.
    expect(multiMediaUploadSource).toMatch(/const invalidItems = items\.filter\(item => item\.validationStatus === 'Invalid'\)/)
    expect(multiMediaUploadSource).toMatch(/invalidItems\.map\(item =>/)
    // Removing an item filters it (and therefore its error) out of the list and
    // clears the transient upload-error banner.
    expect(multiMediaUploadSource).toMatch(/const handleRemove = \(id: string\) => \{\s*onItemsChange\(items\.filter\(item => item\.id !== id\)\)\s*setUploadError\(null\)/)
  })

  it('renders the error panel and status-bar warning only through the upload-aware gate', () => {
    expect(multiMediaUploadSource).toMatch(/\{showValidationErrors && \(\s*<div className="carousel-validation-errors">/)
    expect(multiMediaUploadSource).toMatch(/\{showValidationErrors && \(\s*<span className="carousel-warning">/)
    // The old always-on render condition must be gone.
    expect(multiMediaUploadSource).not.toMatch(/\{hasInvalidItems && \(\s*<div className="carousel-validation-errors">/)
  })

  it('drops a superseded upload result so a late invalid never reappears (stale-async guard intact)', () => {
    expect(multiMediaUploadSource).toMatch(/const isStaleUploadOwner = \(uploadOwnerKey: string\) => activeUploadOwnerKeyRef\.current !== uploadOwnerKey/)
    expect(multiMediaUploadSource).toMatch(/if \(isStaleUploadOwner\(uploadOwnerKey\)\) return/)
  })
})
