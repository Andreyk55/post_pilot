import { describe, it, expect } from 'vitest'
// Import component sources as raw strings (Vite `?raw`) so these checks run in
// the project's Node test environment without a DOM harness.
import schedulePostSource from './SchedulePost.tsx?raw'
import badgeSource from './WorkspaceContextBadge.tsx?raw'
import connectedAccountsSource from '../pages/ConnectedAccountsPage.tsx?raw'
import workspaceGuardSource from './WorkspaceGuard.tsx?raw'
import workspaceSwitcherSource from './WorkspaceSwitcher.tsx?raw'

/**
 * Product rule: workspace switching/creation/management is centralized in the
 * sidebar workspace selector (<WorkspaceSwitcher>) ONLY. Page-level workspace
 * badges are read-only context labels: no Switch / Create / Manage controls, and
 * no page opens the workspace selector directly.
 *
 * There is no DOM test environment configured for this project (no jsdom /
 * testing-library), so these guarantees are enforced at the source level rather
 * than by rendering the components. If a DOM harness is added later, prefer
 * render-based assertions over these string checks.
 */

describe('WorkspaceContextBadge — read-only by default', () => {
  it('renders a non-interactive <span>, not a <button>', () => {
    expect(badgeSource).not.toMatch(/<button/)
    expect(badgeSource).toMatch(/<span[^>]*className=\{`ws-badge/)
  })

  it('has no Switch affordance, no click handler, and never opens the selector', () => {
    // The rendered markup carries no Switch label/affordance and no wiring to the
    // selector. (The component docstring references the sidebar "WorkspaceSwitcher"
    // and the rule itself, so we target the affordance class + JSX, not the word.)
    expect(badgeSource).not.toMatch(/ws-badge__switch/)
    expect(badgeSource).not.toMatch(/>\s*Switch\b/)
    expect(badgeSource).not.toMatch(/onClick/)
    expect(badgeSource).not.toMatch(/openSelector/)
    expect(badgeSource).not.toMatch(/useWorkspaces/)
  })
})

describe('SchedulePost — workspace selection stays in the sidebar', () => {
  it('renders the read-only workspace context badge ("Posting to")', () => {
    expect(schedulePostSource).toMatch(/<WorkspaceContextBadge\b[^>]*action="Posting to"/)
  })

  it('does not render a Switch workspace button or open the selector', () => {
    // No "Switch workspace" affordance and no selector/switch wiring. (Plain
    // "switch"/"switching" in unrelated comments — e.g. platform switching — is
    // fine; we target the workspace-control surfaces specifically.)
    expect(schedulePostSource).not.toMatch(/Switch workspace/i)
    expect(schedulePostSource).not.toMatch(/ws-badge__switch/)
    expect(schedulePostSource).not.toMatch(/openSelector/)
    expect(schedulePostSource).not.toMatch(/useWorkspaces/)
    expect(schedulePostSource).not.toMatch(/switchTo/)
  })
})

describe('ConnectedAccountsPage — workspace selection stays in the sidebar', () => {
  it('renders the read-only workspace context badge ("Connecting for")', () => {
    expect(connectedAccountsSource).toMatch(/<WorkspaceContextBadge\b[^>]*action="Connecting for"/)
  })

  it('does not render a Switch workspace button or open the selector', () => {
    expect(connectedAccountsSource).not.toMatch(/Switch workspace/i)
    expect(connectedAccountsSource).not.toMatch(/ws-badge__switch/)
    expect(connectedAccountsSource).not.toMatch(/openSelector/)
    expect(connectedAccountsSource).not.toMatch(/useWorkspaces/)
    expect(connectedAccountsSource).not.toMatch(/switchTo/)
  })
})

describe('WorkspaceGuard — blocks, never becomes a second workspace UI', () => {
  it('blocks with a message that points the user to the sidebar', () => {
    expect(workspaceGuardSource).toMatch(/Select a workspace from the sidebar before continuing\./)
  })

  it('does not render workspace switch/create/manage controls itself', () => {
    // The guard must not *call* the workspace mutation APIs or render the removed
    // selection modal — those live only in the sidebar WorkspaceSwitcher. (We
    // match call sites like `switchTo(` / `create(`, not the words in prose.)
    expect(workspaceGuardSource).not.toMatch(/switchTo\s*\(/)
    expect(workspaceGuardSource).not.toMatch(/\bcreate\s*\(/)
    expect(workspaceGuardSource).not.toMatch(/WorkspaceSelectionModal/)
  })

  it('does not auto-open a workspace selector', () => {
    expect(workspaceGuardSource).not.toMatch(/openSelector/)
    expect(workspaceGuardSource).not.toMatch(/selectorOpen/)
  })
})

describe('WorkspaceSwitcher — the only interactive workspace control', () => {
  it('still exposes switch and create actions in the sidebar', () => {
    // This is the single permitted switch/create surface; keep it intact.
    expect(workspaceSwitcherSource).toMatch(/switchTo\s*\(/)
    expect(workspaceSwitcherSource).toMatch(/\bcreate\s*\(/)
  })
})
