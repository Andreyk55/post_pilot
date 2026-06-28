import { describe, expect, it } from 'vitest'
import mediaUploadSource from './MediaUpload.tsx?raw'

describe('MediaUpload - validation preview behavior', () => {
  it('keeps Story/single-media uploads preview-first while validation is pending', () => {
    expect(mediaUploadSource).toMatch(/\{preview \? \(/)
    expect(mediaUploadSource).toMatch(/<img src=\{preview\} alt="Upload preview" \/>/)
    expect(mediaUploadSource).toMatch(/<video[\s\S]*src=\{preview\}[\s\S]*controls[\s\S]*muted[\s\S]*playsInline/)
    expect(mediaUploadSource).toMatch(/const showValidationOverlay = !!selectedPlatform && \(uploading \|\| validating\)/)
    expect(mediaUploadSource).toMatch(/<MediaValidationOverlay show=\{showValidationOverlay\} \/>/)
    expect(mediaUploadSource).toMatch(
      /<MediaValidationBadge[\s\S]*status=\{validationStatus\}[\s\S]*showPending=\{!!selectedPlatform && !showValidationOverlay\}/,
    )
  })
})
