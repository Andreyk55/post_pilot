import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { AvatarImage, isUnstableMetaAvatarUrl } from './AvatarImage'

describe('AvatarImage', () => {
  it('renders the fallback for ephemeral Meta CDN avatar URLs', () => {
    const markup = renderToStaticMarkup(
      <AvatarImage
        src="https://scontent-fra3-2.xx.fbcdn.net/v/t39.30808-1/avatar.jpg"
        alt="Posts Dev Page"
        fallback={<span>P</span>}
      />,
    )

    expect(markup).toContain('<span>P</span>')
    expect(markup).not.toContain('<img')
  })

  it('allows stable non-Meta avatar URLs to render as images', () => {
    const markup = renderToStaticMarkup(
      <AvatarImage
        src="https://lh3.googleusercontent.com/a/avatar"
        alt="User avatar"
        fallback={<span>U</span>}
      />,
    )

    expect(markup).toContain('<img')
    expect(markup).toContain('src="https://lh3.googleusercontent.com/a/avatar"')
  })

  it('detects Meta CDN avatar hosts only', () => {
    expect(isUnstableMetaAvatarUrl('https://profile.xx.fbcdn.net/avatar.jpg')).toBe(true)
    expect(isUnstableMetaAvatarUrl('https://platform-lookaside.fbsbx.com/platform/profilepic/')).toBe(true)
    expect(isUnstableMetaAvatarUrl('https://example.com/avatar.jpg')).toBe(false)
    expect(isUnstableMetaAvatarUrl('/local/avatar.jpg')).toBe(false)
  })
})
