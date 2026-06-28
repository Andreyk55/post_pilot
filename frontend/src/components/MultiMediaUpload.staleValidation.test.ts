import { describe, expect, it } from 'vitest'
// Source-level guarantees (no DOM test env in this project) for the carousel /
// multi-media side of the stale media-validation fix. Multi-media stores validation
// per item, so a new upload must (a) suppress the prior validation card while the new
// media is in flight, and (b) when an item is removed/replaced, drop only that item's
// state — never resurrecting a stale one under a freshly added pending item.
import multiMediaUploadSource from './MultiMediaUpload.tsx?raw'

describe('MultiMediaUpload — stale per-item validation never survives a new upload', () => {
  it('suppresses the prior validation card the instant a new upload/validation is in flight', () => {
    // isUploadingMedia is true while a file is uploading OR a pending placeholder
    // card exists, and it gates the shared validation card.
    expect(multiMediaUploadSource).toMatch(/const isUploadingMedia = uploading \|\| pendingUploads\.length > 0/)
    expect(multiMediaUploadSource).toMatch(/const showValidationCard = !isUploadingMedia/)
    expect(multiMediaUploadSource).toMatch(/\{showValidationCard && <MediaValidationCard view=\{aggregatedValidationView\} \/>\}/)
  })

  it('begins a fresh upload-ownership session and shows the new media as pending right away', () => {
    expect(multiMediaUploadSource).toMatch(/const uploadFiles = async \(filesToUpload: File\[\]\) => \{\s*const uploadOwnerKey = beginUploadSession\(\)/)
    // Optimistic pending cards render immediately so the incoming media is visible
    // while the previous card is hidden.
    expect(multiMediaUploadSource).toMatch(/replacePendingUploads\(\s*filesToUpload\.map/)
  })

  it('keeps validation state per item so removing/replacing one drops only that state', () => {
    // Each committed item carries its own status + errors + warnings; the aggregated
    // view maps over the items so a removed item's state leaves with it.
    expect(multiMediaUploadSource).toMatch(/aggregateMediaValidationViews\(\s*items\.map\(\(item, index\) =>/)
    expect(multiMediaUploadSource).toMatch(/status: item\.validationStatus/)
    // Removing an item filters it (and therefore its state) out of the list and
    // clears the transient upload-error banner.
    expect(multiMediaUploadSource).toMatch(/const handleRemove = \(id: string\) => \{\s*onItemsChange\(items\.filter\(item => item\.id !== id\)\)\s*setUploadError\(null\)/)
  })

  it('drops a superseded upload result so a late invalid never reappears (stale-async guard intact)', () => {
    expect(multiMediaUploadSource).toMatch(/const isStaleUploadOwner = \(uploadOwnerKey: string\) => activeUploadOwnerKeyRef\.current !== uploadOwnerKey/)
    expect(multiMediaUploadSource).toMatch(/if \(isStaleUploadOwner\(uploadOwnerKey\)\) return/)
  })
})
