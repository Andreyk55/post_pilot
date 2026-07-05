import { describe, expect, it } from 'vitest'
import { getClientValidationRule, preValidateFile } from '../constants/mediaValidationRules'
import {
  getInstagramFormatHint,
  INSTAGRAM_IMAGE_FORMAT_HINT,
  validateInstagramSelection,
} from './instagramMediaValidation'

const file = (name: string, type: string, size = 1000): File =>
  new File(['x'.repeat(size)], name, { type })

describe('instagram media format validation', () => {
  it('allows PNG and JPEG images for Instagram client validation', () => {
    const feedRule = getClientValidationRule('instagram', 'Feed', 'Image')
    const storyRule = getClientValidationRule('instagram', 'Story', 'Image')

    expect(feedRule?.allowedMimeTypes).toContain('image/jpeg')
    expect(feedRule?.allowedMimeTypes).toContain('image/png')
    expect(storyRule?.allowedMimeTypes).toContain('image/jpeg')
    expect(storyRule?.allowedMimeTypes).toContain('image/png')
    expect(preValidateFile(file('photo.png', 'image/png'), 'instagram', 'Feed')).toEqual([])
  })

  it('still blocks WebP for Instagram', () => {
    const selection = validateInstagramSelection([], [{ name: 'photo.webp', type: 'image/webp' }])

    expect(selection.ok).toBe(false)
    expect(selection.errorMessage).toContain('WebP is not supported')
    expect(preValidateFile(file('photo.webp', 'image/webp'), 'instagram', 'Feed')).toEqual([
      'File type "image/webp" is not supported for instagram. Allowed: image/jpeg, image/png',
    ])
  })

  it('allows MOV (video/quicktime) for Instagram — iPhone compatibility', () => {
    const selection = validateInstagramSelection([], [{ name: 'clip.mov', type: 'video/quicktime' }])
    expect(selection.ok).toBe(true)
    expect(selection.errorMessage).toBeNull()
  })

  it('explains PNG auto-conversion and WebP/HEIC unsupported copy', () => {
    expect(INSTAGRAM_IMAGE_FORMAT_HINT).toBe(
      'Instagram requires JPEG. PNG images will be converted automatically. WebP and HEIC are not supported yet.'
    )
    expect(getInstagramFormatHint('empty')).toContain('PNG is converted to JPEG')
    expect(getInstagramFormatHint('empty')).toContain('WebP and HEIC are not supported')
  })

  it('names formats, the 8MB image limit, and the 3–180s video range in the empty-state hint', () => {
    const hint = getInstagramFormatHint('empty')
    expect(hint).toContain('JPG/PNG')
    expect(hint).toContain('8MB')
    expect(hint).toContain('MP4/MOV')
    expect(hint).toContain('3–180 seconds')
  })

  it('rejects HEIC with dedicated product-limitation copy', () => {
    const byMime = validateInstagramSelection([], [{ name: 'IMG_0001.heic', type: 'image/heic' }])
    expect(byMime.ok).toBe(false)
    expect(byMime.errorMessage).toBe('HEIC is not supported yet. Please upload a JPG or PNG image.')

    // Browsers sometimes report an empty MIME type for HEIC — the extension still catches it.
    const byName = validateInstagramSelection([], [{ name: 'IMG_0002.HEIC', type: '' }])
    expect(byName.ok).toBe(false)
    expect(byName.errorMessage).toBe('HEIC is not supported yet. Please upload a JPG or PNG image.')
  })
})
