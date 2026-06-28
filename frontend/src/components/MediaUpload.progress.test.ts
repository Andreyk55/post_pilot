/// <reference types="node" />
import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import mediaUploadSource from './MediaUpload.tsx?raw'
import multiMediaUploadSource from './MultiMediaUpload.tsx?raw'

const mediaUploadCss = readFileSync(new URL('./MediaUpload.css', import.meta.url), 'utf8')
const multiMediaUploadCss = readFileSync(new URL('./MultiMediaUpload.css', import.meta.url), 'utf8')

describe('Schedule Post media upload progress', () => {
  it('starts each single-media upload session at 0 before making upload requests', () => {
    const resetIndex = mediaUploadSource.indexOf('setProgress(0)')
    const initIndex = mediaUploadSource.indexOf('mediaApi.initUpload')

    expect(resetIndex).toBeGreaterThan(-1)
    expect(initIndex).toBeGreaterThan(-1)
    expect(resetIndex).toBeLessThan(initIndex)
    expect(mediaUploadSource).not.toMatch(/setProgress\((10|20|40|50|60|75|85)\)/)
  })

  it('drops stale single-media progress, completion, validation, and error updates', () => {
    expect(mediaUploadSource).toMatch(/const uploadOwnerKey = beginUploadSession\(\)/)
    expect(mediaUploadSource).toMatch(/activeUploadOwnerKeyRef\.current = uploadOwnerKey/)
    expect(mediaUploadSource).toMatch(/mediaApi\.uploadFile\(uploadUrl, file, \(progressPercent\) =>/)
    expect(mediaUploadSource).toMatch(/if \(!isStaleUploadOwner\(uploadOwnerKey\)\) \{\s*setProgress/)
    expect(mediaUploadSource).toMatch(/if \(isStaleUploadOwner\(uploadOwnerKey\)\) return[\s\S]*onUploadComplete/)
    expect(mediaUploadSource).toMatch(/catch \(err\) \{\s*if \(isStaleUploadOwner\(uploadOwnerKey\)\) return/)
  })

  it('starts each multi-media upload session at 0 and avoids fixed fake progress', () => {
    const resetIndex = multiMediaUploadSource.indexOf('setProgress(0)')
    const initIndex = multiMediaUploadSource.indexOf('mediaApi.initUpload')

    expect(resetIndex).toBeGreaterThan(-1)
    expect(initIndex).toBeGreaterThan(-1)
    expect(resetIndex).toBeLessThan(initIndex)
    expect(multiMediaUploadSource).not.toContain("width: '60%'")
    expect(multiMediaUploadSource).toMatch(/style=\{\{ width: `\$\{progress\}%` \}\}/)
  })

  it('drops stale multi-media progress and completion updates', () => {
    expect(multiMediaUploadSource).toMatch(/const uploadOwnerKey = beginUploadSession\(\)/)
    expect(multiMediaUploadSource).toMatch(/activeUploadOwnerKeyRef\.current = uploadOwnerKey/)
    expect(multiMediaUploadSource).toMatch(/mediaApi\.uploadFile\(uploadUrl, file, \(progressPercent\) =>/)
    expect(multiMediaUploadSource).toMatch(/if \(!isStaleUploadOwner\(uploadOwnerKey\)\) \{[\s\S]*setProgress/)
    expect(multiMediaUploadSource).toMatch(/if \(isStaleUploadOwner\(uploadOwnerKey\)\) return[\s\S]*onItemsChange/)
  })

  it('renders upload progress bars from the left edge', () => {
    for (const source of [mediaUploadCss, multiMediaUploadCss]) {
      expect(source).toMatch(/\.upload-progress\s*\{[\s\S]*overflow: hidden;/)
      expect(source).toMatch(/\.progress-bar\s*\{[\s\S]*left: 0;/)
      expect(source).toMatch(/\.progress-bar\s*\{[\s\S]*transform-origin: left center;/)
      expect(source).not.toMatch(/left:\s*50%/)
      expect(source).not.toMatch(/translateX/)
    }
  })
})
