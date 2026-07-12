import { useEffect, useMemo, useRef, useState } from 'react'
import type { DragEvent as ReactDragEvent } from 'react'

/**
 * Shared drag-and-drop plumbing for the Schedule Post media uploaders (single + multi).
 *
 * Scope is deliberately narrow (see the task/requirements): this hook only manages the
 * drag-over *state* and *extracts* File objects from a drop. It performs NO media
 * validation — every count/format/size/duration/dimension/aspect/platform rule stays in
 * the upload components, which expose a single `onFiles` entry point that both the file
 * picker and the drop handler funnel through. That keeps the drop path and the
 * click-to-browse path on the exact same upload/validation pipeline.
 *
 * The pure helpers (`createMediaDropController`, `extractFilesFromDataTransfer`,
 * `dragPayloadHasFiles`) are exported so they can be unit-tested without a DOM (the
 * project has no jsdom/testing-library test environment).
 */

/** Minimal structural shape of the drop payload we read. Both the browser's real
 * `DataTransfer` and lightweight test doubles satisfy it. */
export interface DragDataTransferLike {
  files?: ArrayLike<File> | null
  items?: ArrayLike<DataTransferItemLike> | null
  types?: ArrayLike<string> | null
  dropEffect?: string
}

export interface DataTransferItemLike {
  kind: string
  getAsFile?: () => File | null
  webkitGetAsEntry?: () => { isDirectory?: boolean } | null
}

export interface DragEventLike {
  preventDefault: () => void
  dataTransfer: DragDataTransferLike | null
}

/**
 * True when the drag advertises files. The browser hides file contents until the actual
 * drop, but always exposes the `'Files'` type entry during dragenter/dragover — so this
 * is how we tell a file drag from a text/link/element drag. Permissive when the type
 * list is unavailable, so we still block the browser's default open/navigate behaviour.
 */
export function dragPayloadHasFiles(dataTransfer: DragDataTransferLike | null | undefined): boolean {
  const types = dataTransfer?.types
  if (!types) return true
  for (let i = 0; i < types.length; i++) {
    if (types[i] === 'Files') return true
  }
  return false
}

/**
 * Extract real File objects from a drop payload. Directories and non-file entries
 * (dragged text, links, whole folders) are skipped so they never reach the upload
 * pipeline. This is the ONLY file-reading step; ordering matches the payload order,
 * which matches how the file input reports a multi-file selection.
 */
export function extractFilesFromDataTransfer(dataTransfer: DragDataTransferLike | null | undefined): File[] {
  if (!dataTransfer) return []

  // Prefer `items`: it lets us drop directories and non-file kinds up front. `getAsFile`
  // must be called synchronously during the event, which it is here.
  const items = dataTransfer.items
  if (items && items.length > 0) {
    const files: File[] = []
    for (let i = 0; i < items.length; i++) {
      const item = items[i]
      if (!item || item.kind !== 'file') continue
      const entry = item.webkitGetAsEntry ? item.webkitGetAsEntry() : null
      if (entry && entry.isDirectory) continue
      const file = item.getAsFile ? item.getAsFile() : null
      if (file) files.push(file)
    }
    return files
  }

  const list = dataTransfer.files
  return list ? Array.from(list) : []
}

export interface MediaDropController {
  onDragEnter: (event: DragEventLike) => void
  onDragOver: (event: DragEventLike) => void
  onDragLeave: (event: DragEventLike) => void
  onDrop: (event: DragEventLike) => void
  reset: () => void
}

export interface MediaDropControllerCallbacks {
  /** Read the latest disabled flag (disabled, uploading, at-capacity, …). */
  isDisabled: () => boolean
  setDragActive: (active: boolean) => void
  /** Hand the extracted files to the component's existing upload entry point. */
  onFiles: (files: File[]) => void
}

/**
 * Framework-agnostic drag state machine. A depth counter (incremented on dragenter,
 * decremented on dragleave) is what prevents flicker: when the pointer crosses from the
 * drop zone onto a child element the browser fires a `dragleave` (parent) *and* a
 * `dragenter` (child) that both bubble to the zone — the counter nets out and the active
 * state stays on until the drag genuinely leaves (depth returns to 0).
 */
export function createMediaDropController(callbacks: MediaDropControllerCallbacks): MediaDropController {
  let dragDepth = 0

  const deactivate = () => {
    dragDepth = 0
    callbacks.setDragActive(false)
  }

  return {
    onDragEnter(event) {
      if (!dragPayloadHasFiles(event.dataTransfer)) return
      event.preventDefault()
      if (callbacks.isDisabled()) return
      dragDepth += 1
      callbacks.setDragActive(true)
    },
    onDragOver(event) {
      if (!dragPayloadHasFiles(event.dataTransfer)) return
      // Required: without preventDefault on dragover the browser treats the page as a
      // non-drop-target and opens/navigates to the file on release.
      event.preventDefault()
      const dataTransfer = event.dataTransfer
      if (dataTransfer && 'dropEffect' in dataTransfer) {
        dataTransfer.dropEffect = callbacks.isDisabled() ? 'none' : 'copy'
      }
    },
    onDragLeave(event) {
      if (!dragPayloadHasFiles(event.dataTransfer)) return
      if (callbacks.isDisabled()) return
      dragDepth = Math.max(0, dragDepth - 1)
      if (dragDepth === 0) callbacks.setDragActive(false)
    },
    onDrop(event) {
      // Always cancel the browser default (open/navigate), even for disabled or empty
      // drops, then reset the drag state so the zone returns to normal immediately.
      event.preventDefault()
      deactivate()
      if (callbacks.isDisabled()) return
      const files = extractFilesFromDataTransfer(event.dataTransfer)
      if (files.length === 0) return
      callbacks.onFiles(files)
    },
    reset: deactivate,
  }
}

export interface UseMediaDropzoneOptions {
  /** When true the zone ignores drops and never shows the active state (covers the
   * disabled control, an in-flight upload, and a multi-uploader already at capacity). */
  disabled?: boolean
  /** Existing upload entry point shared with the file picker — receives dropped files in
   * payload order and applies all the component's own validation. */
  onFiles: (files: File[]) => void
}

export interface MediaDropzoneHandlers {
  onDragEnter: (event: ReactDragEvent) => void
  onDragOver: (event: ReactDragEvent) => void
  onDragLeave: (event: ReactDragEvent) => void
  onDrop: (event: ReactDragEvent) => void
}

export interface UseMediaDropzoneResult {
  isDragActive: boolean
  dropzoneHandlers: MediaDropzoneHandlers
}

export function useMediaDropzone({ disabled = false, onFiles }: UseMediaDropzoneOptions): UseMediaDropzoneResult {
  const [isDragActive, setIsDragActive] = useState(false)

  // Latest-value refs so the single, stable controller always sees the current disabled
  // flag / onFiles callback without being recreated (which would drop the drag counter).
  const disabledRef = useRef(disabled)
  const onFilesRef = useRef(onFiles)
  useEffect(() => {
    disabledRef.current = disabled
  }, [disabled])
  useEffect(() => {
    onFilesRef.current = onFiles
  }, [onFiles])

  // Create the drag state machine once, in a mount effect rather than during render, so
  // no ref is read (or handed to a function) in the render body. Its callbacks read the
  // latest disabled flag / onFiles through refs, so it never needs to be recreated
  // (recreating would drop the in-flight drag depth counter).
  const controllerRef = useRef<MediaDropController | null>(null)
  useEffect(() => {
    controllerRef.current = createMediaDropController({
      isDisabled: () => disabledRef.current,
      setDragActive: setIsDragActive,
      onFiles: (files) => onFilesRef.current(files),
    })
    return () => {
      controllerRef.current = null
    }
  }, [])

  // Narrowly-scoped navigation guard: while a dropzone is mounted, stop the browser from
  // navigating away when a file is dropped *outside* the intended zone. Only file drags
  // are intercepted, and both listeners are removed on unmount so nothing outlives the
  // component.
  useEffect(() => {
    const preventFileNavigation = (event: DragEvent) => {
      if (dragPayloadHasFiles(event.dataTransfer)) {
        event.preventDefault()
      }
    }
    window.addEventListener('dragover', preventFileNavigation)
    window.addEventListener('drop', preventFileNavigation)
    return () => {
      window.removeEventListener('dragover', preventFileNavigation)
      window.removeEventListener('drop', preventFileNavigation)
    }
  }, [])

  // Stable handlers (created once) that delegate to the controller. Reading the ref
  // inside these event callbacks is fine — refs are only off-limits during render.
  const dropzoneHandlers = useMemo<MediaDropzoneHandlers>(
    () => ({
      onDragEnter: (event) => controllerRef.current?.onDragEnter(event),
      onDragOver: (event) => controllerRef.current?.onDragOver(event),
      onDragLeave: (event) => controllerRef.current?.onDragLeave(event),
      onDrop: (event) => controllerRef.current?.onDrop(event),
    }),
    [],
  )

  // Mask the active state whenever the control is disabled so the highlight never shows
  // for an ignored drag — including the control becoming disabled mid-drag. (The
  // controller already refuses to activate while disabled; this also covers the flip.)
  return { isDragActive: isDragActive && !disabled, dropzoneHandlers }
}
