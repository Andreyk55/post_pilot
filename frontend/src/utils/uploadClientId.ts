/**
 * Stable client-side id for an in-flight media upload.
 *
 * Used for temporary upload-item identity (React keys), pending-upload tracking,
 * and the per-component validation-ownership session prefix. Generate it once when
 * the item/session is created and store the result — never call this during render
 * (it is non-deterministic by design, so a fresh call each render would break the
 * stored identity it is meant to provide).
 */
export function createUploadClientId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID()
  }

  // Fallback only for environments without crypto.randomUUID() (older browsers /
  // some non-browser runtimes). This is NOT used for security — it only provides a
  // temporary, collision-unlikely identity for UI bookkeeping. Math.random() is
  // isolated here, out of any component render path, so the react-hooks purity lint
  // rule stays satisfied at the call sites.
  return `${Date.now()}-${Math.random().toString(36).slice(2)}`
}
