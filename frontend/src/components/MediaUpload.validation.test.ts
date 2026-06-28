import { describe, expect, it } from 'vitest'
// Source-level guarantees (the project has no DOM test env) that the single-media
// uploader — used by Facebook/Instagram Stories and the generic single-media feed —
// renders its validation status through the shared MediaValidationStatus pieces, so
// every media surface stays visually consistent.
import mediaUploadSource from './MediaUpload.tsx?raw'

describe('MediaUpload — shared validation UI', () => {
  it('imports the shared validation badge + panel', () => {
    expect(mediaUploadSource).toMatch(
      /import \{ MediaValidationBadge, MediaValidationPanel \} from '\.\/MediaValidationStatus'/,
    )
  })

  it('renders the badge for the live validating/terminal state', () => {
    expect(mediaUploadSource).toMatch(/<MediaValidationBadge\s+validating=\{validating\}\s+status=\{validationStatus\}\s+showPending=\{!!selectedPlatform\}/)
  })

  it('renders errors and warnings through the shared panel', () => {
    expect(mediaUploadSource).toMatch(/<MediaValidationPanel errors=\{validationErrors\} warnings=\{validationWarnings\} \/>/)
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
