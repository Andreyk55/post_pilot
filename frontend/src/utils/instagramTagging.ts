import type { MediaType } from '../api/media'
import type { InstagramUserTag } from '../api/posts'

/**
 * Determines if Instagram media tags should be shown for the current post configuration.
 *
 * Tags are supported for:
 * - Instagram platform only
 * - Feed posts (not Stories)
 * - Single image or single video (not carousel with 2+ items)
 * - Media must be uploaded (mediaUrl present)
 */
export function canShowInstagramTags(
  isInstagram: boolean,
  isStory: boolean,
  mediaType: MediaType | null,
  isMultiMedia: boolean,
  hasMedia: boolean,
): boolean {
  const isTaggableMedia = mediaType === 'Image' || mediaType === 'Video'
  return isInstagram && !isStory && isTaggableMedia && !isMultiMedia && hasMedia
}

/**
 * Determines if carousel per-image tags should be shown.
 * Tags are supported for Instagram Feed carousel posts (2+ items).
 */
export function canShowCarouselTags(
  isInstagram: boolean,
  isStory: boolean,
  isMultiMedia: boolean,
): boolean {
  return isInstagram && !isStory && isMultiMedia
}

export interface MediaTag {
  username: string
  x?: number
  y?: number
}

export interface PlacedTag {
  username: string
  x: number
  y: number
}

/**
 * Builds the placed tags payload for submission.
 * - For video: auto-places all tags at center (0.5, 0.5) since there's no image to click on.
 * - For image: only includes tags that have been manually placed (x, y defined).
 */
export function buildPlacedTags(
  mediaTags: MediaTag[],
  isVideo: boolean,
): PlacedTag[] {
  if (mediaTags.length === 0) return []

  if (isVideo) {
    return mediaTags.map(t => ({
      username: t.username,
      x: t.x ?? 0.5,
      y: t.y ?? 0.5,
    }))
  }

  return mediaTags
    .filter(t => t.x !== undefined && t.y !== undefined)
    .map(t => ({ username: t.username, x: t.x!, y: t.y! }))
}

/**
 * Builds the per-media-item tags payload for carousel submission.
 * Takes the full carousel tags map and returns a Record<number, InstagramUserTag[]>
 * suitable for the API, only including items that have tags with valid placements.
 * For video items, tags are auto-placed at center (0.5, 0.5).
 */
export function buildCarouselMediaTags(
  carouselTags: Map<number, MediaTag[]>,
  mediaTypes: Map<number, MediaType>,
): Record<number, InstagramUserTag[]> | undefined {
  const result: Record<number, InstagramUserTag[]> = {}
  let hasAnyTags = false

  for (const [order, tags] of carouselTags) {
    if (tags.length === 0) continue
    const isVideo = mediaTypes.get(order) === 'Video'
    const placed = buildPlacedTags(tags, isVideo)
    if (placed.length > 0) {
      result[order] = placed
      hasAnyTags = true
    }
  }

  return hasAnyTags ? result : undefined
}
