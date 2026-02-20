import { useState } from 'react'
import { parseInstagramUsername, insertMentionAtCursor, captionContainsMention } from '../utils/instagramMention'

interface InstagramMentionProps {
  caption: string
  onCaptionChange: (newCaption: string) => void
  /** Ref to the caption textarea for cursor position */
  textareaRef: React.RefObject<HTMLTextAreaElement | null>
  disabled?: boolean
}

export function InstagramMention({ caption, onCaptionChange, textareaRef, disabled }: InstagramMentionProps) {
  const [mentionInput, setMentionInput] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [toast, setToast] = useState<string | null>(null)

  const handleAddMention = () => {
    setError(null)
    setToast(null)

    const username = parseInstagramUsername(mentionInput)
    if (!username) {
      setError('Invalid Instagram username')
      return
    }

    if (captionContainsMention(caption, username)) {
      setToast('Already in caption')
      setTimeout(() => setToast(null), 2500)
      setMentionInput('')
      return
    }

    // Get cursor position from textarea
    const cursorPos = textareaRef.current?.selectionStart ?? null

    const { newCaption, newCursorPos } = insertMentionAtCursor(caption, username, cursorPos)
    onCaptionChange(newCaption)
    setMentionInput('')

    // Restore focus and set cursor position after React re-render
    requestAnimationFrame(() => {
      const textarea = textareaRef.current
      if (textarea) {
        textarea.focus()
        textarea.setSelectionRange(newCursorPos, newCursorPos)
      }
    })
  }

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') {
      e.preventDefault()
      handleAddMention()
    }
  }

  return (
    <div className="ig-mention-section">
      <div className="ig-mention-hint">
        Tip: Paste a profile link or username to insert an @mention into your caption.
      </div>
      <div className="ig-mention-row">
        <input
          type="text"
          className="ig-mention-input"
          placeholder="Paste Instagram profile URL or @username"
          value={mentionInput}
          onChange={(e) => {
            setMentionInput(e.target.value)
            setError(null)
          }}
          onKeyDown={handleKeyDown}
          disabled={disabled}
        />
        <button
          type="button"
          className="ig-mention-btn"
          onClick={handleAddMention}
          disabled={disabled || !mentionInput.trim()}
        >
          Add to caption
        </button>
      </div>
      {error && <div className="ig-mention-error">{error}</div>}
      {toast && <div className="ig-mention-toast">{toast}</div>}
    </div>
  )
}
