import { describe, it, expect, vi } from 'vitest'
import {
  createDeleteAccountController,
  DELETE_ACCOUNT_ERROR,
} from './deleteAccountController'

/**
 * Builds a controller with fully mocked dependencies so each test can assert
 * exactly when the irreversible delete API is (and is not) called.
 */
function makeController(overrides: { deleteAccount?: () => Promise<void> } = {}) {
  const setPhase = vi.fn()
  const setError = vi.fn()
  const onDeleted = vi.fn().mockResolvedValue(undefined)
  const deleteAccount = overrides.deleteAccount ?? vi.fn().mockResolvedValue(undefined)

  const controller = createDeleteAccountController({
    setPhase,
    setError,
    onDeleted,
    deleteAccount,
  })

  return { controller, setPhase, setError, onDeleted, deleteAccount }
}

describe('createDeleteAccountController', () => {
  it('does NOT call the delete API when the first button opens the modal', () => {
    const { controller, setPhase, deleteAccount } = makeController()

    controller.requestDelete()

    // The modal appears (phase → confirming) but nothing is deleted yet.
    expect(setPhase).toHaveBeenCalledWith('confirming')
    expect(deleteAccount).not.toHaveBeenCalled()
  })

  it('does NOT call the delete API when the modal is cancelled', () => {
    const { controller, setPhase, deleteAccount } = makeController()

    controller.requestDelete()
    controller.cancel()

    // Cancel returns to idle (modal closed) without ever deleting.
    expect(setPhase).toHaveBeenLastCalledWith('idle')
    expect(deleteAccount).not.toHaveBeenCalled()
  })

  it('calls the delete API only on the final confirmation, with the confirmation text', async () => {
    const { controller, deleteAccount, onDeleted, setPhase } = makeController()

    await controller.confirmDelete('DELETE MY ACCOUNT')

    expect(deleteAccount).toHaveBeenCalledTimes(1)
    expect(deleteAccount).toHaveBeenCalledWith('DELETE MY ACCOUNT')
    expect(setPhase).toHaveBeenCalledWith('deleting')
    expect(onDeleted).toHaveBeenCalledTimes(1)
  })

  it('surfaces an error and keeps the modal open when deletion fails', async () => {
    const failing = vi.fn().mockRejectedValue(new Error('boom'))
    const { controller, setError, setPhase, onDeleted } = makeController({
      deleteAccount: failing,
    })

    await controller.confirmDelete('DELETE MY ACCOUNT')

    expect(setError).toHaveBeenCalledWith(DELETE_ACCOUNT_ERROR)
    // Back to 'confirming' so the user can retry from the still-open modal.
    expect(setPhase).toHaveBeenLastCalledWith('confirming')
    expect(onDeleted).not.toHaveBeenCalled()
  })
})
