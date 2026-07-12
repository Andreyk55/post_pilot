import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { Post, PostDetails, PostMediaItem } from '../api/posts'
import type { MediaType } from '../api/media'
import { PostItem } from './PostItem'

const basePost: Post = {
  id: 'post-1',
  content: 'Test post content',
  mediaUrl: null,
  mediaType: 'None',
  postType: 'Feed',
  platform: 'Facebook',
  scheduledAt: '2026-06-28T12:00:00.000Z',
  status: 'Published',
  createdAt: '2026-06-28T10:00:00.000Z',
  updatedAt: '2026-06-28T10:00:00.000Z',
  targetPageId: null,
  targetPageName: null,
  targetInstagramAccountId: null,
  targetInstagramAccountName: null,
  publishedAt: '2026-06-28T12:05:00.000Z',
  externalPostId: null,
  externalPostUrl: null,
  profileUrl: null,
  errorMessage: null,
  retryCount: 0,
  processingPollCount: 0,
  nextRetryAt: null,
  selectedThumbnailUrl: null,
  instagramMediaType: null,
  thumbnail: null,
  mediaItems: null,
}

const baseDetails: PostDetails = {
  id: 'post-1',
  content: 'Test post content',
  mediaUrl: null,
  mediaType: 'None',
  postType: 'Feed',
  platform: 'Facebook',
  scheduledAt: '2026-06-28T12:00:00.000Z',
  status: 'Published',
  createdAt: '2026-06-28T10:00:00.000Z',
  updatedAt: '2026-06-28T10:00:00.000Z',
  targetPageId: null,
  targetPageName: null,
  targetInstagramAccountId: null,
  targetInstagramAccountName: null,
  publishedAt: '2026-06-28T12:05:00.000Z',
  externalPostId: null,
  errorMessage: null,
  retryCount: 0,
  processingPollCount: 0,
  nextRetryAt: null,
  engagement: null,
  externalPostUrl: null,
  profileUrl: null,
  pageUrl: null,
  instagramMediaType: null,
  thumbnail: null,
  mediaItems: null,
}

function makeMediaItem(order: number, mediaType: MediaType = 'Image'): PostMediaItem {
  return {
    id: `item-${order}`,
    order,
    mediaUrl: `/api/media/item-${order}/file`,
    mediaType,
    thumbnail: null,
  }
}

function renderPostItem(post: Post, cachedDetails?: PostDetails) {
  return renderToStaticMarkup(
    <PostItem
      post={post}
      cachedDetails={cachedDetails}
      onDetailsFetched={() => {}}
    />,
  )
}

function countOccurrences(markup: string, needle: string): number {
  return markup.split(needle).length - 1
}

describe('PostItem media previews', () => {
  it('renders a video thumbnail image with a centered play overlay when thumbnail exists', () => {
    const markup = renderPostItem({
      ...basePost,
      mediaUrl: '/api/media/11111111-1111-1111-1111-111111111111/file',
      mediaId: '11111111-1111-1111-1111-111111111111',
      mediaType: 'Video',
      thumbnail: {
        mediaId: '11111111-1111-1111-1111-111111111111',
        url: '/api/media/11111111-1111-1111-1111-111111111111/file?variant=thumbnail',
        mimeType: 'image/jpeg',
        width: 320,
        height: 180,
        sizeBytes: 1234,
        createdAtUtc: '2026-06-28T10:00:00.000Z',
      },
    })

    expect(markup).toContain('media-thumbnail-video-frame')
    expect(markup).toContain('media-thumbnail-video-overlay')
    expect(markup).toContain('video-play-icon')
    expect(markup).toContain('/api/media/11111111-1111-1111-1111-111111111111/file?variant=thumbnail')
  })

  it('renders the generic video placeholder with play icon when no thumbnail exists', () => {
    const markup = renderPostItem({
      ...basePost,
      mediaUrl: '/api/media/11111111-1111-1111-1111-111111111111/file',
      mediaId: '11111111-1111-1111-1111-111111111111',
      mediaType: 'Video',
    })

    expect(markup).toContain('media-thumbnail-video-placeholder')
    expect(markup).toContain('video-play-icon')
    expect(markup).not.toContain('media-thumbnail-video-overlay')
  })

  it('renders image post without play overlay', () => {
    const markup = renderPostItem({
      ...basePost,
      mediaUrl: '/api/media/11111111-1111-1111-1111-111111111111/file',
      mediaId: '11111111-1111-1111-1111-111111111111',
      mediaType: 'Image',
    })

    expect(markup).toContain('<img')
    expect(markup).toContain('/api/media/11111111-1111-1111-1111-111111111111/file')
    expect(markup).not.toContain('media-thumbnail-video-overlay')
    expect(markup).not.toContain('media-thumbnail-video-placeholder')
  })
})

describe('PostItem expanded MEDIA section', () => {
  const multiPhotoPost: Post = {
    ...basePost,
    mediaUrl: '/api/media/item-0/file',
    mediaType: 'Image',
    mediaItems: [makeMediaItem(0), makeMediaItem(1), makeMediaItem(2)],
  }

  it('collapsed multi-photo card shows only the cover thumbnail and count badge, no MEDIA section', () => {
    const markup = renderPostItem(multiPhotoPost)

    expect(countOccurrences(markup, '<img')).toBe(1)
    expect(markup).toContain('Photos (3)')
    expect(markup).not.toContain('media-section')
  })

  it('multi-photo post shows all media thumbnails in the details panel', () => {
    const markup = renderPostItem(multiPhotoPost, baseDetails)

    expect(markup).toContain('media-section')
    expect(countOccurrences(markup, 'media-section-tile')).toBe(3)
    expect(markup).toContain('alt="Post media 1"')
    expect(markup).toContain('alt="Post media 2"')
    expect(markup).toContain('alt="Post media 3"')
    expect(markup).toContain('Photo 1')
    expect(markup).toContain('Photo 2')
    expect(markup).toContain('Photo 3')
    // 1 collapsed cover + 3 expanded tiles
    expect(countOccurrences(markup, '<img')).toBe(4)
  })

  it('preserves media order by the order field, not array position', () => {
    const markup = renderPostItem({
      ...multiPhotoPost,
      mediaItems: [makeMediaItem(2), makeMediaItem(0), makeMediaItem(1)],
    }, baseDetails)

    const section = markup.slice(markup.indexOf('media-section'))
    const first = section.indexOf('/api/media/item-0/file')
    const second = section.indexOf('/api/media/item-1/file')
    const third = section.indexOf('/api/media/item-2/file')
    expect(first).toBeGreaterThan(-1)
    expect(second).toBeGreaterThan(first)
    expect(third).toBeGreaterThan(second)
  })

  it('single-photo post shows one media tile labeled Photo', () => {
    const markup = renderPostItem({
      ...basePost,
      mediaUrl: '/api/media/single/file',
      mediaType: 'Image',
    }, baseDetails)

    expect(markup).toContain('media-section')
    expect(countOccurrences(markup, 'media-section-tile')).toBe(1)
    expect(markup).toContain('alt="Post media 1"')
    expect(markup).toContain('media-section-label">Photo</span>')
  })

  it('video post shows a video tile with accessible text', () => {
    const markup = renderPostItem({
      ...basePost,
      mediaUrl: '/api/media/vid/file',
      mediaType: 'Video',
    }, baseDetails)

    const section = markup.slice(markup.indexOf('media-section'))
    expect(section).toContain('media-thumbnail-video-placeholder')
    expect(section).toContain('media-section-label">Video</span>')
    expect(markup).toContain('Video media 1')
    expect(markup).toContain('media-sr-only')
  })

  it('mixed Instagram carousel keeps order and per-type labels', () => {
    const markup = renderPostItem({
      ...basePost,
      platform: 'Instagram',
      mediaUrl: '/api/media/item-0/file',
      mediaType: 'Image',
      mediaItems: [makeMediaItem(0), makeMediaItem(1, 'Video'), makeMediaItem(2)],
    }, baseDetails)

    expect(countOccurrences(markup, 'media-section-tile')).toBe(3)
    expect(markup).toContain('alt="Post media 1"')
    expect(markup).toContain('Video media 2')
    expect(markup).toContain('alt="Post media 3"')
    expect(markup).toContain('Photo 1')
    expect(markup).toContain('Photo 2')
  })

  it('text-only post has no MEDIA section', () => {
    const markup = renderPostItem(basePost, baseDetails)

    expect(markup).not.toContain('media-section')
  })

  it('keeps the Open post link for published posts alongside the MEDIA section', () => {
    const markup = renderPostItem(multiPhotoPost, {
      ...baseDetails,
      externalPostUrl: 'https://facebook.com/12345',
    })

    expect(markup).toContain('Open post')
    expect(markup).toContain('https://facebook.com/12345')
    expect(markup).toContain('media-section')
  })

  it('details panel starts collapsed (not visible) even with cached details', () => {
    const markup = renderPostItem(multiPhotoPost, baseDetails)

    expect(markup).toContain('post-details-panel')
    expect(markup).not.toContain('post-details-panel visible')
    expect(markup).not.toContain('rotated')
  })
})
