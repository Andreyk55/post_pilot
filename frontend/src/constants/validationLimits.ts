/**
 * Platform-specific post text character limits.
 * These limits match the backend ValidationLimits.cs - each side maintains its own copy.
 */

export const PostTextMaxLengthFacebook = 5000
export const PostTextMaxLengthInstagram = 2200
export const PostTextMaxLengthLinkedIn = 3000
export const PostTextMaxLengthX = 280

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
