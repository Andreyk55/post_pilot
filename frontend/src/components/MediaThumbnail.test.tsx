import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { MediaThumbnail } from './MediaThumbnail'

describe('MediaThumbnail', () => {
  it('renders a video thumbnail image with a play overlay when thumbnail data exists', () => {
    const markup = renderToStaticMarkup(
      <MediaThumbnail
        storageKey="users/u/workspaces/w/providers/meta-facebook/media/m/clip.mp4"
        mediaType="Video"
        thumbnailStorageKey="users/u/workspaces/w/providers/meta-facebook/media/m/thumbnail.jpg"
        className="media-thumbnail"
      />,
    )

    expect(markup).toContain('<img')
    expect(markup).toContain('media-thumbnail-video-overlay')
    expect(markup).toContain('/api/media/files/users/u/workspaces/w/providers/meta-facebook/media/m/thumbnail.jpg')
  })

  it('renders the generic video placeholder when no thumbnail exists', () => {
    const markup = renderToStaticMarkup(
      <MediaThumbnail
        storageKey="users/u/workspaces/w/providers/meta-facebook/media/m/clip.mp4"
        mediaType="Video"
        className="media-thumbnail"
      />,
    )

    expect(markup).toContain('media-thumbnail-video-placeholder')
    expect(markup).not.toContain('media-thumbnail-video-overlay')
  })

  it('keeps image rendering unchanged', () => {
    const markup = renderToStaticMarkup(
      <MediaThumbnail
        storageKey="users/u/workspaces/w/providers/meta-facebook/media/m/photo.jpg"
        mediaType="Image"
        className="media-thumbnail"
        alt="Preview"
      />,
    )

    expect(markup).toContain('<img')
    expect(markup).toContain('alt="Preview"')
    expect(markup).not.toContain('media-thumbnail-video-overlay')
    expect(markup).not.toContain('media-thumbnail-video-placeholder')
  })

  it('renders an icon-only image placeholder for scheduled cards when no preview exists', () => {
    const markup = renderToStaticMarkup(
      <MediaThumbnail
        storageKey={null}
        mediaType="Image"
        className="media-thumbnail"
        variant="scheduledCard"
      />,
    )

    expect(markup).toContain('media-thumbnail-image-placeholder')
    expect(markup).toContain('media-thumbnail--scheduled-card')
    expect(markup).toContain('media-image-icon')
  })

  it('keeps missing default image previews hidden', () => {
    const markup = renderToStaticMarkup(
      <MediaThumbnail
        storageKey={null}
        mediaType="Image"
      />,
    )

    expect(markup).toBe('')
  })
})
