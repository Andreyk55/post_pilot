/**
 * Pure presentation logic for the public deletion-status page, split out so it can
 * be unit-tested without rendering React (mirrors myPostsTabs.ts).
 */

export type DeletionStatusTone = 'pending' | 'success' | 'error'

export interface DeletionStatusView {
  title: string
  message: string
  tone: DeletionStatusTone
}

/** Maps a backend status string to user-facing copy + tone. */
export function describeDeletionStatus(status: string): DeletionStatusView {
  switch (status) {
    case 'Processing':
      return {
        title: 'Deletion in progress',
        message: 'Your data deletion request has been received and is being processed.',
        tone: 'pending',
      }
    case 'Completed':
      return {
        title: 'Deletion completed',
        message: 'Your Meta-related data has been deleted from PostPilot.',
        tone: 'success',
      }
    case 'AlreadyDeleted':
      return {
        title: 'Nothing to delete',
        message: 'We found no data for this account. It may have already been deleted.',
        tone: 'success',
      }
    case 'Failed':
      return {
        title: 'Deletion failed',
        message:
          'Something went wrong while deleting your data. Please contact support so we can complete your request.',
        tone: 'error',
      }
    default:
      return {
        title: 'Unknown status',
        message: 'We could not determine the status of this request.',
        tone: 'error',
      }
  }
}
