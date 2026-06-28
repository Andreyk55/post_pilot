import { readFileSync } from 'node:fs'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { Post } from '../api/posts'
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
      mediaUrl: 'users/u/workspaces/w/providers/meta-facebook/media/m/clip.mp4',
      mediaType: 'Video',
      thumbnail: {
        storageKey: 'users/u/workspaces/w/providers/meta-facebook/media/m/thumbnail.jpg',
        url: null,
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
    expect(markup).toContain('/api/media/files/users/u/workspaces/w/providers/meta-facebook/media/m/thumbnail.jpg')
  })

  it('renders the scheduled video fallback with a centered play icon when no thumbnail exists', () => {
    const markup = renderScheduledPosts({
      ...basePost,
      mediaUrl: 'users/u/workspaces/w/providers/meta-facebook/media/m/clip.mp4',
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
      mediaUrl: 'users/u/workspaces/w/providers/meta-facebook/media/m/photo.jpg',
      mediaType: 'Image',
    })

    expect(markup).toContain('<img')
    expect(markup).toContain('media-thumbnail media-thumbnail--scheduled-card')
    expect(markup).toContain('/api/media/files/users/u/workspaces/w/providers/meta-facebook/media/m/photo.jpg')
    expect(markup).not.toContain('media-indicator')
    expect(markup).not.toContain('media-thumbnail-video-overlay')
    expect(markup).not.toContain('media-thumbnail-video-placeholder')
  })

  it('keeps the scheduled metadata media badges outside the preview tile', () => {
    const markup = renderScheduledPosts({
      ...basePost,
      mediaUrl: 'users/u/workspaces/w/providers/meta-facebook/media/m/clip.mp4',
      mediaType: 'Video',
    })

    expect(markup).toContain('class="media-type-badge" data-type="post">Post</span>')
    expect(markup).toContain('class="media-type-badge" data-type="video">Video</span>')
    expect(markup).not.toContain('media-indicator')
  })
})
