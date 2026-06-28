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
    expect(getFacebookFormatHint('empty')).toBe('JPG, PNG, MP4, or MOV')
    expect(getFacebookFormatHint('single_video')).toBe('MP4 or MOV')
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
})
