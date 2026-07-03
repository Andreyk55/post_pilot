import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { Post } from '../api/posts'
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

function renderPostItem(post: Post) {
  return renderToStaticMarkup(
    <PostItem
      post={post}
      cachedDetails={undefined}
      onDetailsFetched={() => {}}
    />,
  )
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
