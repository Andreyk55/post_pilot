import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
// Source-level guarantees (the project has no DOM test env) that the single-media
// uploader wires up real drag-and-drop through the shared hook, funnels dropped files
// into the same handleFiles pipeline as the file picker, and adds no bespoke DnD or
// duplicate validation logic.
import mediaUploadSource from './MediaUpload.tsx?raw'

const mediaUploadCss = readFileSync(new URL('./MediaUpload.css', import.meta.url), 'utf8')

describe('MediaUpload — drag-and-drop', () => {
  it('uses the shared useMediaDropzone hook rather than bespoke drag logic', () => {
    expect(mediaUploadSource).toMatch(/import \{ useMediaDropzone \} from '\.\.\/hooks\/useMediaDropzone'/)
    expect(mediaUploadSource).toMatch(/const \{ isDragActive, dropzoneHandlers \} = useMediaDropzone\(\{/)
    // The component must not re-implement file extraction / drag plumbing itself.
    expect(mediaUploadSource).not.toMatch(/dataTransfer/)
    expect(mediaUploadSource).not.toMatch(/createMediaDropController/)
  })

  it('routes dropped files through the same handleFiles entry point as the picker', () => {
    expect(mediaUploadSource).toMatch(/onFiles: handleFiles/)
    expect(mediaUploadSource).toMatch(/onChange=\{handleFileSelect\}/)
    expect(mediaUploadSource).toMatch(/const handleFileSelect = async[\s\S]*await handleFiles\(files\)/)
    expect(mediaUploadSource).toMatch(/const handleFiles = async \(files: File\[\]\) =>/)
  })

  it('disables drops while uploading or when the control is disabled', () => {
    expect(mediaUploadSource).toMatch(/useMediaDropzone\(\{\s*disabled: disabled \|\| uploading,\s*onFiles: handleFiles,/)
  })

  it('spreads the drag handlers onto the drop zone and shows the active state + copy', () => {
    expect(mediaUploadSource).toMatch(/\{\.\.\.dropzoneHandlers\}/)
    expect(mediaUploadSource).toMatch(/isDragActive \? 'drag-active' : ''/)
    expect(mediaUploadSource).toMatch(/isDragActive \? \(\s*<span className="upload-text">Drop files here<\/span>/)
  })

  it('rejects multiple files on the single-media surface instead of silently choosing one', () => {
    expect(mediaUploadSource).toMatch(/if \(files\.length > 1\) \{[\s\S]*onUploadError\(/)
  })

  it('keeps existing validation copy for the single file path (no duplicated validation)', () => {
    // Same friendly client pre-validation helpers as before — unchanged by DnD.
    expect(mediaUploadSource).toMatch(/resolveClientMediaError\(file, selectedPlatform, placement\)/)
    expect(mediaUploadSource).toMatch(/resolveClientDimensionError\(dims\.width, dims\.height, selectedPlatform, placement\)/)
  })

  it('keeps click + Enter/Space keyboard activation on the drop zone', () => {
    expect(mediaUploadSource).toMatch(/onClick=\{handleClick\}/)
    expect(mediaUploadSource).toMatch(/onKeyDown=\{handleKeyActivate\}/)
    expect(mediaUploadSource).toMatch(/e\.key === 'Enter' \|\| e\.key === ' '/)
    expect(mediaUploadSource).toMatch(/e\.preventDefault\(\)\s*handleClick\(\)/)
  })

  it('styles a subtle active drag-over state and holds the zone height (no layout shift)', () => {
    expect(mediaUploadCss).toMatch(/\.upload-area\.drag-active \{[\s\S]*border-color: #667eea;/)
    expect(mediaUploadCss).toMatch(/\.upload-placeholder \{[\s\S]*min-height: 7\.5rem;/)
  })
})
