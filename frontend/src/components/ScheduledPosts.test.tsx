import { readFileSync } from 'node:fs'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { Post, PostMediaItem } from '../api/posts'
import type { MediaType } from '../api/media'
import { ScheduledPosts } from './ScheduledPosts'

const scheduledPostsCss = readFileSync(new URL('./ScheduledPosts.css', import.meta.url), 'utf-8')

const basePost: Post = {
  id: 'post-1',
  content: 'Scheduled content',
  mediaUrl: null,
  mediaType: 'None',
  postType: 'Feed',
  platform: 'Facebook',
  scheduledAt: '2026-06-28T12:00:00.000Z',
  status: 'Scheduled',
  createdAt: '2026-06-28T10:00:00.000Z',
  updatedAt: '2026-06-28T10:00:00.000Z',
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
  processingPollCount: 0,
  nextRetryAt: null,
  selectedThumbnailUrl: null,
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

function renderScheduledPosts(post: Post) {
  return renderToStaticMarkup(
    <ScheduledPosts
      posts={[post]}
      onCancel={async () => {}}
      onDelete={async () => {}}
      onLoadMore={() => {}}
      hasMore={false}
      isLoading={false}
      totalCount={1}
    />,
  )
}

describe('ScheduledPosts media previews', () => {
  it('styles scheduled media previews as compact square tiles', () => {
    const previewRule = scheduledPostsCss.match(/\.post-media-preview\s*\{[\s\S]*?\}/)?.[0] ?? ''

    expect(previewRule).toContain('width: 52px')
    expect(previewRule).toContain('height: 52px')
    expect(previewRule).toContain('padding: 0')
    expect(previewRule).toContain('border: 0')
    expect(previewRule).not.toContain('width: fit-content')
    expect(previewRule).not.toContain('border: 1px solid')
  })

  it('renders a scheduled video thumbnail with a centered play overlay', () => {
    const markup = renderScheduledPosts({
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
    expect(markup).toContain('media-thumbnail--scheduled-card')
    expect(markup).toContain('media-thumbnail-video-overlay')
    expect(markup).toContain('video-play-icon')
    expect(markup).not.toContain('media-indicator')
    expect(markup).toContain('/api/media/11111111-1111-1111-1111-111111111111/file?variant=thumbnail')
  })

  it('renders the scheduled video fallback with a centered play icon when no thumbnail exists', () => {
    const markup = renderScheduledPosts({
      ...basePost,
      mediaUrl: '/api/media/11111111-1111-1111-1111-111111111111/file',
      mediaId: '11111111-1111-1111-1111-111111111111',
      mediaType: 'Video',
    })

    expect(markup).toContain('media-thumbnail-video-placeholder')
    expect(markup).toContain('media-thumbnail--scheduled-card')
    expect(markup).toContain('video-play-icon')
    expect(markup).not.toContain('media-indicator')
    expect(markup).not.toContain('media-thumbnail-video-overlay')
  })

  it('keeps scheduled image previews on the shared scheduled-card thumbnail variant', () => {
    const markup = renderScheduledPosts({
      ...basePost,
      mediaUrl: '/api/media/11111111-1111-1111-1111-111111111111/file',
      mediaId: '11111111-1111-1111-1111-111111111111',
      mediaType: 'Image',
    })

    expect(markup).toContain('<img')
    expect(markup).toContain('media-thumbnail media-thumbnail--scheduled-card')
    expect(markup).toContain('/api/media/11111111-1111-1111-1111-111111111111/file')
    expect(markup).not.toContain('media-indicator')
    expect(markup).not.toContain('media-thumbnail-video-overlay')
    expect(markup).not.toContain('media-thumbnail-video-placeholder')
  })

  it('keeps the scheduled metadata media badges outside the preview tile', () => {
    const markup = renderScheduledPosts({
      ...basePost,
      mediaUrl: '/api/media/11111111-1111-1111-1111-111111111111/file',
      mediaId: '11111111-1111-1111-1111-111111111111',
      mediaType: 'Video',
    })

    expect(markup).toContain('class="media-type-badge" data-type="post">Post</span>')
    expect(markup).toContain('class="media-type-badge" data-type="video">Video</span>')
    expect(markup).not.toContain('media-indicator')
  })
})

describe('ScheduledPosts "+N more" carousel overlay', () => {
  const multiPhotoPost = (count: number): Post => ({
    ...basePost,
    mediaUrl: '/api/media/item-0/file',
    mediaType: 'Image',
    mediaItems: Array.from({ length: count }, (_, i) => makeMediaItem(i)),
  })

  it('shows "+2 more" for a 3-image post, keeping the cover thumbnail and badge', () => {
    const markup = renderScheduledPosts(multiPhotoPost(3))

    expect(markup).toContain('class="more-media-overlay" aria-hidden="true">+2 more</span>')
    // Still one compact cover thumbnail, not all images
    expect(markup.split('<img').length - 1).toBe(1)
    expect(markup).toContain('/api/media/item-0/file')
    expect(markup).toContain('media-thumbnail media-thumbnail--scheduled-card')
    // Existing badge unchanged
    expect(markup).toContain('Photos (3)')
  })

  it('shows "+1 more" for a 2-image post', () => {
    const markup = renderScheduledPosts(multiPhotoPost(2))

    expect(markup).toContain('+1 more')
    expect(markup).toContain('Photos (2)')
  })

  it('does not show the overlay for a single-image post', () => {
    const markup = renderScheduledPosts({
      ...basePost,
      mediaUrl: '/api/media/single/file',
      mediaType: 'Image',
    })

    expect(markup).toContain('<img')
    expect(markup).not.toContain('more-media-overlay')
  })

  it('does not show the overlay for a video post', () => {
    const markup = renderScheduledPosts({
      ...basePost,
      mediaUrl: '/api/media/vid/file',
      mediaType: 'Video',
    })

    expect(markup).not.toContain('more-media-overlay')
  })

  it('does not show the overlay for a text-only post', () => {
    const markup = renderScheduledPosts(basePost)

    expect(markup).not.toContain('more-media-overlay')
  })

  it('does not show the overlay for mixed image + video posts', () => {
    const markup = renderScheduledPosts({
      ...basePost,
      mediaUrl: '/api/media/item-0/file',
      mediaType: 'Image',
      mediaItems: [makeMediaItem(0), makeMediaItem(1), makeMediaItem(2, 'Video')],
    })

    expect(markup).not.toContain('more-media-overlay')
  })

  it('styles the overlay as a non-interactive layer inside the tile', () => {
    const overlayRule = scheduledPostsCss.match(/\.more-media-overlay\s*\{[\s\S]*?\}/)?.[0] ?? ''

    expect(overlayRule).toContain('position: absolute')
    expect(overlayRule).toContain('pointer-events: none')
    expect(scheduledPostsCss).toMatch(/\.post-media-preview\.carousel-preview\s*\{[^}]*position: relative/)
  })
})
