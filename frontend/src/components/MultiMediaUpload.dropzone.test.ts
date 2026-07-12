import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
// Source-level guarantees (the project has no DOM test env) that the multi-media
// (carousel) uploader wires real drag-and-drop through the same shared hook, funnels
// dropped files into the existing handleFiles pipeline (so max-count / mixed-media rules
// still apply), and adds no bespoke DnD or duplicate validation logic.
import multiMediaUploadSource from './MultiMediaUpload.tsx?raw'

const multiMediaUploadCss = readFileSync(new URL('./MultiMediaUpload.css', import.meta.url), 'utf8')

describe('MultiMediaUpload — drag-and-drop', () => {
  it('uses the shared useMediaDropzone hook rather than bespoke drag logic', () => {
    expect(multiMediaUploadSource).toMatch(/import \{ useMediaDropzone \} from '\.\.\/hooks\/useMediaDropzone'/)
    expect(multiMediaUploadSource).toMatch(/const \{ isDragActive, dropzoneHandlers \} = useMediaDropzone\(\{/)
    expect(multiMediaUploadSource).not.toMatch(/dataTransfer/)
    expect(multiMediaUploadSource).not.toMatch(/createMediaDropController/)
  })

  it('routes dropped files through the same handleFiles entry point as the picker', () => {
    expect(multiMediaUploadSource).toMatch(/onFiles: handleFiles/)
    expect(multiMediaUploadSource).toMatch(/onChange=\{handleFileSelect\}/)
    expect(multiMediaUploadSource).toMatch(/const handleFileSelect = async[\s\S]*await handleFiles\(files\)/)
    expect(multiMediaUploadSource).toMatch(/const handleFiles = async \(files: File\[\]\) =>/)
  })

  it('disables drops while uploading, when disabled, or when already at capacity', () => {
    expect(multiMediaUploadSource).toMatch(
      /useMediaDropzone\(\{\s*disabled: disabled \|\| uploading \|\| !canAddMore,\s*onFiles: handleFiles,/,
    )
  })

  it('accepts drops on both the empty drop zone and the "add more" tile', () => {
    // Two drop targets share the same handlers spread.
    const spreadCount = (multiMediaUploadSource.match(/\{\.\.\.dropzoneHandlers\}/g) ?? []).length
    expect(spreadCount).toBe(2)
  })

  it('shows the active state + copy on the empty zone and highlights the add tile', () => {
    expect(multiMediaUploadSource).toMatch(/isDragActive \? 'drag-active' : ''/)
    expect(multiMediaUploadSource).toMatch(/isDragActive \? \(\s*<span className="upload-text">Drop files here<\/span>/)
    expect(multiMediaUploadSource).toMatch(/\{isDragActive \? 'Drop' : 'Add'\}/)
  })

  it('preserves the existing per-platform selection validation (no duplicated validation)', () => {
    // Instagram/Facebook count + mixed-media rules unchanged, still applied in handleFiles.
    expect(multiMediaUploadSource).toMatch(/validateInstagramSelection\(existingAsInfo, newAsInfo\)/)
    expect(multiMediaUploadSource).toMatch(/validateFacebookSelection\(existingAsInfo, newAsInfo\)/)
    expect(multiMediaUploadSource).toMatch(/resolveClientMediaError\(file, selectedPlatform, 'Feed'\)/)
  })

  it('keeps click + Enter/Space keyboard activation on both drop targets', () => {
    expect(multiMediaUploadSource).toMatch(/onKeyDown=\{handleKeyActivate\}/)
    expect(multiMediaUploadSource).toMatch(/e\.key === 'Enter' \|\| e\.key === ' '/)
    expect(multiMediaUploadSource).toMatch(/e\.preventDefault\(\)\s*handleClick\(\)/)
  })

  it('styles a subtle active drag-over state for both targets and holds zone height', () => {
    expect(multiMediaUploadCss).toMatch(/\.multi-media-upload \.upload-area\.drag-active \{[\s\S]*border-color: #667eea;/)
    expect(multiMediaUploadCss).toMatch(/\.carousel-add-btn\.drag-active \{[\s\S]*border-color: #667eea;/)
    expect(multiMediaUploadCss).toMatch(/\.multi-media-upload \.upload-placeholder \{[\s\S]*min-height: 7\.5rem;/)
  })
})
