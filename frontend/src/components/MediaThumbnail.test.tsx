import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { MediaThumbnail } from './MediaThumbnail'

describe('MediaThumbnail', () => {
  it('renders a video thumbnail image with a play overlay when thumbnail data exists', () => {
    const markup = renderToStaticMarkup(
      <MediaThumbnail
        src="/api/media/11111111-1111-1111-1111-111111111111/file"
        mediaType="Video"
        thumbnailUrl="/api/media/11111111-1111-1111-1111-111111111111/file?variant=thumbnail"
        className="media-thumbnail"
      />,
    )

    expect(markup).toContain('<img')
    expect(markup).toContain('media-thumbnail-video-overlay')
    expect(markup).toContain('/api/media/11111111-1111-1111-1111-111111111111/file?variant=thumbnail')
  })

  it('renders the generic video placeholder when no thumbnail exists', () => {
    const markup = renderToStaticMarkup(
      <MediaThumbnail
        src="/api/media/11111111-1111-1111-1111-111111111111/file"
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
        src="/api/media/11111111-1111-1111-1111-111111111111/file"
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
        src={null}
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
        src={null}
        mediaType="Image"
      />,
    )

    expect(markup).toBe('')
  })
})
