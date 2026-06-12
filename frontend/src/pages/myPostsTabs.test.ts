import { describe, it, expect, vi, afterEach } from 'vitest'
import { STATUS_TABS, resolveFilter, tabLabel } from './myPostsTabs'
import { postsApi } from '../api/posts'

describe('My Posts status tabs', () => {
  it('renders only the simplified tab set', () => {
    const labels = STATUS_TABS.map(t => t.label)
    expect(labels).toEqual(['All', 'Scheduled', 'In Progress', 'Published', 'Failed', 'Canceled'])
  })

  it('has no separate Publishing, Processing, or Retry Pending tabs', () => {
    const labels = STATUS_TABS.map(t => t.label)
    expect(labels).not.toContain('Publishing')
    expect(labels).not.toContain('Processing')
    expect(labels).not.toContain('Retry Pending')
    expect(labels).not.toContain('RetryPending')
    expect(labels).not.toContain('Retrying')
  })

  it('the In Progress tab maps to the inProgress status group', () => {
    expect(resolveFilter('inProgress')).toEqual({ statusGroup: 'inProgress' })
  })

  it("'all' maps to no filter", () => {
    expect(resolveFilter('all')).toEqual({})
  })

  it.each([
    ['Scheduled', 'Scheduled'],
    ['Published', 'Published'],
    ['Failed', 'Failed'],
    ['Canceled', 'Canceled'],
  ])('the %s tab maps to a single status filter', (tabValue, status) => {
    expect(resolveFilter(tabValue)).toEqual({ status })
  })

  it('tabLabel resolves known values and falls back for unknown', () => {
    expect(tabLabel('inProgress')).toBe('In Progress')
    expect(tabLabel('all')).toBe('All')
    expect(tabLabel('nope')).toBe('matching')
  })
})

describe('postsApi.getPaginated query construction', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  function mockFetchOk() {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0, hasNextPage: false, hasPreviousPage: false }),
    })
    vi.stubGlobal('fetch', fetchMock)
    return fetchMock
  }

  it('clicking In Progress issues statusGroup=inProgress (no status param)', async () => {
    const fetchMock = mockFetchOk()
    const { statusGroup } = resolveFilter('inProgress')

    await postsApi.getPaginated(1, 20, undefined, undefined, statusGroup)

    const calledUrl: string = fetchMock.mock.calls[0][0]
    expect(calledUrl).toContain('statusGroup=inProgress')
    expect(calledUrl).not.toContain('status=')
  })

  it('a single-status tab issues status=<Status> (no statusGroup param)', async () => {
    const fetchMock = mockFetchOk()
    const { status } = resolveFilter('Scheduled')

    await postsApi.getPaginated(1, 20, status)

    const calledUrl: string = fetchMock.mock.calls[0][0]
    expect(calledUrl).toContain('status=Scheduled')
    expect(calledUrl).not.toContain('statusGroup=')
  })
})
