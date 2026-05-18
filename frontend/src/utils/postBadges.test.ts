import { describe, it, expect } from 'vitest'
import type { Post } from '../api/posts'
import { getPostTypeBadge, getMediaSummaryBadge, getContentBadges, formatMediaCounts } from './postBadges'

/** Helper to build a minimal Post with overrides */
function makePost(overrides: Partial<Post> = {}): Post {
  return {
    id: '1',
    content: '',
    mediaUrl: null,
    mediaType: 'None',
    postType: 'Feed',
    platform: 'Instagram',
    scheduledAt: '',
    status: 'Scheduled',
    createdAt: '',
    updatedAt: '',
    targetPageId: null,
    targetPageName: null,
    targetInstagramAccountId: null,
    targetInstagramAccountName: null,
    publishedAt: null,
    externalPostId: null,
    externalPostUrl: null,
    profileUrl: null,
    errorMessage: null,
    retryCount: 0,
    selectedThumbnailUrl: null,
    instagramMediaType: null,
    mediaItems: null,
    processingPollCount: 0,
    nextRetryAt: null,
    ...overrides,
  }
}

// ─── formatMediaCounts ───────────────────────────────────────────

describe('formatMediaCounts', () => {
  it('photos only => "5p"', () => {
    expect(formatMediaCounts(5, 0)).toBe('5p')
  })

  it('videos only => "1v"', () => {
    expect(formatMediaCounts(0, 1)).toBe('1v')
  })

  it('mixed => "2p+3v"', () => {
    expect(formatMediaCounts(2, 3)).toBe('2p+3v')
  })

  it('both zero => empty string', () => {
    expect(formatMediaCounts(0, 0)).toBe('')
  })

  it('no extra spaces around "+"', () => {
    const result = formatMediaCounts(1, 1)
    expect(result).toBe('1p+1v')
    expect(result).not.toContain(' ')
  })
})

// ─── getPostTypeBadge ────────────────────────────────────────────

describe('getPostTypeBadge', () => {
  it('returns Post for a feed post', () => {
    const badge = getPostTypeBadge(makePost({ postType: 'Feed' }))
    expect(badge.text).toBe('Post')
    expect(badge.dataType).toBe('post')
  })

  it('returns Story for a story', () => {
    const badge = getPostTypeBadge(makePost({ postType: 'Story' }))
    expect(badge.text).toBe('Story')
    expect(badge.dataType).toBe('story')
  })

  it('returns Reel when instagramMediaType is Reels', () => {
    const badge = getPostTypeBadge(makePost({ instagramMediaType: 'Reels' }))
    expect(badge.text).toBe('Reel')
    expect(badge.dataType).toBe('reel')
  })

  it('returns Carousel when mediaItems >= 2', () => {
    const badge = getPostTypeBadge(makePost({
      mediaItems: [
        { id: '1', order: 0, mediaUrl: 'a.jpg', mediaType: 'Image' },
        { id: '2', order: 1, mediaUrl: 'b.jpg', mediaType: 'Image' },
      ],
    }))
    expect(badge.text).toBe('Carousel')
    expect(badge.dataType).toBe('carousel')
  })

  it('returns Carousel for CarouselAlbum instagramMediaType', () => {
    const badge = getPostTypeBadge(makePost({ instagramMediaType: 'CarouselAlbum' }))
    expect(badge.text).toBe('Carousel')
    expect(badge.dataType).toBe('carousel')
  })

  it('returns Post for Facebook even with multiple media items', () => {
    const badge = getPostTypeBadge(makePost({
      platform: 'Facebook',
      mediaItems: [
        { id: '1', order: 0, mediaUrl: 'a.jpg', mediaType: 'Image' },
        { id: '2', order: 1, mediaUrl: 'b.jpg', mediaType: 'Image' },
        { id: '3', order: 2, mediaUrl: 'c.jpg', mediaType: 'Image' },
      ],
    }))
    expect(badge.text).toBe('Post')
    expect(badge.dataType).toBe('post')
  })

  it('returns Post for feed even when instagramMediaType is Image', () => {
    const badge = getPostTypeBadge(makePost({ instagramMediaType: 'Image' }))
    expect(badge.text).toBe('Post')
  })
})

// ─── getMediaSummaryBadge ────────────────────────────────────────

describe('getMediaSummaryBadge', () => {
  it('returns Text when no media', () => {
    const badge = getMediaSummaryBadge(makePost())
    expect(badge.text).toBe('Text')
    expect(badge.dataType).toBe('text')
  })

  it('returns Photo for single image', () => {
    const badge = getMediaSummaryBadge(makePost({ mediaType: 'Image', mediaUrl: 'img.jpg' }))
    expect(badge.text).toBe('Photo')
    expect(badge.dataType).toBe('image')
  })

  it('returns Video for single video', () => {
    const badge = getMediaSummaryBadge(makePost({ mediaType: 'Video', mediaUrl: 'vid.mp4' }))
    expect(badge.text).toBe('Video')
    expect(badge.dataType).toBe('video')
  })

  it('returns Photos (n) for multiple images', () => {
    const badge = getMediaSummaryBadge(makePost({
      mediaItems: [
        { id: '1', order: 0, mediaUrl: 'a.jpg', mediaType: 'Image' },
        { id: '2', order: 1, mediaUrl: 'b.jpg', mediaType: 'Image' },
        { id: '3', order: 2, mediaUrl: 'c.jpg', mediaType: 'Image' },
      ],
    }))
    expect(badge.text).toBe('Photos (3)')
    expect(badge.dataType).toBe('image')
  })

  it('returns Videos (n) for multiple videos', () => {
    const badge = getMediaSummaryBadge(makePost({
      mediaItems: [
        { id: '1', order: 0, mediaUrl: 'a.mp4', mediaType: 'Video' },
        { id: '2', order: 1, mediaUrl: 'b.mp4', mediaType: 'Video' },
      ],
    }))
    expect(badge.text).toBe('Videos (2)')
    expect(badge.dataType).toBe('video')
  })

  it('returns mixed format for photos + videos', () => {
    const badge = getMediaSummaryBadge(makePost({
      mediaItems: [
        { id: '1', order: 0, mediaUrl: 'a.jpg', mediaType: 'Image' },
        { id: '2', order: 1, mediaUrl: 'b.jpg', mediaType: 'Image' },
        { id: '3', order: 2, mediaUrl: 'c.mp4', mediaType: 'Video' },
        { id: '4', order: 3, mediaUrl: 'd.mp4', mediaType: 'Video' },
        { id: '5', order: 4, mediaUrl: 'e.mp4', mediaType: 'Video' },
      ],
    }))
    expect(badge.text).toBe('photos (2) + videos (3)')
    expect(badge.dataType).toBe('carouselalbum')
  })

  it('returns Video for reel (single video)', () => {
    const badge = getMediaSummaryBadge(makePost({
      mediaType: 'Video',
      mediaUrl: 'reel.mp4',
      instagramMediaType: 'Reels',
    }))
    expect(badge.text).toBe('Video')
  })
})

// ─── getContentBadges (combined) ─────────────────────────────────

describe('getContentBadges', () => {
  it('returns exactly 2 badges: [postType, media]', () => {
    const badges = getContentBadges(makePost())
    expect(badges).toHaveLength(2)
    expect(badges[0].key).toBe('postType')
    expect(badges[1].key).toBe('media')
  })

  it('Reel | Video scenario', () => {
    const badges = getContentBadges(makePost({
      instagramMediaType: 'Reels',
      mediaType: 'Video',
      mediaUrl: 'reel.mp4',
      status: 'Published',
    }))
    expect(badges[0].text).toBe('Reel')
    expect(badges[1].text).toBe('Video')
  })

  it('Story | Photo scenario', () => {
    const badges = getContentBadges(makePost({
      postType: 'Story',
      mediaType: 'Image',
      mediaUrl: 'story.jpg',
    }))
    expect(badges[0].text).toBe('Story')
    expect(badges[1].text).toBe('Photo')
  })

  it('Carousel | Photos (4) scenario', () => {
    const badges = getContentBadges(makePost({
      mediaItems: [
        { id: '1', order: 0, mediaUrl: 'a.jpg', mediaType: 'Image' },
        { id: '2', order: 1, mediaUrl: 'b.jpg', mediaType: 'Image' },
        { id: '3', order: 2, mediaUrl: 'c.jpg', mediaType: 'Image' },
        { id: '4', order: 3, mediaUrl: 'd.jpg', mediaType: 'Image' },
      ],
    }))
    expect(badges[0].text).toBe('Carousel')
    expect(badges[1].text).toBe('Photos (4)')
  })

  it('Carousel | photos (2) + videos (3) scenario', () => {
    const badges = getContentBadges(makePost({
      mediaItems: [
        { id: '1', order: 0, mediaUrl: 'a.jpg', mediaType: 'Image' },
        { id: '2', order: 1, mediaUrl: 'b.jpg', mediaType: 'Image' },
        { id: '3', order: 2, mediaUrl: 'c.mp4', mediaType: 'Video' },
        { id: '4', order: 3, mediaUrl: 'd.mp4', mediaType: 'Video' },
        { id: '5', order: 4, mediaUrl: 'e.mp4', mediaType: 'Video' },
      ],
    }))
    expect(badges[0].text).toBe('Carousel')
    expect(badges[1].text).toBe('photos (2) + videos (3)')
  })

  it('Post | Text scenario', () => {
    const badges = getContentBadges(makePost())
    expect(badges[0].text).toBe('Post')
    expect(badges[1].text).toBe('Text')
  })

  it('Post | Photo (single image feed post)', () => {
    const badges = getContentBadges(makePost({
      mediaType: 'Image',
      mediaUrl: 'photo.jpg',
    }))
    expect(badges[0].text).toBe('Post')
    expect(badges[1].text).toBe('Photo')
  })

  it('Post | Video (single video feed post)', () => {
    const badges = getContentBadges(makePost({
      mediaType: 'Video',
      mediaUrl: 'vid.mp4',
    }))
    expect(badges[0].text).toBe('Post')
    expect(badges[1].text).toBe('Video')
  })

  it('Story | Video scenario', () => {
    const badges = getContentBadges(makePost({
      postType: 'Story',
      mediaType: 'Video',
      mediaUrl: 'story.mp4',
    }))
    expect(badges[0].text).toBe('Story')
    expect(badges[1].text).toBe('Video')
  })

  it('Carousel | Videos (3) (all-video carousel)', () => {
    const badges = getContentBadges(makePost({
      mediaItems: [
        { id: '1', order: 0, mediaUrl: 'a.mp4', mediaType: 'Video' },
        { id: '2', order: 1, mediaUrl: 'b.mp4', mediaType: 'Video' },
        { id: '3', order: 2, mediaUrl: 'c.mp4', mediaType: 'Video' },
      ],
    }))
    expect(badges[0].text).toBe('Carousel')
    expect(badges[1].text).toBe('Videos (3)')
  })
})
