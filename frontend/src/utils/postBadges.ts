import type { Post } from '../api/posts'
import { getMediaTypeFromFile } from '../api/media'

export interface ContentBadge {
  key: string
  text: string
  dataType: string
}

function getEffectiveMediaType(post: Post): 'None' | 'Image' | 'Video' {
  if (post.mediaType && post.mediaType !== 'None') {
    return post.mediaType
  }
  if (post.mediaUrl) {
    return getMediaTypeFromFile(post.mediaUrl)
  }
  return 'None'
}

// ─── Media counts helper ─────────────────────────────────────────

/**
 * Counts photos and videos in a post's mediaItems.
 */
function countMedia(post: Post): { photos: number; videos: number } {
  const items = post.mediaItems ?? []
  return {
    photos: items.filter(i => i.mediaType === 'Image').length,
    videos: items.filter(i => i.mediaType === 'Video').length,
  }
}

/**
 * Formats photo/video counts for multi-media labels.
 * Examples: "5p", "1v", "2p+3v"
 * Never shows "0p" or "0v".
 */
export function formatMediaCounts(photoCount: number, videoCount: number): string {
  const parts: string[] = []
  if (photoCount > 0) parts.push(`${photoCount}p`)
  if (videoCount > 0) parts.push(`${videoCount}v`)
  return parts.join('+')
}

// ─── Post Type chip ──────────────────────────────────────────────

/**
 * Post Type chip (always present):
 *   Story    – postType === 'Story'
 *   Reel     – instagramMediaType === 'Reels'
 *   Carousel – 2+ media items (multi-image/video/mixed)
 *   Post     – everything else (single feed post)
 */
export function getPostTypeBadge(post: Post): ContentBadge {
  if (post.postType === 'Story') {
    return { key: 'postType', text: 'Story', dataType: 'story' }
  }
  if (post.instagramMediaType === 'Reels') {
    return { key: 'postType', text: 'Reel', dataType: 'reel' }
  }
  if ((post.mediaItems?.length ?? 0) >= 2 || post.instagramMediaType === 'CarouselAlbum') {
    return { key: 'postType', text: 'Carousel', dataType: 'carousel' }
  }
  return { key: 'postType', text: 'Post', dataType: 'post' }
}

// ─── Media Summary chip ──────────────────────────────────────────

/**
 * Media Summary chip (always present):
 *   Text                              – no media
 *   Photo                             – 1 image
 *   Video                             – 1 video
 *   Photos (n)                        – n images, no videos
 *   Videos (n)                        – n videos, no images
 *   photos (x) + videos (y)          – mixed (shown inside "Carousel:" wrapper by caller if needed)
 */
export function getMediaSummaryBadge(post: Post): ContentBadge {
  const items = post.mediaItems ?? []
  const hasMainMedia = getEffectiveMediaType(post) !== 'None'

  // Multi-item posts
  if (items.length >= 2) {
    const { photos, videos } = countMedia(post)

    if (videos === 0) {
      return { key: 'media', text: `Photos (${photos})`, dataType: 'image' }
    }
    if (photos === 0) {
      return { key: 'media', text: `Videos (${videos})`, dataType: 'video' }
    }
    // Mixed
    return {
      key: 'media',
      text: `photos (${photos}) + videos (${videos})`,
      dataType: 'carouselalbum',
    }
  }

  // Single media item or main media field
  if (items.length === 1 || hasMainMedia) {
    const media = items.length === 1 ? items[0].mediaType : getEffectiveMediaType(post)
    if (media === 'Video') {
      return { key: 'media', text: 'Video', dataType: 'video' }
    }
    return { key: 'media', text: 'Photo', dataType: 'image' }
  }

  // No media
  return { key: 'media', text: 'Text', dataType: 'text' }
}

/**
 * Returns [POST_TYPE, MEDIA_SUMMARY] badges in order.
 * Used by both PostItem and ScheduledPosts for the chip row.
 */
export function getContentBadges(post: Post): ContentBadge[] {
  return [getPostTypeBadge(post), getMediaSummaryBadge(post)]
}

// ─── Thumbnail overlay label ─────────────────────────────────────

/**
 * Returns a short media label for thumbnail overlays.
 * Examples: "Reel", "Photo", "Photos (3p)", "Carousel (2p+3v)"
 */
export function getMediaLabel(post: Post): string {
  if (post.mediaItems && post.mediaItems.length >= 2) {
    const { photos, videos } = countMedia(post)
    const counts = formatMediaCounts(photos, videos)
    if (videos === 0) return `Photos (${counts})`
    if (photos === 0) return `Videos (${counts})`
    return `Carousel (${counts})`
  }
  if (post.platform === 'Instagram' && post.instagramMediaType) {
    switch (post.instagramMediaType) {
      case 'Reels': return 'Reel'
      case 'Image': return 'Photo'
      case 'CarouselAlbum': return 'Carousel'
      case 'Video': return 'Video'
    }
  }
  return getEffectiveMediaType(post) === 'Video' ? 'Video' : 'Photo'
}
