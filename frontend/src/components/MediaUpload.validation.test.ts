import { describe, expect, it } from 'vitest'
// Source-level guarantees (the project has no DOM test env) that the single-media
// uploader — used by Facebook/Instagram Stories and the generic single-media feed —
// renders its validation status through the shared MediaValidationStatus pieces, so
// every media surface stays visually consistent.
import mediaUploadSource from './MediaUpload.tsx?raw'

describe('MediaUpload — shared validation UI', () => {
  it('imports the shared validation badge + card', () => {
    expect(mediaUploadSource).toMatch(
      /import \{ MediaValidationBadge, MediaValidationCard \} from '\.\/MediaValidationStatus'/,
    )
  })

  it('renders the badge for the live validating/terminal state', () => {
    expect(mediaUploadSource).toMatch(/<MediaValidationBadge\s+validating=\{validating\}\s+status=\{validationStatus\}\s+showPending=\{!!selectedPlatform\}/)
  })

  it('renders errors and warnings through the shared card driven by the normalized view', () => {
    expect(mediaUploadSource).toMatch(
      /<MediaValidationCard\s+view=\{resolveMediaValidationView\(validationStatus, validationErrors, validationWarnings, \{ validating \}\)\}/,
    )
  })

  it('uses the friendly, specific client pre-validation copy (not the technical strings)', () => {
    expect(mediaUploadSource).toMatch(/resolveClientMediaError\(file, selectedPlatform, placement\)/)
    expect(mediaUploadSource).toMatch(/resolveClientDimensionError\(dims\.width, dims\.height, selectedPlatform, placement\)/)
    // The old technical pre-validation helpers are no longer used here.
    expect(mediaUploadSource).not.toMatch(/preValidateFile\(/)
    expect(mediaUploadSource).not.toMatch(/preValidateImageDimensions\(/)
  })

  it('surfaces the real upload/API error message instead of a generic string', () => {
    expect(mediaUploadSource).toMatch(/onUploadError\(getUploadErrorMessage\(err\)\)/)
    expect(mediaUploadSource).not.toMatch(/Failed to upload file/)
  })

  it('no longer hand-rolls the inline validation badge or panel markup', () => {
    expect(mediaUploadSource).not.toMatch(/className="validation-badge/)
    expect(mediaUploadSource).not.toMatch(/className="validation-errors"/)
    expect(mediaUploadSource).not.toMatch(/className="validation-warnings"/)
    expect(mediaUploadSource).not.toMatch(/const getValidationBadge/)
  })

  it('keeps the stale-result guard so a superseded validation never re-renders', () => {
    // Purely a UI refactor — the owner-key gating that drops late results stays.
    expect(mediaUploadSource).toMatch(/if \(isStaleUploadOwner\(uploadOwnerKey\)\) return/)
    expect(mediaUploadSource).toMatch(/if \(!isStaleUploadOwner\(uploadOwnerKey\)\) \{[\s\S]*setValidationStatus/)
  })
})
