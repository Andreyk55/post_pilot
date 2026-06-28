import { describe, expect, it } from 'vitest'
// Import the component source as a raw string (Vite `?raw`) so these guarantees run
// in the project's Node test environment without a DOM/interaction harness. They pin
// the consolidation of the carousel/multi-media uploader onto the SAME shared
// validation UI as the single-media uploader: one badge, one normalized card.
import multiMediaUploadSource from './MultiMediaUpload.tsx?raw'

describe('MultiMediaUpload — shared validation UI', () => {
  it('renders validation through the shared card driven by the aggregated view', () => {
    expect(multiMediaUploadSource).toMatch(/import \{ MediaValidationBadge, MediaValidationCard \} from '\.\/MediaValidationStatus'/)
    expect(multiMediaUploadSource).toMatch(/aggregateMediaValidationViews\(/)
    // One shared card below the grid — no bespoke per-item error markup.
    expect(multiMediaUploadSource).toMatch(/\{showValidationCard && <MediaValidationCard view=\{aggregatedValidationView\} \/>\}/)
    expect(multiMediaUploadSource).not.toMatch(/carousel-validation-errors/)
    expect(multiMediaUploadSource).not.toMatch(/carousel-warning/)
    expect(multiMediaUploadSource).not.toMatch(/carousel-item-error/)
  })

  it('suppresses prior results while media is uploading or validating', () => {
    expect(multiMediaUploadSource).toMatch(/const isUploadingMedia = uploading \|\| pendingUploads\.length > 0/)
    expect(multiMediaUploadSource).toMatch(/const showValidationCard = !isUploadingMedia/)
  })

  it('renders optimistic pending cards using the shared validating badge', () => {
    expect(multiMediaUploadSource).toMatch(/setPendingUploads\(/)
    expect(multiMediaUploadSource).toMatch(/pendingUploads\.map\(pending =>/)
    // Pending cards render the shared MediaValidationBadge in its validating state,
    // matching the single-media (Story) "Validating…" visual.
    expect(multiMediaUploadSource).toMatch(/<MediaValidationBadge validating \/>/)
    // Pending placeholders must clear once the upload settles.
    expect(multiMediaUploadSource).toMatch(/setPendingUploads\(\[\]\)/)
  })

  it("shows each item's validation status with the shared badge", () => {
    // Same Validating/Valid/Invalid/Warning component the single-media uploader uses,
    // so carousel items get the clear valid/success state too — not just errors.
    expect(multiMediaUploadSource).toMatch(/<MediaValidationBadge\s+status=\{item\.validationStatus\}/)
  })

  it('uses the friendly client pre-validation copy shared with the single-media uploader', () => {
    expect(multiMediaUploadSource).toMatch(/resolveClientMediaError\(file, selectedPlatform, 'Feed'\)/)
    expect(multiMediaUploadSource).toMatch(/resolveClientDimensionError\(dims\.width, dims\.height, selectedPlatform, 'Feed'\)/)
    expect(multiMediaUploadSource).not.toMatch(/preValidateFile\(/)
  })

  it('preserves the real upload error message per file instead of discarding it', () => {
    expect(multiMediaUploadSource).toMatch(/setUploadError\(getUploadErrorMessage\(err, `Couldn't upload \$\{file\.name\}\. Please try again\.`\)\)/)
    expect(multiMediaUploadSource).not.toMatch(/setUploadError\(`Failed to upload \$\{file\.name\}`\)/)
  })

  it('merges completed uploads against the latest items so a removal is not clobbered', () => {
    expect(multiMediaUploadSource).toMatch(/itemsRef\.current = items/)
    expect(multiMediaUploadSource).toMatch(/onItemsChange\(\[\.\.\.itemsRef\.current, \.\.\.newItems\]\)/)
    // The completion merge must not read the stale closure snapshot.
    expect(multiMediaUploadSource).not.toMatch(/onItemsChange\(\[\.\.\.items, \.\.\.newItems\]\)/)
  })
})
