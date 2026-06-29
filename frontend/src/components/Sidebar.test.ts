import { describe, expect, it } from 'vitest'
// Import the component source as a raw string (Vite `?raw`) so these checks run in
// the project's Node test environment without a DOM harness — the same approach used
// by SchedulePost.workspace.test.ts and the channel-switch source tests.
import sidebarSource from './Sidebar.tsx?raw'

describe('Sidebar navigation — Publishing Assets label', () => {
  it('labels the assets nav item "Publishing Assets"', () => {
    expect(sidebarSource).toMatch(/label: 'Publishing Assets'/)
  })

  it('does not keep the generic "Assets" label', () => {
    expect(sidebarSource).not.toMatch(/label: 'Assets'/)
  })

  it('keeps the assets nav item id stable (route unchanged)', () => {
    // Only the visible label changes — the route id stays 'assets'.
    expect(sidebarSource).toMatch(/id: 'assets', label: 'Publishing Assets'/)
  })
})
