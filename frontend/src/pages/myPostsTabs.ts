import type { PostStatus, PostStatusGroup } from '../api/posts'

/** Tab filter key: 'all', a single backend status, or a server-side status group. */
export type TabFilter =
  | { kind: 'all' }
  | { kind: 'status'; status: PostStatus }
  | { kind: 'group'; group: PostStatusGroup }

export type StatusTab = { label: string; value: string; filter: TabFilter }

// User-facing simplification: Publishing / Processing / RetryPending are collapsed into
// a single "In Progress" tab (statusGroup=inProgress). Individual post cards still show
// the specific status badge. Backend enum values are unchanged.
export const STATUS_TABS: StatusTab[] = [
  { label: 'All', value: 'all', filter: { kind: 'all' } },
  { label: 'Scheduled', value: 'Scheduled', filter: { kind: 'status', status: 'Scheduled' } },
  { label: 'In Progress', value: 'inProgress', filter: { kind: 'group', group: 'inProgress' } },
  { label: 'Published', value: 'Published', filter: { kind: 'status', status: 'Published' } },
  { label: 'Failed', value: 'Failed', filter: { kind: 'status', status: 'Failed' } },
  { label: 'Canceled', value: 'Canceled', filter: { kind: 'status', status: 'Canceled' } },
]

/**
 * Resolves a tab value to the (status, statusGroup) args passed to postsApi.getPaginated.
 * 'all' → no filter; a single-status tab → { status }; the In Progress tab → { statusGroup }.
 */
export function resolveFilter(tabValue: string): { status?: PostStatus; statusGroup?: PostStatusGroup } {
  const tab = STATUS_TABS.find(t => t.value === tabValue)
  if (!tab || tab.filter.kind === 'all') return {}
  if (tab.filter.kind === 'group') return { statusGroup: tab.filter.group }
  return { status: tab.filter.status }
}

/** User-facing label for a tab value (falls back to 'matching' for unknown values). */
export function tabLabel(tabValue: string): string {
  return STATUS_TABS.find(t => t.value === tabValue)?.label ?? 'matching'
}
