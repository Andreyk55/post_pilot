/**
 * Derives a user-facing upload error message, preferring a real, specific message
 * from the server/API/network over a generic fallback.
 *
 * The upload pipeline (`mediaApi.initUpload` / `uploadFile` / `completeUpload` /
 * `validateMedia`) rejects with an `Error` whose message carries the server-provided
 * reason when one is available. Surfacing it — instead of replacing every failure with
 * "Failed to upload file" — is the difference between a user knowing *why* an upload
 * failed and being stuck. The generic fallback is used only when no detail exists.
 */
export function getUploadErrorMessage(
  err: unknown,
  fallback = 'Upload failed. Please try again.',
): string {
  if (err instanceof Error) {
    const message = err.message?.trim()
    if (message) return message
  }
  if (typeof err === 'string' && err.trim()) return err.trim()
  return fallback
}
