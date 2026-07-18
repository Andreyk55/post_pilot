import { describe, expect, it } from 'vitest'
import { getClientValidationRule, preValidateFile } from '../constants/mediaValidationRules'
import type { MediaValidationError, MediaValidationWarning, ValidationStatus } from '../constants/mediaValidationRules'
import {
  getInstagramFormatHint,
  getInstagramMediaMode,
  getInstagramUploaderLabel,
  INSTAGRAM_IMAGE_FORMAT_HINT,
  validateInstagramSelection,
  validateInstagramFeedVideoDuration,
  revalidateInstagramFeedCollection,
  type RevalidatableMediaItem,
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

describe('instagram carousels (mixed media + carousel video duration copy)', () => {
  const jpg = { name: 'a.jpg', type: 'image/jpeg' }
  const mov = { name: 'b.mov', type: 'video/quicktime' }
  const mp4 = { name: 'c.mp4', type: 'video/mp4' }

  it('allows a mixed image + video carousel selection (2–10 items)', () => {
    const result = validateInstagramSelection([jpg], [mp4])
    expect(result.ok).toBe(true)
    expect(result.errorMessage).toBeNull()
    expect(result.nextFiles).toHaveLength(2)
  })

  it('classifies a mixed selection as a mixed carousel and preserves selection order', () => {
    const files = [jpg, mp4, mov]
    expect(getInstagramMediaMode(files)).toBe('carousel_mixed')
    // Order is preserved in the file list the composer keeps.
    const result = validateInstagramSelection([jpg], [mp4, mov])
    expect(result.nextFiles.map(f => f.name)).toEqual(['a.jpg', 'c.mp4', 'b.mov'])
  })

  it('single and carousel video duration copy differ (180s single vs 60s carousel)', () => {
    // A single video (Reel) allows 3–180s; carousel video items are capped at 3–60s.
    expect(getInstagramFormatHint('single_video')).toContain('3–180 seconds')
    expect(getInstagramFormatHint('single_video')).not.toContain('3–60 seconds')
    expect(getInstagramFormatHint('carousel_videos')).toContain('3–60 seconds')
    expect(getInstagramFormatHint('carousel_mixed')).toContain('3–60 seconds')
  })

  it('still labels the destination/type "Instagram Feed" style (Reel for a single video)', () => {
    // The single IG video is user-facing "Feed" content (published by Meta as a Reel); the
    // uploader keeps the familiar carousel/photo/video labels — never "Instagram Reel post".
    expect(getInstagramUploaderLabel('single_video', 1)).toBe('Reel selected')
    expect(getInstagramUploaderLabel('carousel_mixed', 3)).toContain('carousel')
    expect(INSTAGRAM_IMAGE_FORMAT_HINT).toContain('Instagram')
  })
})

// ── Count-dependent video-duration revalidation (single ↔ carousel) ──────────

const CAROUSEL_MSG = 'Videos in an Instagram Feed carousel must be between 3 and 60 seconds.'
const SINGLE_MSG = 'Instagram Feed videos must be between 3 and 180 seconds.'

const err = (code: string, message = 'x'): MediaValidationError => ({
  code, field: 'f', message, expected: null, actual: null,
})
const FILE_TOO_LARGE = err('FILE_TOO_LARGE', 'This video is too large. Instagram videos can be up to 50MB.')

type TestItem = RevalidatableMediaItem & { id: string }

const videoItem = (
  id: string,
  durationSeconds: number | null,
  extraErrors: MediaValidationError[] = [],
  warnings: MediaValidationWarning[] = [],
): TestItem => ({
  id,
  mediaType: 'Video',
  durationSeconds,
  validationErrors: [...extraErrors],
  validationWarnings: [...warnings],
  validationStatus: (extraErrors.length ? 'Invalid' : warnings.length ? 'Warning' : 'Valid') as ValidationStatus,
})

const imageItem = (id: string): TestItem => ({
  id,
  mediaType: 'Image',
  durationSeconds: null,
  validationErrors: [],
  validationWarnings: [],
  validationStatus: 'Valid',
})

const durationErrors = (item: RevalidatableMediaItem) =>
  item.validationErrors.filter(e => e.code === 'DURATION_TOO_LONG' || e.code === 'DURATION_TOO_SHORT')

describe('validateInstagramFeedVideoDuration (single 3–180s vs carousel 3–60s)', () => {
  it('3s is valid as single and in a carousel (inclusive lower bound)', () => {
    expect(validateInstagramFeedVideoDuration(3, false).valid).toBe(true)
    expect(validateInstagramFeedVideoDuration(3, true).valid).toBe(true)
  })

  it('60s is valid as single and in a carousel (inclusive carousel upper bound)', () => {
    expect(validateInstagramFeedVideoDuration(60, false).valid).toBe(true)
    expect(validateInstagramFeedVideoDuration(60, true).valid).toBe(true)
  })

  it('61s is valid as single but invalid in a carousel with the carousel message', () => {
    expect(validateInstagramFeedVideoDuration(61, false).valid).toBe(true)
    const c = validateInstagramFeedVideoDuration(61, true)
    expect(c.valid).toBe(false)
    expect(c.code).toBe('DURATION_TOO_LONG')
    expect(c.message).toBe(CAROUSEL_MSG)
  })

  it('180s is valid as single (inclusive) but invalid in a carousel', () => {
    expect(validateInstagramFeedVideoDuration(180, false).valid).toBe(true)
    expect(validateInstagramFeedVideoDuration(180, true).valid).toBe(false)
  })

  it('181s is invalid as single (single message) and invalid in a carousel (carousel message)', () => {
    const s = validateInstagramFeedVideoDuration(181, false)
    expect(s.valid).toBe(false)
    expect(s.code).toBe('DURATION_TOO_LONG')
    expect(s.message).toBe(SINGLE_MSG)
    expect(validateInstagramFeedVideoDuration(181, true).message).toBe(CAROUSEL_MSG)
  })

  it('below 3s is invalid in both contexts (DURATION_TOO_SHORT, matching range copy)', () => {
    const s = validateInstagramFeedVideoDuration(2, false)
    expect(s.valid).toBe(false)
    expect(s.code).toBe('DURATION_TOO_SHORT')
    expect(s.message).toBe(SINGLE_MSG)
    const c = validateInstagramFeedVideoDuration(2, true)
    expect(c.valid).toBe(false)
    expect(c.code).toBe('DURATION_TOO_SHORT')
    expect(c.message).toBe(CAROUSEL_MSG)
  })

  it('sources its bounds from the shared rule mirror (numbers are not duplicated)', () => {
    const single = getClientValidationRule('instagram', 'Feed', 'Video')
    const carousel = getClientValidationRule('instagram', 'Feed', 'Video', { carousel: true })
    expect(validateInstagramFeedVideoDuration(9999, false).message).toContain(String(single?.durationMaxSeconds))
    expect(validateInstagramFeedVideoDuration(9999, true).message).toContain(String(carousel?.durationMaxSeconds))
  })
})

describe('revalidateInstagramFeedCollection (single ↔ carousel transitions)', () => {
  it('#1 a valid 90s single video becomes invalid the instant a second item is added', () => {
    // Valid as the only item (single 3–180s).
    expect(revalidateInstagramFeedCollection([videoItem('v', 90)], 'instagram', 'Feed')[0].validationStatus).toBe('Valid')

    // Adding any second item makes it a carousel → the 90s video is immediately invalid.
    const afterAdd = revalidateInstagramFeedCollection([videoItem('v', 90), imageItem('i')], 'instagram', 'Feed')
    expect(afterAdd[0].validationStatus).toBe('Invalid')
    expect(durationErrors(afterAdd[0])).toEqual([
      expect.objectContaining({ code: 'DURATION_TOO_LONG', message: CAROUSEL_MSG }),
    ])
  })

  it('#2 a carousel-invalid 90s video becomes valid again when the extra item is removed (same item, not re-uploaded)', () => {
    const carousel = revalidateInstagramFeedCollection([videoItem('v', 90), imageItem('i')], 'instagram', 'Feed')
    expect(carousel[0].validationStatus).toBe('Invalid')

    const afterRemove = revalidateInstagramFeedCollection([carousel[0]], 'instagram', 'Feed')
    expect(afterRemove[0].validationStatus).toBe('Valid')
    expect(durationErrors(afterRemove[0])).toEqual([])
    // Same item object identity preserved (id, mediaId, preview would all survive the spread).
    expect((afterRemove[0] as TestItem).id).toBe('v')
  })

  it('preserves an unrelated file-size error across count changes and never duplicates the duration message', () => {
    const carousel = revalidateInstagramFeedCollection([videoItem('v', 90, [FILE_TOO_LARGE]), imageItem('i')], 'instagram', 'Feed')
    expect(carousel[0].validationErrors.filter(e => e.code === 'FILE_TOO_LARGE')).toHaveLength(1)
    expect(carousel[0].validationErrors.filter(e => e.code === 'DURATION_TOO_LONG')).toHaveLength(1)

    // Back to single: the carousel duration error is removed but the file-size error remains.
    const single = revalidateInstagramFeedCollection([carousel[0]], 'instagram', 'Feed')
    expect(single[0].validationErrors.filter(e => e.code === 'FILE_TOO_LARGE')).toHaveLength(1)
    expect(single[0].validationErrors.filter(e => e.code === 'DURATION_TOO_LONG')).toHaveLength(0)
    expect(single[0].validationStatus).toBe('Invalid') // still blocked by size
  })

  it('a 45s video stays valid in both contexts; a 200s video stays invalid in both', () => {
    expect(revalidateInstagramFeedCollection([videoItem('v', 45)], 'instagram', 'Feed')[0].validationStatus).toBe('Valid')
    expect(revalidateInstagramFeedCollection([videoItem('v', 45), imageItem('i')], 'instagram', 'Feed')[0].validationStatus).toBe('Valid')
    expect(revalidateInstagramFeedCollection([videoItem('v', 200)], 'instagram', 'Feed')[0].validationStatus).toBe('Invalid')
    expect(revalidateInstagramFeedCollection([videoItem('v', 200), imageItem('i')], 'instagram', 'Feed')[0].validationStatus).toBe('Invalid')
  })

  it('a second VIDEO also triggers revalidation of the existing video', () => {
    const afterAdd = revalidateInstagramFeedCollection([videoItem('a', 90), videoItem('b', 30)], 'instagram', 'Feed')
    expect(afterAdd[0].validationStatus).toBe('Invalid') // 90s → carousel-invalid
    expect(afterAdd[1].validationStatus).toBe('Valid')   // 30s fine
  })

  it('3→2 items keeps the carousel rule; 2→1 restores the single rule', () => {
    const three = revalidateInstagramFeedCollection([videoItem('a', 90), imageItem('b'), imageItem('c')], 'instagram', 'Feed')
    expect(three[0].validationStatus).toBe('Invalid')
    const two = revalidateInstagramFeedCollection([three[0], three[1]], 'instagram', 'Feed')
    expect(two[0].validationStatus).toBe('Invalid') // 2 items → still carousel
    const one = revalidateInstagramFeedCollection([two[0]], 'instagram', 'Feed')
    expect(one[0].validationStatus).toBe('Valid')   // single restored
  })

  it('removing an item from the MIDDLE of a 3-item carousel keeps the carousel rule (still 2 items)', () => {
    const three = revalidateInstagramFeedCollection([videoItem('a', 90), imageItem('b'), imageItem('c')], 'instagram', 'Feed')
    const afterMiddleRemove = revalidateInstagramFeedCollection([three[0], three[2]], 'instagram', 'Feed')
    expect(afterMiddleRemove[0].validationStatus).toBe('Invalid')
  })

  it('mixed and video-only carousels both revalidate every video item', () => {
    const mixed = revalidateInstagramFeedCollection([videoItem('a', 90), imageItem('b')], 'instagram', 'Feed')
    expect(mixed[0].validationStatus).toBe('Invalid')
    const videoOnly = revalidateInstagramFeedCollection([videoItem('a', 90), videoItem('b', 30)], 'instagram', 'Feed')
    expect(videoOnly[0].validationStatus).toBe('Invalid')
    expect(videoOnly[1].validationStatus).toBe('Valid')
  })

  it('identifies the correct item when only one of several videos exceeds 60s', () => {
    const out = revalidateInstagramFeedCollection(
      [videoItem('a', 30), videoItem('b', 90), videoItem('c', 45)], 'instagram', 'Feed')
    expect(out[0].validationStatus).toBe('Valid')
    expect(out[1].validationStatus).toBe('Invalid')
    expect(out[2].validationStatus).toBe('Valid')
    expect(durationErrors(out[1])[0].message).toBe(CAROUSEL_MSG)
  })

  it('reordering does not lose or misassign per-item errors', () => {
    const out = revalidateInstagramFeedCollection([videoItem('a', 30), videoItem('b', 90)], 'instagram', 'Feed')
    const reordered = revalidateInstagramFeedCollection([out[1], out[0]], 'instagram', 'Feed')
    expect((reordered[0] as TestItem).id).toBe('b')
    expect(reordered[0].validationStatus).toBe('Invalid') // 90s stays invalid
    expect(reordered[1].validationStatus).toBe('Valid')   // 30s stays valid
  })

  it('is idempotent — running twice returns the SAME reference (no effect loop)', () => {
    const once = revalidateInstagramFeedCollection([videoItem('a', 90), imageItem('b')], 'instagram', 'Feed')
    expect(revalidateInstagramFeedCollection(once, 'instagram', 'Feed')).toBe(once)
  })
})

describe('revalidateInstagramFeedCollection isolation', () => {
  it('never touches Facebook Feed, Facebook Story, or Instagram Story collections', () => {
    const coll = [videoItem('v', 90), imageItem('i')]
    expect(revalidateInstagramFeedCollection(coll, 'facebook', 'Feed')).toBe(coll)
    expect(revalidateInstagramFeedCollection(coll, 'facebook', 'Story')).toBe(coll)
    expect(revalidateInstagramFeedCollection(coll, 'instagram', 'Story')).toBe(coll)
  })

  it('never touches Instagram Feed images (image-only carousel returned unchanged)', () => {
    const imgs = [imageItem('a'), imageItem('b')]
    expect(revalidateInstagramFeedCollection(imgs, 'instagram', 'Feed')).toBe(imgs)
  })

  it('leaves video items with no known duration untouched (restored/legacy media)', () => {
    const noDuration = [videoItem('a', null), imageItem('b')]
    expect(revalidateInstagramFeedCollection(noDuration, 'instagram', 'Feed')).toBe(noDuration)
  })
})
