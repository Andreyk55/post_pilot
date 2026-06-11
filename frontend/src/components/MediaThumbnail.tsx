import { useState } from 'react'
import { getMediaUrl } from '../api/media'

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
 * `thumbnailUrl` (e.g. a user-selected video thumbnail) is an already-resolved
 * absolute/relative URL and is rendered directly as an image when provided for a
 * video, falling back to the play-icon placeholder if it errors.
 */
interface MediaThumbnailProps {
  /** Raw storage key (or external URL) for the media; resolved via getMediaUrl. */
  storageKey: string | null | undefined
  mediaType: 'None' | 'Image' | 'Video'
  className?: string
  /** Alt text for genuine image previews. Never shown for videos or on error. */
  alt?: string
  /** Pre-resolved thumbnail URL (e.g. selectedThumbnailUrl) for videos. */
  thumbnailUrl?: string | null
}

export function MediaThumbnail({
  storageKey,
  mediaType,
  className,
  alt = '',
  thumbnailUrl,
}: MediaThumbnailProps) {
  const [imageBroken, setImageBroken] = useState(false)

  if (mediaType === 'Image') {
    const src = getMediaUrl(storageKey)
    if (!src || imageBroken) return null
    return (
      <img
        src={src}
        alt={alt}
        className={className}
        onError={() => setImageBroken(true)}
      />
    )
  }

  if (mediaType === 'Video') {
    // A user-picked thumbnail is a real image; try it, fall back to the icon.
    if (thumbnailUrl && !imageBroken) {
      return (
        <img
          src={thumbnailUrl}
          alt=""
          className={className}
          onError={() => setImageBroken(true)}
        />
      )
    }
    // No usable thumbnail (or it errored): clean static placeholder. We do NOT
    // fetch the video file just to draw a frame.
    return <VideoPlaceholder className={className} />
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
