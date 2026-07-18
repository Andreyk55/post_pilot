/**
 * Platform-specific post text character limits.
 * These limits match the backend ValidationLimits.cs - each side maintains its own copy.
 */

export const PostTextMaxLengthFacebook = 5000
export const PostTextMaxLengthInstagram = 2200
export const PostTextMaxLengthLinkedIn = 3000
export const PostTextMaxLengthX = 280

/**
 * Instagram Feed caption entity limits (hashtags/@mentions written IN the caption text).
 * Mirrors the backend ValidationLimits.cs. Both are inclusive: exactly the value is accepted,
 * one over is rejected. These count entities inside the caption and are SEPARATE from Instagram
 * media tags (usernames attached to the image/video), which have their own validation.
 */
export const InstagramFeedMaxHashtags = 30
export const InstagramFeedMaxMentions = 20

/** Blocking message when an Instagram Feed caption carries too many hashtags. */
export const InstagramFeedTooManyHashtagsMessage =
  `Instagram Feed captions can contain at most ${InstagramFeedMaxHashtags} hashtags.`

/** Blocking message when an Instagram Feed caption carries too many @mentions. */
export const InstagramFeedTooManyMentionsMessage =
  `Instagram Feed captions can contain at most ${InstagramFeedMaxMentions} @mentions.`

/** Platform identifiers as used in the UI */
export type PlatformId = 'facebook' | 'instagram' | 'linkedin' | 'twitter'

/** Map of platform IDs to their maximum post text character limits */
export const PostTextMaxCharsByPlatform: Record<PlatformId, number> = {
  facebook: PostTextMaxLengthFacebook,
  instagram: PostTextMaxLengthInstagram,
  linkedin: PostTextMaxLengthLinkedIn,
  twitter: PostTextMaxLengthX,
} as const

/**
 * Gets the maximum post text length for a given platform.
 * @param platformId - The platform identifier (e.g., 'facebook', 'twitter')
 * @returns The maximum character count, or 5000 as fallback
 */
export function getPostTextMaxChars(platformId: PlatformId | string | null): number {
  if (!platformId) return 5000
  return PostTextMaxCharsByPlatform[platformId as PlatformId] ?? 5000
}

/**
 * Gets the platform name for display in error messages.
 */
export function getPlatformDisplayName(platformId: PlatformId | string): string {
  const names: Record<string, string> = {
    facebook: 'Facebook',
    instagram: 'Instagram',
    linkedin: 'LinkedIn',
    twitter: 'X',
  }
  return names[platformId] ?? platformId
}
