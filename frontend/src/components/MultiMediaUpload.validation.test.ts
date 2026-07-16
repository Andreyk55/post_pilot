import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
// Import the component source as a raw string (Vite `?raw`) so these guarantees run
// in the project's Node test environment without a DOM/interaction harness. They pin
// the consolidation of the carousel/multi-media uploader onto the SAME shared
// validation UI as the single-media uploader: one badge, one normalized card.
import multiMediaUploadSource from './MultiMediaUpload.tsx?raw'
const multiMediaUploadCss = readFileSync(new URL('./MultiMediaUpload.css', import.meta.url), 'utf8')

describe('MultiMediaUpload — shared validation UI', () => {
  it('renders validation through the shared card driven by the aggregated view', () => {
    expect(multiMediaUploadSource).toMatch(/import \{ MediaValidationBadge, MediaValidationCard, MediaValidationOverlay \} from '\.\/MediaValidationStatus'/)
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

  it('renders optimistic pending cards using the shared validating overlay', () => {
    expect(multiMediaUploadSource).toMatch(/setPendingUploads\(/)
    expect(multiMediaUploadSource).toMatch(/pendingUploads\.map\(pending =>/)
    // Pending cards render the shared full-thumbnail overlay, matching the
    // single-media (Story) "Validating..." visual.
    expect(multiMediaUploadSource).toMatch(/<MediaValidationOverlay show \/>/)
    // Pending previews must clear once the upload settles.
    expect(multiMediaUploadSource).toMatch(/replacePendingUploads\(\[\]\)/)
  })

  it('renders pending Feed image uploads as real previews with the validating overlay', () => {
    expect(multiMediaUploadSource).toMatch(/previewUrl: URL\.createObjectURL\(file\)/)
    expect(multiMediaUploadSource).toMatch(/mediaType: getPendingUploadMediaType\(file\)/)
    expect(multiMediaUploadSource).toMatch(
      /pending\.mediaType === 'image'[\s\S]*<img src=\{pending\.previewUrl\} alt=\{pending\.fileName\} className="carousel-thumbnail" \/>/,
    )
    expect(multiMediaUploadSource).toMatch(/<MediaValidationOverlay show \/>/)
    expect(multiMediaUploadSource).not.toMatch(
      /<div className="carousel-thumbnail carousel-thumbnail--pending">\s*<MediaValidationBadge validating/,
    )
  })

  it('renders pending Feed video uploads as compact video previews with the play overlay', () => {
    expect(multiMediaUploadSource).toMatch(
      /pending\.mediaType === 'video'[\s\S]*<video[\s\S]*src=\{pending\.previewUrl\}[\s\S]*className="carousel-thumbnail"[\s\S]*preload="metadata"[\s\S]*<span className="carousel-video-indicator" aria-hidden="true">/,
    )
    expect(multiMediaUploadSource).toMatch(
      /<span className="carousel-video-indicator" aria-hidden="true">[\s\S]*<MediaValidationOverlay show \/>/,
    )
    expect(multiMediaUploadCss).toMatch(/\.carousel-video-indicator \{[\s\S]*z-index: 1;/)
    expect(multiMediaUploadCss).toMatch(/\.carousel-item-filename \{[\s\S]*z-index: 2;/)
  })

  it('uses the same pending preview path for Facebook Feed and Instagram Feed', () => {
    expect(multiMediaUploadSource).toMatch(/if \(isInstagram\) \{[\s\S]*await uploadFiles\(filesToUpload\)[\s\S]*return\s*\}/)
    expect(multiMediaUploadSource).toMatch(/if \(isFacebook\) \{[\s\S]*await uploadFiles\(filesToUpload\)[\s\S]*return\s*\}/)
    expect(multiMediaUploadSource).toMatch(/platform: isFacebook \? 'Facebook' : 'Instagram'/)
  })

  it("shows each item's validation status with the shared badge", () => {
    // Same Validating/Valid/Invalid/Warning component the single-media uploader uses,
    // so carousel items get the clear valid/success state too — not just errors.
    expect(multiMediaUploadSource).toMatch(/<MediaValidationBadge\s+status=\{item\.validationStatus\}/)
    expect(multiMediaUploadCss).toMatch(/\.carousel-item-badge \{[\s\S]*top: 4px;[\s\S]*right: 4px;[\s\S]*z-index: 3;/)
  })

  it('keeps the video play icon visible after validation completes', () => {
    expect(multiMediaUploadSource).toMatch(
      /isItemVideo\(item\)[\s\S]*<video src=\{item\.previewUrl\} className="carousel-thumbnail" muted \/>[\s\S]*<span className="carousel-video-indicator">/,
    )
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

  it('surfaces Facebook selection rejections (11th image, 2nd video, mixed media) through the existing error UI', () => {
    // Rejections from validateFacebookSelection (count/video/mix rules, covered in
    // facebookMediaValidation.test.ts) must feed the same uploadError banner every
    // other upload failure uses — no bespoke error surface, no silent drop.
    expect(multiMediaUploadSource).toMatch(/const result = validateFacebookSelection\(existingAsInfo, newAsInfo\)/)
    expect(multiMediaUploadSource).toMatch(/if \(!result\.ok\) \{\s*setUploadError\(result\.errorMessage\)\s*return\s*\}/)
    expect(multiMediaUploadSource).toMatch(/\{uploadError && <div className="carousel-upload-error">\{uploadError\}<\/div>\}/)
  })
})
