import { useState, useMemo, useCallback } from 'react'
import { parseInstagramUsername, extractMentionsFromCaption } from '../utils/instagramMention'
import { getMediaUrl } from '../api/media'
import './InstagramMediaTags.css'

export interface MediaTag {
  username: string
  x?: number
  y?: number
}

interface InstagramMediaTagsProps {
  caption: string
  mediaTags: MediaTag[]
  onMediaTagsChange: (tags: MediaTag[]) => void
  /** S3 key of the selected image */
  mediaS3Key: string | null
  disabled?: boolean
}

export function InstagramMediaTags({
  caption,
  mediaTags,
  onMediaTagsChange,
  mediaS3Key,
  disabled,
}: InstagramMediaTagsProps) {
  const [input, setInput] = useState('')
  const [inputError, setInputError] = useState<string | null>(null)
  const [selectedTag, setSelectedTag] = useState<string | null>(null)

  // Derive caption mentions
  const captionMentions = useMemo(() => extractMentionsFromCaption(caption), [caption])

  // Filter out mentions already in mediaTags
  const suggestedMentions = useMemo(() => {
    const taggedLower = new Set(mediaTags.map(t => t.username.toLowerCase()))
    return captionMentions.filter(m => !taggedLower.has(m.toLowerCase()))
  }, [captionMentions, mediaTags])

  const imageUrl = useMemo(() => getMediaUrl(mediaS3Key), [mediaS3Key])

  const addTag = useCallback((username: string) => {
    const lower = username.toLowerCase()
    if (mediaTags.some(t => t.username.toLowerCase() === lower)) return
    const updated = [...mediaTags, { username }]
    onMediaTagsChange(updated)
    setSelectedTag(username)
  }, [mediaTags, onMediaTagsChange])

  const removeTag = useCallback((username: string) => {
    const updated = mediaTags.filter(t => t.username.toLowerCase() !== username.toLowerCase())
    onMediaTagsChange(updated)
    if (selectedTag?.toLowerCase() === username.toLowerCase()) {
      setSelectedTag(null)
    }
  }, [mediaTags, onMediaTagsChange, selectedTag])

  const handleAddClick = () => {
    if (!input.trim()) return
    const parsed = parseInstagramUsername(input)
    if (!parsed) {
      setInputError('Invalid Instagram username or URL')
      return
    }
    addTag(parsed)
    setInput('')
    setInputError(null)
  }

  const handleInputKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') {
      e.preventDefault()
      handleAddClick()
    }
  }

  const handleImageClick = (e: React.MouseEvent<HTMLImageElement>) => {
    if (disabled) return
    const rect = e.currentTarget.getBoundingClientRect()
    const x = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width))
    const y = Math.max(0, Math.min(1, (e.clientY - rect.top) / rect.height))

    // Determine which tag to place
    let tagToPlace = selectedTag

    // If no tag selected, pick the first unplaced tag
    if (!tagToPlace) {
      const unplaced = mediaTags.find(t => t.x === undefined || t.y === undefined)
      if (unplaced) {
        tagToPlace = unplaced.username
      } else if (mediaTags.length > 0) {
        // All placed — reposition the last one
        tagToPlace = mediaTags[mediaTags.length - 1].username
      }
    }

    if (!tagToPlace) return

    const updated = mediaTags.map(t =>
      t.username.toLowerCase() === tagToPlace!.toLowerCase()
        ? { ...t, x, y }
        : t
    )
    onMediaTagsChange(updated)
    setSelectedTag(null)
  }

  const isPlaced = (tag: MediaTag) => tag.x !== undefined && tag.y !== undefined

  return (
    <div className="media-tags-section">
      <label className="media-tags-label">Tag people on media (optional)</label>
      <p className="media-tags-helper">
        Tags appear on the photo. Add @username or paste a profile URL, then click the image to place.
      </p>

      {/* Input + Add button */}
      <div className="media-tags-input-row">
        <input
          type="text"
          value={input}
          onChange={e => { setInput(e.target.value); setInputError(null) }}
          onKeyDown={handleInputKeyDown}
          placeholder="Paste Instagram profile URL or @username"
          className={`media-tags-input ${inputError ? 'error' : ''}`}
          disabled={disabled}
        />
        <button
          type="button"
          className="media-tags-add-btn"
          onClick={handleAddClick}
          disabled={disabled || !input.trim()}
        >
          Add tag
        </button>
      </div>
      {inputError && <div className="media-tags-error">{inputError}</div>}

      {/* Suggested from caption */}
      {suggestedMentions.length > 0 && (
        <div className="media-tags-suggestions">
          <span className="media-tags-suggestions-label">From caption:</span>
          {suggestedMentions.map(username => (
            <button
              key={username}
              type="button"
              className="media-tags-chip suggestion"
              onClick={() => addTag(username)}
              disabled={disabled}
            >
              @{username}
            </button>
          ))}
        </div>
      )}

      {/* Selected tags */}
      {mediaTags.length > 0 && (
        <div className="media-tags-list">
          {mediaTags.map(tag => (
            <span
              key={tag.username}
              className={`media-tags-chip tag ${isPlaced(tag) ? 'placed' : 'not-placed'} ${selectedTag?.toLowerCase() === tag.username.toLowerCase() ? 'selected' : ''}`}
              onClick={() => !disabled && setSelectedTag(tag.username)}
            >
              @{tag.username}
              <span className="media-tags-chip-status">
                {isPlaced(tag) ? 'Placed' : 'Not placed'}
              </span>
              <button
                type="button"
                className="media-tags-chip-remove"
                onClick={e => { e.stopPropagation(); removeTag(tag.username) }}
                disabled={disabled}
                aria-label={`Remove @${tag.username}`}
              >
                ×
              </button>
            </span>
          ))}
        </div>
      )}

      {/* Image placement editor */}
      {imageUrl && mediaTags.length > 0 && (
        <div className="media-tags-editor">
          <div className="media-tags-editor-label">
            {selectedTag
              ? `Click the image to place @${selectedTag}`
              : 'Click a tag above, then click the image to place it'}
          </div>
          <div className="media-tags-image-container">
            <img
              src={imageUrl}
              alt="Tag placement"
              className="media-tags-image"
              onClick={handleImageClick}
              draggable={false}
            />
            {/* Markers for placed tags */}
            {mediaTags.filter(isPlaced).map(tag => (
              <div
                key={tag.username}
                className={`media-tags-marker ${selectedTag?.toLowerCase() === tag.username.toLowerCase() ? 'selected' : ''}`}
                style={{ left: `${tag.x! * 100}%`, top: `${tag.y! * 100}%` }}
                onClick={e => { e.stopPropagation(); !disabled && setSelectedTag(tag.username) }}
              >
                <span className="media-tags-marker-dot" />
                <span className="media-tags-marker-label">@{tag.username}</span>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}
