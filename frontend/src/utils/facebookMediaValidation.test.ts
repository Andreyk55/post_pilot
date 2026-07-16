import { describe, expect, it } from 'vitest'
import {
  validateFacebookSelection,
  getFacebookFormatHint,
  getFacebookMediaMode,
  isVideoFile,
} from './facebookMediaValidation'

const info = (name: string, type: string) => ({ name, type })

describe('facebook media format validation — final product policy', () => {
  it('accepts MOV (video/quicktime) as a single video — iPhone compatibility', () => {
    expect(isVideoFile(info('clip.mov', 'video/quicktime'))).toBe(true)
    const result = validateFacebookSelection([], [info('clip.mov', 'video/quicktime')])
    expect(result.ok).toBe(true)
    expect(result.errorMessage).toBeNull()
  })

  it('accepts MP4 as a single video', () => {
    const result = validateFacebookSelection([], [info('clip.mp4', 'video/mp4')])
    expect(result.ok).toBe(true)
  })

  it('rejects AVI (no longer supported)', () => {
    expect(isVideoFile(info('clip.avi', 'video/x-msvideo'))).toBe(false)
    const result = validateFacebookSelection([], [info('clip.avi', 'video/x-msvideo')])
    expect(result.ok).toBe(false)
    expect(result.errorMessage).toContain('JPG, PNG, MP4, or MOV')
  })

  it('advertises MP4 or MOV (never AVI/GIF/WebP) in the format hint', () => {
    expect(getFacebookFormatHint('empty')).toBe(
      'Photos: JPG/PNG up to 10MB. Videos: MP4/MOV, 3–180 seconds. HEIC is not supported yet.',
    )
    expect(getFacebookFormatHint('single_video')).toBe('MP4 or MOV, 3–180 seconds')
    const hints = [
      getFacebookFormatHint('empty'),
      getFacebookFormatHint('single_video'),
      getFacebookFormatHint('single_image'),
      getFacebookFormatHint('multi_photo'),
    ].join(' ')
    for (const banned of ['AVI', 'WebM', 'GIF', 'WebP', 'BMP', 'TIFF']) {
      expect(hints).not.toContain(banned)
    }
  })

  it('names the 10MB image limit and the 3–180s video range in the empty-state hint', () => {
    const hint = getFacebookFormatHint('empty')
    expect(hint).toContain('JPG/PNG')
    expect(hint).toContain('10MB')
    expect(hint).toContain('MP4/MOV')
    expect(hint).toContain('3–180 seconds')
  })

  it('rejects HEIC with dedicated product-limitation copy', () => {
    const byMime = validateFacebookSelection([], [info('IMG_0001.heic', 'image/heic')])
    expect(byMime.ok).toBe(false)
    expect(byMime.errorMessage).toBe('HEIC is not supported yet. Please upload a JPG or PNG image.')

    // Browsers sometimes report an empty MIME type for HEIC — the extension still catches it.
    const byName = validateFacebookSelection([], [info('IMG_0002.HEIC', '')])
    expect(byName.ok).toBe(false)
    expect(byName.errorMessage).toBe('HEIC is not supported yet. Please upload a JPG or PNG image.')
  })

  // ── Matrix behavior: FB Feed allows single image, single video, 2–10 image carousel ──

  it('allows a 2–10 image carousel', () => {
    const result = validateFacebookSelection(
      [info('a.jpg', 'image/jpeg')],
      [info('b.jpg', 'image/jpeg')],
    )
    expect(result.ok).toBe(true)
    expect(result.nextFiles).toHaveLength(2)
  })

  it('blocks mixed image+video carousel', () => {
    const result = validateFacebookSelection(
      [info('a.jpg', 'image/jpeg')],
      [info('clip.mp4', 'video/mp4')],
    )
    expect(result.ok).toBe(false)
  })

  it('blocks multiple videos', () => {
    const result = validateFacebookSelection(
      [info('first.mp4', 'video/mp4')],
      [info('second.mp4', 'video/mp4')],
    )
    expect(result.ok).toBe(false)
    expect(getFacebookMediaMode([info('first.mp4', 'video/mp4')])).toBe('single_video')
  })

  it('blocks selecting two videos at once', () => {
    const result = validateFacebookSelection(
      [],
      [info('first.mp4', 'video/mp4'), info('second.mp4', 'video/mp4')],
    )
    expect(result.ok).toBe(false)
    expect(result.nextFiles).toHaveLength(0)
  })

  // ── Image count boundary: FB Feed allows at most 10 images ──

  it('allows selecting exactly 10 images', () => {
    const ten = Array.from({ length: 10 }, (_, i) => info(`img${i}.jpg`, 'image/jpeg'))
    const result = validateFacebookSelection([], ten)
    expect(result.ok).toBe(true)
    expect(result.errorMessage).toBeNull()
    expect(result.nextFiles).toHaveLength(10)
  })

  it('rejects an 11th image once 10 are selected', () => {
    const ten = Array.from({ length: 10 }, (_, i) => info(`img${i}.jpg`, 'image/jpeg'))
    const result = validateFacebookSelection(ten, [info('extra.jpg', 'image/jpeg')])
    expect(result.ok).toBe(false)
    expect(result.errorMessage).toBe('Maximum 10 photos for carousel. Remove some photos first.')
    expect(result.nextFiles).toHaveLength(10)
  })

  it('truncates an over-limit batch to the remaining slots and says so', () => {
    const eight = Array.from({ length: 8 }, (_, i) => info(`img${i}.jpg`, 'image/jpeg'))
    const result = validateFacebookSelection(eight, [
      info('a.jpg', 'image/jpeg'),
      info('b.jpg', 'image/jpeg'),
      info('c.jpg', 'image/jpeg'),
    ])
    expect(result.ok).toBe(true)
    expect(result.errorMessage).toBe('Only 2 more photo(s) can be added. Max 10 total.')
    expect(result.nextFiles).toHaveLength(10)
  })
})
