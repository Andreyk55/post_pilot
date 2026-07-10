import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { DeleteAccountConfirmModal } from './DeleteAccountConfirmModal'

function render(props: Partial<Parameters<typeof DeleteAccountConfirmModal>[0]> = {}) {
  return renderToStaticMarkup(
    <DeleteAccountConfirmModal
      isOpen
      deleting={false}
      onCancel={() => {}}
      onConfirm={() => {}}
      {...props}
    />,
  )
}

describe('DeleteAccountConfirmModal', () => {
  it('renders nothing when closed', () => {
    expect(render({ isOpen: false })).toBe('')
  })

  it('renders the confirmation title and irreversible warning', () => {
    const html = render()
    expect(html).toContain('Delete account permanently?')
    expect(html).toContain('cannot be undone')
  })

  it('lists the data that will be deleted', () => {
    const html = render()
    expect(html).toContain('Publish Harbor')
    expect(html).toContain('owned workspaces')
    expect(html).toContain('provider connections')
    expect(html).toContain('scheduled posts')
    expect(html).toContain('uploaded media')
    expect(html).toContain('bucket files')
  })

  it('notes that already-published posts are not deleted automatically', () => {
    const html = render()
    expect(html).toContain('Posts already published to Facebook or Instagram are not deleted automatically')
  })

  it('offers Cancel and final-confirm buttons', () => {
    const html = render()
    expect(html).toContain('Cancel')
    expect(html).toContain('Yes, delete permanently')
  })

  it('shows a deleting state and disables both buttons while the delete is in flight', () => {
    const html = render({ deleting: true })
    expect(html).toContain('Deleting…')
    // Both action buttons carry the disabled attribute.
    expect(html.match(/disabled/g)?.length).toBe(2)
  })

  it('shows a failure message inside the modal when one is provided', () => {
    const html = render({ error: 'We could not delete your account.' })
    expect(html).toContain('delete-account-modal-error')
    expect(html).toContain('We could not delete your account.')
  })

  it('renders no error block when there is no error', () => {
    expect(render()).not.toContain('delete-account-modal-error')
  })

  it('exposes the dialog to assistive tech', () => {
    const html = render()
    expect(html).toContain('role="dialog"')
    expect(html).toContain('aria-modal="true"')
  })
})
