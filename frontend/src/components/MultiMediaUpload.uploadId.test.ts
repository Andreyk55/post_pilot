import { describe, expect, it } from 'vitest'
// Source-level guarantees (no DOM test env in this project) that MultiMediaUpload
// gets its temporary ids from the shared createUploadClientId helper — generated
// once per item/session and stored — rather than calling Math.random()/randomUUID
// inline, and that the upload-ownership session semantics are preserved.
import multiMediaUploadSource from './MultiMediaUpload.tsx?raw'

describe('MultiMediaUpload — stable client-side upload ids', () => {
  it('no longer calls Math.random() or crypto.randomUUID() directly', () => {
    expect(multiMediaUploadSource).not.toMatch(/Math\.random/)
    expect(multiMediaUploadSource).not.toMatch(/crypto\.randomUUID/)
  })

  it('sources every temporary id from the shared helper', () => {
    expect(multiMediaUploadSource).toMatch(/import \{ createUploadClientId \} from '\.\.\/utils\/uploadClientId'/)
    // Pending placeholder ids and committed item ids both come from the helper.
    const helperCalls = multiMediaUploadSource.match(/createUploadClientId\(\)/g) ?? []
    expect(helperCalls.length).toBeGreaterThanOrEqual(2)
  })

  it('creates the session prefix once via a lazy useState initializer, not during render', () => {
    // Passing the helper by reference means React calls it a single time — no impure
    // call runs in the render path and the prefix is never regenerated on re-render.
    expect(multiMediaUploadSource).toMatch(/const \[uploadSessionInstance\] = useState\(createUploadClientId\)/)
    expect(multiMediaUploadSource).not.toMatch(/useRef\(Math\.random/)
  })

  it('keeps React keys on the stored ids, never a freshly generated value', () => {
    expect(multiMediaUploadSource).toMatch(/items\.map\(\(item, index\) => \([\s\S]*?key=\{item\.id\}/)
    expect(multiMediaUploadSource).toMatch(/pendingUploads\.map\(pending => \([\s\S]*?key=\{pending\.id\}/)
    expect(multiMediaUploadSource).not.toMatch(/key=\{createUploadClientId\(\)\}/)
  })

  it('preserves the upload-ownership session semantics (stale-validation guard intact)', () => {
    // The owner key still composes the per-component prefix with an incrementing
    // counter, and late results are still dropped when the owner is superseded.
    expect(multiMediaUploadSource).toMatch(/`\$\{uploadSessionInstance\}:\$\{\+\+uploadSessionCounterRef\.current\}`/)
    expect(multiMediaUploadSource).toMatch(/const isStaleUploadOwner = \(uploadOwnerKey: string\) => activeUploadOwnerKeyRef\.current !== uploadOwnerKey/)
  })
})
