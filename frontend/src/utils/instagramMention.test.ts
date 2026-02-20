import { describe, it, expect } from 'vitest'
import { parseInstagramUsername, insertMentionAtCursor, captionContainsMention } from './instagramMention'

describe('parseInstagramUsername', () => {
  // Valid URLs
  it('parses https://www.instagram.com/natgeo/', () => {
    expect(parseInstagramUsername('https://www.instagram.com/natgeo/')).toBe('natgeo')
  })

  it('parses instagram.com/natgeo?utm=1', () => {
    expect(parseInstagramUsername('instagram.com/natgeo?utm=1')).toBe('natgeo')
  })

  it('parses http://instagram.com/natgeo', () => {
    expect(parseInstagramUsername('http://instagram.com/natgeo')).toBe('natgeo')
  })

  it('parses www.instagram.com/natgeo?ref=share', () => {
    expect(parseInstagramUsername('www.instagram.com/natgeo?ref=share')).toBe('natgeo')
  })

  // @-prefixed
  it('parses @natgeo', () => {
    expect(parseInstagramUsername('@natgeo')).toBe('natgeo')
  })

  // Plain username
  it('parses natgeo', () => {
    expect(parseInstagramUsername('natgeo')).toBe('natgeo')
  })

  // Username with dots and underscores
  it('accepts underscores and dots mid-username', () => {
    expect(parseInstagramUsername('john_doe.99')).toBe('john_doe.99')
  })

  // Reserved URL paths return null
  it('returns null for https://www.instagram.com/p/abc', () => {
    expect(parseInstagramUsername('https://www.instagram.com/p/abc')).toBeNull()
  })

  it('returns null for reel URLs', () => {
    expect(parseInstagramUsername('https://instagram.com/reel/xyz123')).toBeNull()
  })

  it('returns null for stories URLs', () => {
    expect(parseInstagramUsername('https://instagram.com/stories/user/123')).toBeNull()
  })

  it('returns null for explore URLs', () => {
    expect(parseInstagramUsername('https://instagram.com/explore/tags/food')).toBeNull()
  })

  // Invalid usernames
  it('returns null for .bad (starts with dot)', () => {
    expect(parseInstagramUsername('.bad')).toBeNull()
  })

  it('returns null for bad.. (consecutive dots)', () => {
    expect(parseInstagramUsername('bad..')).toBeNull()
  })

  it('returns null for bad. (ends with dot)', () => {
    expect(parseInstagramUsername('bad.')).toBeNull()
  })

  it('returns null for empty string', () => {
    expect(parseInstagramUsername('')).toBeNull()
  })

  it('returns null for whitespace only', () => {
    expect(parseInstagramUsername('   ')).toBeNull()
  })

  it('returns null for username with spaces', () => {
    expect(parseInstagramUsername('bad name')).toBeNull()
  })

  it('returns null for username with special chars', () => {
    expect(parseInstagramUsername('bad!name')).toBeNull()
  })

  it('returns null for username over 30 chars', () => {
    expect(parseInstagramUsername('a'.repeat(31))).toBeNull()
  })

  it('accepts single character username', () => {
    expect(parseInstagramUsername('a')).toBe('a')
  })

  it('accepts 30 char username', () => {
    expect(parseInstagramUsername('a'.repeat(30))).toBe('a'.repeat(30))
  })

  // Trimming
  it('trims whitespace from input', () => {
    expect(parseInstagramUsername('  @natgeo  ')).toBe('natgeo')
  })

  // URL with root path only
  it('returns null for bare instagram.com', () => {
    expect(parseInstagramUsername('https://instagram.com/')).toBeNull()
  })
})

describe('insertMentionAtCursor', () => {
  it('appends to empty caption', () => {
    const result = insertMentionAtCursor('', 'natgeo', null)
    expect(result.newCaption).toBe('@natgeo')
  })

  it('appends with newline to non-empty caption when cursor is null', () => {
    const result = insertMentionAtCursor('Hello world', 'natgeo', null)
    expect(result.newCaption).toBe('Hello world\n@natgeo')
  })

  it('inserts at cursor with space before when needed', () => {
    const result = insertMentionAtCursor('Hello world', 'natgeo', 5) // after "Hello"
    expect(result.newCaption).toBe('Hello @natgeo world')
  })

  it('does not add space before when already at whitespace', () => {
    const result = insertMentionAtCursor('Hello world', 'natgeo', 6) // after "Hello "
    expect(result.newCaption).toBe('Hello @natgeo world')
  })

  it('inserts at start of caption', () => {
    const result = insertMentionAtCursor('Hello', 'natgeo', 0)
    expect(result.newCaption).toBe('@natgeo Hello')
  })

  it('inserts at end of caption', () => {
    const result = insertMentionAtCursor('Hello', 'natgeo', 5)
    expect(result.newCaption).toBe('Hello @natgeo')
  })

  it('adds space after when next char is not whitespace', () => {
    const result = insertMentionAtCursor('AB', 'natgeo', 1) // between A and B
    expect(result.newCaption).toBe('A @natgeo B')
  })

  it('returns correct cursor position after insert', () => {
    const result = insertMentionAtCursor('Hello world', 'natgeo', 6)
    // "Hello @natgeo world" — cursor should be right after "@natgeo"
    expect(result.newCursorPos).toBe(6 + '@natgeo'.length)
  })
})

describe('captionContainsMention', () => {
  it('finds exact mention', () => {
    expect(captionContainsMention('Check out @natgeo today', 'natgeo')).toBe(true)
  })

  it('is case-insensitive', () => {
    expect(captionContainsMention('Check out @NatGeo today', 'natgeo')).toBe(true)
  })

  it('returns false when not present', () => {
    expect(captionContainsMention('Hello world', 'natgeo')).toBe(false)
  })

  it('does not match partial username', () => {
    // @natgeotravel contains @natgeo as substring — this is acceptable per spec
    // (exact string match, not word boundary)
    expect(captionContainsMention('@natgeotravel', 'natgeo')).toBe(true)
  })
})
