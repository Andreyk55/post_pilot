import { describe, expect, it } from 'vitest'
import {
  isMetaChannelSwitch,
  isPostTypeSwitch,
  isComposerDraftDirty,
  type ComposerDraftSnapshot,
} from './schedulePostChannelSwitch'

const emptyDraft: ComposerDraftSnapshot = {
  content: '',
  mediaUrl: null,
  carouselItemCount: 0,
  mediaTagCount: 0,
  scheduledDate: '',
  scheduledTime: '',
  postType: 'Feed',
  selectedThumbnailUrl: null,
  hasUploadError: false,
  hasSingleMediaValidationState: false,
  carouselValidationIssueCount: 0,
}

describe('isMetaChannelSwitch', () => {
  it('detects Instagram -> Facebook as a switch', () => {
    expect(isMetaChannelSwitch(['instagram'], 'facebook')).toBe(true)
  })

  it('detects Facebook -> Instagram as a switch', () => {
    expect(isMetaChannelSwitch(['facebook'], 'instagram')).toBe(true)
  })

  it('is not a switch on first selection (nothing selected yet)', () => {
    expect(isMetaChannelSwitch([], 'facebook')).toBe(false)
    expect(isMetaChannelSwitch([], 'instagram')).toBe(false)
  })

  it('is not a switch when re-clicking the already-selected channel (deselect)', () => {
    expect(isMetaChannelSwitch(['facebook'], 'facebook')).toBe(false)
    expect(isMetaChannelSwitch(['instagram'], 'instagram')).toBe(false)
  })

  it('ignores non-Meta platform ids', () => {
    expect(isMetaChannelSwitch(['facebook'], 'twitter')).toBe(false)
    expect(isMetaChannelSwitch(['instagram'], 'linkedin')).toBe(false)
  })
})

describe('isComposerDraftDirty', () => {
  it('is clean for an empty draft', () => {
    expect(isComposerDraftDirty(emptyDraft)).toBe(false)
  })

  it('is dirty when caption/content has text', () => {
    expect(isComposerDraftDirty({ ...emptyDraft, content: 'hello' })).toBe(true)
  })

  it('is dirty when single media is selected', () => {
    expect(isComposerDraftDirty({ ...emptyDraft, mediaUrl: 'media/key.jpg' })).toBe(true)
  })

  it('is dirty when carousel media is selected', () => {
    expect(isComposerDraftDirty({ ...emptyDraft, carouselItemCount: 2 })).toBe(true)
  })

  it('is dirty when media tags are placed', () => {
    expect(isComposerDraftDirty({ ...emptyDraft, mediaTagCount: 1 })).toBe(true)
  })

  it('is dirty when a schedule date or time is set', () => {
    expect(isComposerDraftDirty({ ...emptyDraft, scheduledDate: '2026-06-28' })).toBe(true)
    expect(isComposerDraftDirty({ ...emptyDraft, scheduledTime: '09:00' })).toBe(true)
  })

  it('is dirty when the post type is no longer the default Feed', () => {
    expect(isComposerDraftDirty({ ...emptyDraft, postType: 'Story' })).toBe(true)
  })

  it('can ignore post type when checking an in-platform post type switch', () => {
    expect(isComposerDraftDirty({ ...emptyDraft, postType: 'Story' }, { includePostType: false })).toBe(false)
  })

  it('is dirty when an AI thumbnail has been selected', () => {
    expect(isComposerDraftDirty({ ...emptyDraft, selectedThumbnailUrl: 'thumb/key.jpg' })).toBe(true)
  })

  it('is dirty when upload or validation state is visible', () => {
    expect(isComposerDraftDirty({ ...emptyDraft, hasUploadError: true })).toBe(true)
    expect(isComposerDraftDirty({ ...emptyDraft, hasSingleMediaValidationState: true })).toBe(true)
    expect(isComposerDraftDirty({ ...emptyDraft, carouselValidationIssueCount: 1 })).toBe(true)
  })
})

describe('isPostTypeSwitch', () => {
  it('detects an actual post type change', () => {
    expect(isPostTypeSwitch('Feed', 'Story')).toBe(true)
    expect(isPostTypeSwitch('Story', 'Feed')).toBe(true)
  })

  it('ignores re-selecting the current post type', () => {
    expect(isPostTypeSwitch('Feed', 'Feed')).toBe(false)
    expect(isPostTypeSwitch('Story', 'Story')).toBe(false)
  })
})
