/**
 * Utility functions for text processing in PostPilot
 */

export interface StripHashtagsResult {
  cleanedText: string
  removedHashtags: string[]
}

/**
 * Strips hashtags from text while preserving URLs, @mentions, and regular punctuation.
 * Uses Unicode-aware regex to support hashtags in any language (Hebrew, Russian, etc.)
 * 
 * @param text - The input text that may contain hashtags
 * @returns Object containing the cleaned text and array of removed hashtags
 */
export function stripHashtags(text: string): StripHashtagsResult {
  // Unicode-aware regex: matches # followed by letters, numbers, or underscores in any language
  const hashtagRegex = /#[\p{L}\p{N}_]+/gu
  
  // Find all hashtags before removing them
  const removedHashtags = text.match(hashtagRegex) || []
  
  // Remove hashtags
  let cleanedText = text.replace(hashtagRegex, '')
  
  // Clean up extra whitespace:
  // 1. Replace multiple spaces with single space
  cleanedText = cleanedText.replace(/  +/g, ' ')
  
  // 2. Remove spaces at the beginning of lines
  cleanedText = cleanedText.replace(/^ +/gm, '')
  
  // 3. Remove spaces at the end of lines
  cleanedText = cleanedText.replace(/ +$/gm, '')
  
  // 4. Remove multiple blank lines (keep at most one)
  cleanedText = cleanedText.replace(/\n{3,}/g, '\n\n')
  
  // 5. Trim leading/trailing whitespace
  cleanedText = cleanedText.trim()
  
  return {
    cleanedText,
    removedHashtags,
  }
}
