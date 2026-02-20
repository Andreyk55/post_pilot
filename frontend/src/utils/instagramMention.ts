/**
 * Utilities for Instagram @mention handling.
 * Parses usernames from raw input (URL, @handle, or plain text)
 * and inserts them into caption text.
 */

/** Reserved Instagram path segments that are NOT profile handles */
const RESERVED_SEGMENTS = new Set([
  'p', 'reel', 'tv', 'stories', 'explore', 'accounts', 'direct',
  'reels', 'tags', 'locations', 'nametag', 'about', 'developer',
])

/**
 * Validates an Instagram username string.
 * Rules: 1–30 chars, letters/digits/underscore/dot,
 * cannot start or end with dot, no consecutive dots.
 */
function isValidUsername(username: string): boolean {
  if (username.length < 1 || username.length > 30) return false
  if (username.startsWith('.') || username.endsWith('.')) return false
  if (username.includes('..')) return false
  return /^[a-zA-Z0-9_.]+$/.test(username)
}

/**
 * Parse an Instagram username from various input formats.
 *
 * Accepts:
 * - Plain username: "natgeo"
 * - @-prefixed: "@natgeo"
 * - Instagram URLs: "https://www.instagram.com/natgeo/"
 *
 * Returns the clean username (without @) or null if invalid.
 */
export function parseInstagramUsername(raw: string): string | null {
  let input = raw.trim()
  if (!input) return null

  // Check if it looks like a URL (contains instagram.com)
  if (input.includes('instagram.com')) {
    try {
      // Normalise: add protocol if missing so URL can parse
      let urlString = input
      if (!urlString.startsWith('http://') && !urlString.startsWith('https://')) {
        urlString = 'https://' + urlString
      }
      const url = new URL(urlString)

      // Verify host
      if (!url.hostname.includes('instagram.com')) return null

      // Extract first meaningful path segment
      const segments = url.pathname.split('/').filter(Boolean)
      if (segments.length === 0) return null

      const firstSegment = segments[0].toLowerCase()
      if (RESERVED_SEGMENTS.has(firstSegment)) return null

      input = segments[0] // keep original case for the username
    } catch {
      return null
    }
  } else {
    // Strip leading @ if present
    if (input.startsWith('@')) {
      input = input.slice(1)
    }
  }

  // Remove any trailing slashes (edge case from manual paste)
  input = input.replace(/\/+$/, '')

  return isValidUsername(input) ? input : null
}

/**
 * Insert a @mention into caption text at the given cursor position,
 * adding surrounding whitespace as needed.
 *
 * Returns the new caption and the new cursor position (right after the inserted mention).
 */
export function insertMentionAtCursor(
  caption: string,
  username: string,
  cursorPos: number | null,
): { newCaption: string; newCursorPos: number } {
  const mention = '@' + username

  if (cursorPos === null || cursorPos < 0 || cursorPos > caption.length) {
    // Append to end
    if (caption.length === 0) {
      return { newCaption: mention, newCursorPos: mention.length }
    }
    const newCaption = caption + '\n' + mention
    return { newCaption, newCursorPos: newCaption.length }
  }

  // Build prefix/suffix with spacing
  let prefix = ''
  if (cursorPos > 0 && !/\s/.test(caption[cursorPos - 1])) {
    prefix = ' '
  }
  let suffix = ''
  if (cursorPos < caption.length && !/\s/.test(caption[cursorPos])) {
    suffix = ' '
  }

  const insert = prefix + mention + suffix
  const before = caption.slice(0, cursorPos)
  const after = caption.slice(cursorPos)
  const newCaption = before + insert + after
  const newCursorPos = cursorPos + prefix.length + mention.length

  return { newCaption, newCursorPos }
}

/**
 * Check if a mention already exists in the caption (case-insensitive).
 */
export function captionContainsMention(caption: string, username: string): boolean {
  const mention = '@' + username
  return caption.toLowerCase().includes(mention.toLowerCase())
}
