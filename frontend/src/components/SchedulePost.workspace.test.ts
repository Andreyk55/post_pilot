import { describe, it, expect } from 'vitest'
// Import component sources as raw strings (Vite `?raw`) so these checks run in
// the project's Node test environment without a DOM harness.
import schedulePostSource from './SchedulePost.tsx?raw'
import badgeSource from './WorkspaceContextBadge.tsx?raw'
import connectedAccountsSource from '../pages/ConnectedAccountsPage.tsx?raw'
import assetsPageSource from '../pages/AssetsPage.tsx?raw'
import schedulePostsPageSource from '../pages/SchedulePostsPage.tsx?raw'
import workspaceGuardSource from './WorkspaceGuard.tsx?raw'
import workspaceSwitcherSource from './WorkspaceSwitcher.tsx?raw'
import aiAssistPanelSource from './AiAssistPanel.tsx?raw'
import suggestedTimesSource from './SuggestedTimes.tsx?raw'
import composerEnabledSource from '../hooks/useComposerEnabled.ts?raw'

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

  it('renders the page or account selector before Post Type', () => {
    const metaChannelIndex = schedulePostSource.indexOf('<label>Meta Channel</label>')
    const postTypeIndex = schedulePostSource.indexOf('<label>Post Type</label>')
    const facebookPageIndex = schedulePostSource.indexOf('<label htmlFor="facebookPage">Facebook Page</label>')
    const instagramAccountIndex = schedulePostSource.indexOf('<label htmlFor="instagramAccount">Instagram Account</label>')

    expect(metaChannelIndex).toBeGreaterThan(-1)
    expect(facebookPageIndex).toBeGreaterThan(metaChannelIndex)
    expect(instagramAccountIndex).toBeGreaterThan(metaChannelIndex)
    expect(postTypeIndex).toBeGreaterThan(facebookPageIndex)
    expect(postTypeIndex).toBeGreaterThan(instagramAccountIndex)
  })

  it('keeps the Facebook target-selection warning copy aligned with the selector', () => {
    expect(composerEnabledSource).toMatch(/Select a Facebook Page above to enable scheduling and AI features\./)
  })

  it('only exposes Meta channels in the platform selector', () => {
    expect(schedulePostSource).toMatch(/label>Meta Channel</)
    expect(schedulePostSource).toMatch(/id: 'facebook'/)
    expect(schedulePostSource).toMatch(/id: 'instagram'/)
    expect(schedulePostSource).not.toMatch(/id: 'twitter'/)
    expect(schedulePostSource).not.toMatch(/id: 'linkedin'/)
    expect(schedulePostSource).not.toMatch(/Coming Soon/)
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

  it('focuses provider connection UI on Meta only', () => {
    expect(connectedAccountsSource).toMatch(/Connect your Meta account to manage Facebook Pages and linked Instagram accounts\./)
    expect(connectedAccountsSource).toMatch(/Facebook Pages and linked Instagram accounts/)
    expect(connectedAccountsSource).not.toMatch(/LinkedIn/)
    expect(connectedAccountsSource).not.toMatch(/Twitter/)
    expect(connectedAccountsSource).not.toMatch(/TikTok/)
    expect(connectedAccountsSource).not.toMatch(/Coming Soon/)
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

describe('SchedulePost AI sections', () => {
  it('renders AI Content Assist as a collapsed accordion with generated-variant summary support', () => {
    expect(aiAssistPanelSource).toMatch(/useState\(false\)/)
    expect(aiAssistPanelSource).toMatch(/AI Content Assist/)
    expect(aiAssistPanelSource).toMatch(/Generate captions, translate, or improve your post text\./)
    expect(aiAssistPanelSource).toMatch(/aria-expanded=\{expanded\}/)
    expect(aiAssistPanelSource).toMatch(/variants generated/)
  })

  it('keeps existing AI tabs and generated variants UI inside the accordion body', () => {
    expect(aiAssistPanelSource).toMatch(/>\s*Text\s*</)
    expect(aiAssistPanelSource).toMatch(/>\s*Translate\s*</)
    expect(aiAssistPanelSource).toMatch(/showMediaTab && \(/)
    expect(aiAssistPanelSource).toMatch(/Voice Profile/)
    expect(aiAssistPanelSource).toMatch(/Generated Variants/)
    expect(aiAssistPanelSource).toMatch(/>\s*Apply\s*</)
    expect(aiAssistPanelSource).toMatch(/Copied!'\s*:\s*'Copy'/)
    expect(aiAssistPanelSource).toMatch(/Regenerate/)
  })

  it('passes the selected media collection into AiAssistPanel so Media AI can reject multi-photo and video states', () => {
    expect(schedulePostSource).toMatch(/const aiAssistMediaItems: AiAssistMediaItem\[\] = carouselItems.length > 0/)
    expect(schedulePostSource).toMatch(/mediaItems=\{aiAssistMediaItems\}/)
  })

  it('renames Suggest Best Time to AI Best Time', () => {
    expect(suggestedTimesSource).toMatch(/AI Best Time/)
    expect(suggestedTimesSource).not.toMatch(/Suggest Best Time/)
  })

  it('keeps media and publishing controls outside the AI panel in SchedulePost', () => {
    const aiPanelIndex = schedulePostSource.indexOf('<AiAssistPanel')
    const mediaLabelIndex = schedulePostSource.indexOf("'Media (required)'")
    const formActionsIndex = schedulePostSource.indexOf('className="form-actions"')

    expect(aiPanelIndex).toBeGreaterThan(-1)
    expect(mediaLabelIndex).toBeGreaterThan(aiPanelIndex)
    expect(formActionsIndex).toBeGreaterThan(aiPanelIndex)
  })
})

describe('SchedulePost — asset selection behavior', () => {
  it('does not auto-select the first Facebook Page on load', () => {
    // Auto-select on load must be absent — user must choose explicitly
    expect(schedulePostSource).not.toMatch(/Auto-select first page if only one exists/)
    expect(schedulePostSource).not.toMatch(/response\.connection\.pages\.length === 1[\s\S]{0,60}setSelectedPageId/)
  })

  it('does not auto-select the first Instagram account on load', () => {
    expect(schedulePostSource).not.toMatch(/Auto-select first IG account if only one exists/)
    expect(schedulePostSource).not.toMatch(/igAccounts\.length === 1[\s\S]{0,60}setSelectedInstagramAccountId/)
  })

  it('does not auto-select an asset when switching to Facebook or Instagram', () => {
    expect(schedulePostSource).not.toMatch(/Auto-select IG account if switching to Instagram/)
    expect(schedulePostSource).not.toMatch(/Auto-select Facebook page if switching to Facebook/)
  })

  it('clears the Facebook Page selection when switching away from Facebook', () => {
    // The selectPlatform handler must contain both the guard condition and the clear call
    expect(schedulePostSource).toContain("selectedPlatforms.includes('facebook') && platformId !== 'facebook'")
    expect(schedulePostSource).toContain("setSelectedPageId('')")
  })

  it('clears the Instagram account selection when switching away from Instagram', () => {
    expect(schedulePostSource).toContain("selectedPlatforms.includes('instagram') && platformId !== 'instagram'")
    expect(schedulePostSource).toContain("setSelectedInstagramAccountId('')")
  })

  it('blocks schedule submit until an explicit Facebook Page is selected', () => {
    // isFormValid must gate on selectedPageId when Facebook is selected
    expect(schedulePostSource).toMatch(/!isFacebookSelected \|\| selectedPageId/)
  })

  it('blocks schedule submit until an explicit Instagram account is selected', () => {
    expect(schedulePostSource).toMatch(/!isInstagramSelected \|\| selectedInstagramAccountId/)
  })

  it('uses an unselected placeholder for the Facebook Page dropdown', () => {
    expect(schedulePostSource).toMatch(/Select a Facebook Page/)
  })

  it('uses an unselected placeholder for the Instagram Account dropdown', () => {
    expect(schedulePostSource).toMatch(/Select an Instagram Account/)
  })
})

describe('Meta-focused page copy', () => {
  it('keeps the schedule page positioned around Facebook and Instagram only', () => {
    expect(schedulePostsPageSource).toMatch(/Plan and schedule Facebook and Instagram content/)
  })

  it('describes assets as Facebook Pages plus linked Instagram professional accounts', () => {
    expect(assetsPageSource).toMatch(/Manage your Facebook Pages and linked Instagram professional accounts/)
    expect(assetsPageSource).toMatch(/Available through connected Facebook Pages/)
    expect(assetsPageSource).toMatch(/Facebook Pages and linked Instagram accounts/)
  })
})
