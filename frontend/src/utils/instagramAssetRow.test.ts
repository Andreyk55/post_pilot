import { describe, it, expect } from 'vitest'
import { instagramAssetRowView } from './instagramAssetRow'

describe('instagramAssetRowView', () => {
  // Req #1: IG connected row does not render a disconnect X — derived assets are read-only.
  it('never exposes a disconnect action (IG is a derived asset)', () => {
    const view = instagramAssetRowView({
      username: 'appquestor',
      name: 'App Questor',
      pageName: 'Posts Dev Page',
    })
    expect(view.canDisconnect).toBe(false)
  })

  // Req #2: IG row shows the parent Facebook Page name.
  it('attributes the IG to its parent connected Facebook Page', () => {
    const view = instagramAssetRowView({
      username: 'appquestor',
      name: 'App Questor',
      pageName: 'Posts Dev Page',
    })
    expect(view.parentPageLabel).toBe(
      'Available through connected Facebook Page: Posts Dev Page',
    )
    expect(view.parentPageLabel).toContain('Posts Dev Page')
  })

  it('prefers the @username for display', () => {
    const view = instagramAssetRowView({
      username: 'appquestor',
      name: 'App Questor',
      pageName: 'Posts Dev Page',
    })
    expect(view.displayName).toBe('@appquestor')
  })

  it('falls back to the display name, then a stable label, when no username', () => {
    expect(
      instagramAssetRowView({ username: '', name: 'App Questor', pageName: 'P' }).displayName,
    ).toBe('App Questor')
    expect(
      instagramAssetRowView({ username: '', name: undefined, pageName: 'P' }).displayName,
    ).toBe('Instagram Account')
  })
})
