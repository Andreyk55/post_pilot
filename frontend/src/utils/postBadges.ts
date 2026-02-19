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

/**
 * Returns a short media label for thumbnail overlays (e.g. "Video", "Photo", "Photos (2)").
 */
export function getMediaLabel(post: Post): string {
  if (post.mediaItems && post.mediaItems.length >= 2) {
    if (post.platform === 'Facebook') return `Photos (${post.mediaItems.length})`
    return 'Carousel'
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

/**
 * Returns an array of content-type badges for a post.
 * Used by both MyPosts (PostItem) and SchedulePosts (ScheduledPosts) lists.
 */
export function getContentBadges(post: Post): ContentBadge[] {
  const badges: ContentBadge[] = []

  // Story badge
  if (post.postType === 'Story') {
    badges.push({ key: 'story', text: 'Story', dataType: 'story' })

    // Story media badge (video or image)
    const media = getEffectiveMediaType(post)
    if (media === 'Video') {
      badges.push({ key: 'media', text: 'Video', dataType: 'video' })
    } else {
      badges.push({ key: 'media', text: 'Photo', dataType: 'image' })
    }
    return badges
  }

  // Feed post badges
  const mediaCount = post.mediaItems?.length ?? 0
  const hasMainMedia = getEffectiveMediaType(post) !== 'None'

  // Multi-image (carousel / photos)
  if (mediaCount >= 2) {
    if (post.platform === 'Facebook') {
      badges.push({ key: 'media', text: `Photos (${mediaCount})`, dataType: 'carouselalbum' })
    } else {
      badges.push({ key: 'media', text: 'Carousel', dataType: 'carouselalbum' })
    }
    return badges
  }

  // Single media or Instagram-specific type
  if (post.platform === 'Instagram' && post.instagramMediaType) {
    switch (post.instagramMediaType) {
      case 'Reels':
        badges.push({ key: 'media', text: 'Reel', dataType: 'reels' })
        return badges
      case 'CarouselAlbum':
        badges.push({ key: 'media', text: 'Carousel', dataType: 'carouselalbum' })
        return badges
      case 'Video':
        badges.push({ key: 'media', text: 'Video', dataType: 'video' })
        return badges
      case 'Image':
        badges.push({ key: 'media', text: 'Photo', dataType: 'image' })
        return badges
    }
  }

  // Single image or video (any platform)
  if (mediaCount === 1 || hasMainMedia) {
    const media = getEffectiveMediaType(post)
    if (media === 'Video') {
      badges.push({ key: 'media', text: 'Video', dataType: 'video' })
    } else if (media === 'Image') {
      badges.push({ key: 'media', text: 'Photo', dataType: 'image' })
    }
    return badges
  }

  // Text-only (no media)
  badges.push({ key: 'media', text: 'Text', dataType: 'text' })
  return badges
}
