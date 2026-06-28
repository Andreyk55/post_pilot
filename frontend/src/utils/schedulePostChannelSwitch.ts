import type { PostType } from '../api/posts'

/**
 * Channel-switch rules for the Schedule Post composer.
 *
 * The composer draft (caption, media, schedule, AI results, …) is authored for a
 * single Meta channel. When the user moves the Meta Channel selection between
 * Facebook and Instagram, the draft belongs to the *old* channel and must be
 * cleared so media/text meant for one channel can't be published to the other.
 *
 * These are pure helpers so the switch decision and the "is anything worth
 * confirming?" check can be unit-tested without a DOM harness. The actual reset +
 * in-flight upload/validation guard lives in SchedulePost (it remounts the upload
 * components, invalidating their ownership tokens — see MediaUpload /
 * MultiMediaUpload).
 */

export const META_CHANNELS = ['facebook', 'instagram'] as const

export type MetaChannel = (typeof META_CHANNELS)[number]

function isMetaChannel(platformId: string): platformId is MetaChannel {
  return (META_CHANNELS as readonly string[]).includes(platformId)
}

/**
 * True only when selecting `nextPlatformId` changes the active Meta channel from
 * the other one (Facebook→Instagram or Instagram→Facebook). This is the single
 * transition that must reset the draft.
 *
 * Returns false for:
 *  - first selection (no Meta channel selected yet) — nothing to clear,
 *  - re-clicking the already-selected channel (that is a deselect, not a switch),
 *  - non-Meta platform ids.
 */
export function isMetaChannelSwitch(currentPlatforms: string[], nextPlatformId: string): boolean {
  if (!isMetaChannel(nextPlatformId)) return false
  // Re-clicking the active channel deselects it; it is not a cross-channel switch.
  if (currentPlatforms.includes(nextPlatformId)) return false
  // A switch requires the *other* Meta channel to be the current selection.
  return currentPlatforms.some(p => isMetaChannel(p) && p !== nextPlatformId)
}

export function isPostTypeSwitch(currentPostType: PostType, nextPostType: PostType): boolean {
  return currentPostType !== nextPostType
}

/**
 * Snapshot of the draft fields that represent user-authored work. Channel-specific
 * selections (target Page/IG account) are intentionally excluded: they are trivial
 * to re-pick and get cleared by the switch regardless, so they should not trigger a
 * "you'll lose your draft" confirmation on their own.
 */
export interface ComposerDraftSnapshot {
  content: string
  mediaUrl: string | null
  carouselItemCount: number
  mediaTagCount: number
  scheduledDate: string
  scheduledTime: string
  postType: PostType
  selectedThumbnailUrl: string | null
  hasUploadError: boolean
  hasSingleMediaValidationState: boolean
  carouselValidationIssueCount: number
}

interface ComposerDraftDirtyOptions {
  includePostType?: boolean
}

/**
 * True when the composer holds work the user would lose on a channel switch. Used
 * to decide whether to prompt for confirmation before clearing the draft.
 */
export function isComposerDraftDirty(
  draft: ComposerDraftSnapshot,
  options: ComposerDraftDirtyOptions = {},
): boolean {
  const includePostType = options.includePostType ?? true

  return (
    draft.content.length > 0 ||
    draft.mediaUrl !== null ||
    draft.carouselItemCount > 0 ||
    draft.mediaTagCount > 0 ||
    draft.scheduledDate.length > 0 ||
    draft.scheduledTime.length > 0 ||
    (includePostType && draft.postType !== 'Feed') ||
    draft.selectedThumbnailUrl !== null ||
    draft.hasUploadError ||
    draft.hasSingleMediaValidationState ||
    draft.carouselValidationIssueCount > 0
  )
}
