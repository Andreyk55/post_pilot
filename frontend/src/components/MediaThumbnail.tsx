import { useEffect, useState } from 'react'

/**
 * Fail-safe media preview thumbnail for post lists (My Posts / Scheduled).
 *
 * Rules (intentionally conservative — a broken preview must never show alt text
 * or a torn-image glyph):
 *  - Image: render <img> from the resolved media URL. If it fails to load
 *    (404 / expired / blocked), hide it entirely — no visible alt text.
 *  - Video: NEVER load the video file into an <img>. Show a clean, static
 *    play-icon placeholder. (Real frame extraction is deliberately out of scope.)
 *  - Missing key / unknown type: render nothing.
 *
 * `thumbnailUrl` (e.g. a user-selected or backend-generated video thumbnail) is an
 * already-resolved absolute/relative URL and is rendered directly as an image when
 * provided for a video.
 */
interface MediaThumbnailProps {
  /** Already-resolved media preview URL. */
  src: string | null | undefined
  mediaType: 'None' | 'Image' | 'Video'
  className?: string
  variant?: 'default' | 'scheduledCard'
  /** Alt text for genuine image previews. Never shown for videos or on error. */
  alt?: string
  /** Pre-resolved thumbnail URL (e.g. selectedThumbnailUrl) for videos. */
  thumbnailUrl?: string | null
}

export function MediaThumbnail({
  src,
  mediaType,
  className,
  variant = 'default',
  alt = '',
  thumbnailUrl,
}: MediaThumbnailProps) {
  const [imageBroken, setImageBroken] = useState(false)
  const classes = [
    className,
    variant === 'scheduledCard' ? 'media-thumbnail--scheduled-card' : null,
  ].filter(Boolean).join(' ')

  useEffect(() => {
    setImageBroken(false)
  }, [thumbnailUrl, src, mediaType])

  if (mediaType === 'Image') {
    if (!src || imageBroken) {
      return variant === 'scheduledCard' ? <ImagePlaceholder className={classes || undefined} /> : null
    }
    return (
      <img
        src={src}
        alt={alt}
        className={classes || undefined}
        onError={() => setImageBroken(true)}
      />
    )
  }

  if (mediaType === 'Video') {
    if (thumbnailUrl && !imageBroken) {
      return (
        <div className={['media-thumbnail-video-frame', classes].filter(Boolean).join(' ')}>
          <img
            src={thumbnailUrl}
            alt=""
            onError={() => setImageBroken(true)}
          />
          <div className="media-thumbnail-video-overlay" aria-hidden="true">
            <svg className="video-play-icon" viewBox="0 0 24 24" fill="currentColor">
              <path d="M8 5v14l11-7z" />
            </svg>
          </div>
        </div>
      )
    }

    return <VideoPlaceholder className={classes || undefined} />
  }

  return null
}

/** Static, dependency-free video placeholder: a centered play glyph. */
function VideoPlaceholder({ className }: { className?: string }) {
  return (
    <div
      className={`media-thumbnail-video-placeholder ${className ?? ''}`.trim()}
      aria-hidden="true"
    >
      <svg className="video-play-icon" viewBox="0 0 24 24" fill="currentColor">
        <path d="M8 5v14l11-7z" />
      </svg>
    </div>
  )
}

function ImagePlaceholder({ className }: { className?: string }) {
  return (
    <div
      className={`media-thumbnail-image-placeholder ${className ?? ''}`.trim()}
      aria-hidden="true"
    >
      <svg className="media-image-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <rect x="3" y="5" width="18" height="14" rx="2" />
        <circle cx="8.5" cy="10" r="1.5" />
        <path d="M21 15l-5-5L5 19" />
      </svg>
    </div>
  )
}
