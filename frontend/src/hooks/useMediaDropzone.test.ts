import { describe, expect, it, vi } from 'vitest'
import {
  createMediaDropController,
  dragPayloadHasFiles,
  extractFilesFromDataTransfer,
  type DataTransferItemLike,
  type DragDataTransferLike,
  type DragEventLike,
} from './useMediaDropzone'
// Source-level guarantees (the project has no DOM test env): the window navigation guard
// is cleaned up on unmount, and the hook contains no media-validation logic of its own.
import useMediaDropzoneSource from './useMediaDropzone.ts?raw'

// Lightweight fakes — the project intentionally avoids constructing real File/DataTransfer
// objects in tests. extractFilesFromDataTransfer only reads the structural shape below.
const file = (name: string, type = 'image/jpeg'): File => ({ name, type, size: 1000 } as unknown as File)

interface FakeItemSpec {
  kind: string
  file?: File | null
  directory?: boolean
}

const makeItem = (spec: FakeItemSpec): DataTransferItemLike => ({
  kind: spec.kind,
  getAsFile: () => spec.file ?? null,
  webkitGetAsEntry: () => (spec.directory ? { isDirectory: true } : { isDirectory: false }),
})

const makeDataTransfer = (opts: {
  files?: File[]
  items?: FakeItemSpec[]
  types?: string[]
}): DragDataTransferLike => ({
  files: opts.files,
  items: opts.items?.map(makeItem),
  // Default to a file drag unless a test overrides it.
  types: opts.types ?? ['Files'],
  dropEffect: 'none',
})

const makeEvent = (dataTransfer: DragDataTransferLike | null) => {
  const preventDefault = vi.fn()
  const event: DragEventLike = { preventDefault, dataTransfer }
  return { event, preventDefault }
}

const makeController = (opts?: { disabled?: boolean }) => {
  const active: boolean[] = []
  const onFiles = vi.fn()
  let disabled = opts?.disabled ?? false
  const controller = createMediaDropController({
    isDisabled: () => disabled,
    setDragActive: (value) => active.push(value),
    onFiles,
  })
  return {
    controller,
    onFiles,
    /** Latest active value pushed, or false if none. */
    isActive: () => (active.length ? active[active.length - 1] : false),
    activeHistory: active,
    setDisabled: (value: boolean) => {
      disabled = value
    },
  }
}

describe('dragPayloadHasFiles', () => {
  it('is true when the payload advertises the Files type', () => {
    expect(dragPayloadHasFiles({ types: ['Files'] })).toBe(true)
    expect(dragPayloadHasFiles({ types: ['text/plain', 'Files'] })).toBe(true)
  })

  it('is false for non-file drags (text, links, elements)', () => {
    expect(dragPayloadHasFiles({ types: ['text/plain'] })).toBe(false)
    expect(dragPayloadHasFiles({ types: [] })).toBe(false)
  })

  it('is permissive (true) when the type list is unavailable, so navigation is still blocked', () => {
    expect(dragPayloadHasFiles(undefined)).toBe(true)
    expect(dragPayloadHasFiles({})).toBe(true)
  })
})

describe('extractFilesFromDataTransfer', () => {
  it('returns files from items in payload order', () => {
    const dt = makeDataTransfer({
      items: [
        { kind: 'file', file: file('a.jpg') },
        { kind: 'file', file: file('b.png', 'image/png') },
        { kind: 'file', file: file('c.mp4', 'video/mp4') },
      ],
    })
    expect(extractFilesFromDataTransfer(dt).map((f) => f.name)).toEqual(['a.jpg', 'b.png', 'c.mp4'])
  })

  it('skips directories and non-file items (folders / dragged text)', () => {
    const dt = makeDataTransfer({
      items: [
        { kind: 'file', file: file('keep.jpg') },
        { kind: 'file', file: file('folder'), directory: true },
        { kind: 'string' },
      ],
    })
    expect(extractFilesFromDataTransfer(dt).map((f) => f.name)).toEqual(['keep.jpg'])
  })

  it('falls back to the files list when items are unavailable', () => {
    const dt = makeDataTransfer({ files: [file('x.jpg'), file('y.jpg')], items: undefined })
    expect(extractFilesFromDataTransfer(dt).map((f) => f.name)).toEqual(['x.jpg', 'y.jpg'])
  })

  it('returns an empty array for an empty or missing payload', () => {
    expect(extractFilesFromDataTransfer(null)).toEqual([])
    expect(extractFilesFromDataTransfer(makeDataTransfer({ items: [], files: [] }))).toEqual([])
  })
})

describe('createMediaDropController', () => {
  it('dragover calls preventDefault (so the browser does not open the file) and sets a copy effect', () => {
    const { controller } = makeController()
    const dt = makeDataTransfer({ items: [{ kind: 'file', file: file('a.jpg') }] })
    const { event, preventDefault } = makeEvent(dt)
    controller.onDragOver(event)
    expect(preventDefault).toHaveBeenCalledTimes(1)
    expect(dt.dropEffect).toBe('copy')
  })

  it('dragenter turns on the active state', () => {
    const { controller, isActive } = makeController()
    const { event, preventDefault } = makeEvent(makeDataTransfer({ items: [{ kind: 'file', file: file('a.jpg') }] }))
    controller.onDragEnter(event)
    expect(preventDefault).toHaveBeenCalled()
    expect(isActive()).toBe(true)
  })

  it('dragleave clears the active state once the drag fully leaves', () => {
    const { controller, isActive } = makeController()
    const dt = makeDataTransfer({ items: [{ kind: 'file', file: file('a.jpg') }] })
    controller.onDragEnter(makeEvent(dt).event)
    controller.onDragLeave(makeEvent(dt).event)
    expect(isActive()).toBe(false)
  })

  it('does not flicker when the pointer crosses child elements (depth counter)', () => {
    const { controller, isActive } = makeController()
    const dt = makeDataTransfer({ items: [{ kind: 'file', file: file('a.jpg') }] })
    // enter zone, then enter a child (both bubble to the zone) → depth 2
    controller.onDragEnter(makeEvent(dt).event)
    controller.onDragEnter(makeEvent(dt).event)
    // leaving the parent as we enter the child fires one dragleave → depth 1, still active
    controller.onDragLeave(makeEvent(dt).event)
    expect(isActive()).toBe(true)
    // finally leave the zone entirely → depth 0, inactive
    controller.onDragLeave(makeEvent(dt).event)
    expect(isActive()).toBe(false)
  })

  it('drop passes the extracted files (in order) to onFiles and resets the active state', () => {
    const { controller, onFiles, isActive } = makeController()
    controller.onDragEnter(makeEvent(makeDataTransfer({ items: [{ kind: 'file', file: file('a.jpg') }] })).event)
    const dt = makeDataTransfer({
      items: [
        { kind: 'file', file: file('a.jpg') },
        { kind: 'file', file: file('b.jpg') },
      ],
    })
    const { event, preventDefault } = makeEvent(dt)
    controller.onDrop(event)
    expect(preventDefault).toHaveBeenCalled()
    expect(onFiles).toHaveBeenCalledTimes(1)
    expect(onFiles.mock.calls[0][0].map((f: File) => f.name)).toEqual(['a.jpg', 'b.jpg'])
    expect(isActive()).toBe(false)
  })

  it('drop of the same file twice invokes onFiles each time (independent drops)', () => {
    const { controller, onFiles } = makeController()
    const dt = () => makeDataTransfer({ items: [{ kind: 'file', file: file('same.jpg') }] })
    controller.onDrop(makeEvent(dt()).event)
    controller.onDrop(makeEvent(dt()).event)
    expect(onFiles).toHaveBeenCalledTimes(2)
  })

  it('ignores drops (no onFiles) but still prevents default when disabled — covers uploading/at-capacity too', () => {
    const { controller, onFiles, isActive } = makeController({ disabled: true })
    const enter = makeEvent(makeDataTransfer({ items: [{ kind: 'file', file: file('a.jpg') }] }))
    controller.onDragEnter(enter.event)
    expect(isActive()).toBe(false) // no active state while disabled
    const drop = makeEvent(makeDataTransfer({ items: [{ kind: 'file', file: file('a.jpg') }] }))
    controller.onDrop(drop.event)
    expect(drop.preventDefault).toHaveBeenCalled() // still cancels browser navigation
    expect(onFiles).not.toHaveBeenCalled()
  })

  it('ignores an empty drop payload safely', () => {
    const { controller, onFiles } = makeController()
    const { event, preventDefault } = makeEvent(makeDataTransfer({ items: [], files: [] }))
    controller.onDrop(event)
    expect(preventDefault).toHaveBeenCalled()
    expect(onFiles).not.toHaveBeenCalled()
  })

  it('ignores a non-file drag: dragover/enter do not activate and drop yields no files', () => {
    const { controller, onFiles, isActive } = makeController()
    const overText = makeEvent({ types: ['text/plain'] })
    controller.onDragOver(overText.event)
    expect(overText.preventDefault).not.toHaveBeenCalled()
    const enterText = makeEvent({ types: ['text/plain'] })
    controller.onDragEnter(enterText.event)
    expect(isActive()).toBe(false)
    // A stray drop with only string data extracts nothing.
    const drop = makeEvent(makeDataTransfer({ items: [{ kind: 'string' }], types: ['text/plain'] }))
    controller.onDrop(drop.event)
    expect(onFiles).not.toHaveBeenCalled()
  })
})

describe('useMediaDropzone — source guarantees', () => {
  it('adds a window navigation guard and removes it on unmount (no leaked global listeners)', () => {
    expect(useMediaDropzoneSource).toMatch(/window\.addEventListener\('dragover', preventFileNavigation\)/)
    expect(useMediaDropzoneSource).toMatch(/window\.addEventListener\('drop', preventFileNavigation\)/)
    expect(useMediaDropzoneSource).toMatch(/removeEventListener\('dragover', preventFileNavigation\)/)
    expect(useMediaDropzoneSource).toMatch(/removeEventListener\('drop', preventFileNavigation\)/)
  })

  it('contains no media-validation logic — validation stays in the upload components', () => {
    expect(useMediaDropzoneSource).not.toMatch(/resolveClientMediaError|resolveClientDimensionError/)
    expect(useMediaDropzoneSource).not.toMatch(/getClientValidationRule|validateInstagramSelection|validateFacebookSelection/)
    expect(useMediaDropzoneSource).not.toMatch(/mediaApi/)
  })
})
