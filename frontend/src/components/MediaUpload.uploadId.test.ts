import { describe, expect, it } from 'vitest'
// Source-level guarantees (no DOM test env in this project) that the single-media
// uploader gets its validation-ownership session prefix from the shared
// createUploadClientId helper — generated once via a lazy useState initializer,
// never with Math.random() in the render path — while preserving the owner-key
// stale-validation semantics. Mirrors MultiMediaUpload.uploadId.test.ts.
import mediaUploadSource from './MediaUpload.tsx?raw'

describe('MediaUpload — stable client-side upload id', () => {
  it('no longer calls Math.random() directly', () => {
    expect(mediaUploadSource).not.toMatch(/Math\.random/)
    expect(mediaUploadSource).not.toMatch(/useRef\(Math\.random/)
  })

  it('sources the session prefix from the shared helper', () => {
    expect(mediaUploadSource).toMatch(/import \{ createUploadClientId \} from '\.\.\/utils\/uploadClientId'/)
    const helperCalls = mediaUploadSource.match(/createUploadClientId/g) ?? []
    // The import reference + the lazy initializer reference.
    expect(helperCalls.length).toBeGreaterThanOrEqual(2)
  })

  it('creates the session prefix once via a lazy useState initializer, not during render', () => {
    expect(mediaUploadSource).toMatch(/const \[sessionInstance\] = useState\(createUploadClientId\)/)
  })

  it('preserves the upload-ownership session semantics (stale-validation guard intact)', () => {
    // The owner key still composes the per-component prefix with an incrementing
    // counter, and late results are still dropped when the owner is superseded.
    expect(mediaUploadSource).toMatch(/`\$\{sessionInstance\}:\$\{\+\+sessionCounterRef\.current\}`/)
    expect(mediaUploadSource).toMatch(/const isStaleUploadOwner = \(uploadOwnerKey: string\) => activeUploadOwnerKeyRef\.current !== uploadOwnerKey/)
  })
})
