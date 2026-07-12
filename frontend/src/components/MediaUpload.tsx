import { useState, useRef, useEffect } from 'react'
import { mediaApi, type MediaType, type ValidationStatus, type MediaValidationError, type MediaValidationWarning, type Platform, type Placement } from '../api/media'
import { getImageDimensions } from '../constants/mediaValidationRules'
import type { PlatformId } from '../constants/validationLimits'
import { MediaValidationBadge, MediaValidationCard, MediaValidationOverlay } from './MediaValidationStatus'
import { resolveClientMediaError, resolveClientDimensionError } from '../utils/mediaRequirements'
import { resolveMediaValidationView } from '../utils/mediaValidationView'
import { getUploadErrorMessage } from '../utils/uploadError'
import { createUploadClientId } from '../utils/uploadClientId'
import { useMediaDropzone } from '../hooks/useMediaDropzone'
import './MediaUpload.css'

interface MediaUploadProps {
  onUploadComplete: (mediaId: string, previewUrl: string, mediaType: MediaType) => void
  onUploadError: (error: string) => void
  onClear: () => void
  onUploadingChange?: (isUploading: boolean) => void
  onValidationChange?: (status: ValidationStatus, errors: MediaValidationError[], warnings: MediaValidationWarning[], ownerKey: string) => void
  selectedPlatform?: PlatformId | null
  placement?: Placement
  /** When true, disables all upload functionality (no connected account/page) */
  disabled?: boolean
}

// Default generic limits (used when no platform-specific rules exist)
const DEFAULT_MAX_IMAGE_SIZE_MB = 20
const DEFAULT_MAX_VIDEO_SIZE_MB = 200

export function MediaUpload({
  onUploadComplete,
  onUploadError,
  onClear,
  onUploadingChange,
  onValidationChange,
  selectedPlatform,
  placement = 'Feed',
  disabled = false,
}: MediaUploadProps) {
  const [uploading, setUploading] = useState(false)
  const [preview, setPreview] = useState<string | null>(null)
  const [progress, setProgress] = useState(0)
  const [fileName, setFileName] = useState<string | null>(null)
  const [mediaType, setMediaType] = useState<'image' | 'video' | null>(null)
  const [uploadedMediaId, setUploadedMediaId] = useState<string | null>(null)
  const [uploadedMimeType, setUploadedMimeType] = useState<string | null>(null)
  const [validationStatus, setValidationStatus] = useState<ValidationStatus>('Pending')
  const [validationErrors, setValidationErrors] = useState<MediaValidationError[]>([])
  const [validationWarnings, setValidationWarnings] = useState<MediaValidationWarning[]>([])
  const [validating, setValidating] = useState(false)

  const fileInputRef = useRef<HTMLInputElement>(null)
  const videoRef = useRef<HTMLVideoElement>(null)

  // Identifies the in-flight upload/validation owner. Each new file selection or
  // re-validation begins a fresh owner key; late progress/completion/error events
  // from an older owner are dropped instead of writing over the current media.
  // The per-component prefix is created once via the lazy useState initializer
  // (React calls it a single time) so no impure call runs in the render path.
  const [sessionInstance] = useState(createUploadClientId)
  const sessionCounterRef = useRef(0)
  const activeUploadOwnerKeyRef = useRef<string>('')

  const beginUploadSession = (): string => {
    const uploadOwnerKey = `${sessionInstance}:${++sessionCounterRef.current}`
    activeUploadOwnerKeyRef.current = uploadOwnerKey
    return uploadOwnerKey
  }

  const isStaleUploadOwner = (uploadOwnerKey: string) => activeUploadOwnerKeyRef.current !== uploadOwnerKey

  const setNeutralValidationState = (ownerKey: string) => {
    setValidationStatus('Pending')
    setValidationErrors([])
    setValidationWarnings([])
    onValidationChange?.('Pending', [], [], ownerKey)
  }

  // Validate/re-validate when platform changes (including first selection after upload)
  useEffect(() => {
    if (uploadedMediaId && uploadedMimeType && selectedPlatform) {
      revalidateMedia()
    }
  }, [selectedPlatform])

  useEffect(() => {
    return () => {
      activeUploadOwnerKeyRef.current = ''
    }
  }, [])

  const revalidateMedia = async () => {
    if (!uploadedMediaId || !uploadedMimeType || !selectedPlatform) return

    const uploadOwnerKey = beginUploadSession()
    try {
      setValidating(true)
      setNeutralValidationState(uploadOwnerKey)

      const platformMap: Record<string, Platform> = {
        facebook: 'Facebook',
        instagram: 'Instagram',
        twitter: 'Twitter',
        linkedin: 'LinkedIn',
      }

      const result = await mediaApi.validateMedia({
        mediaId: uploadedMediaId,
        mimeType: uploadedMimeType,
        platform: platformMap[selectedPlatform] as Platform,
        placement: placement,
      })

      // A newer upload/re-validation superseded this one — ignore the stale result.
      if (isStaleUploadOwner(uploadOwnerKey)) return

      setValidationStatus(result.status)
      setValidationErrors(result.errors)
      setValidationWarnings(result.warnings)
      onValidationChange?.(result.status, result.errors, result.warnings, uploadOwnerKey)
    } catch (err) {
      console.error('Re-validation failed:', err)
    } finally {
      if (!isStaleUploadOwner(uploadOwnerKey)) {
        setValidating(false)
      }
    }
  }

  const getMediaTypeFromMime = (type: string): 'image' | 'video' | null => {
    if (type.startsWith('image/')) return 'image'
    if (type.startsWith('video/')) return 'video'
    return null
  }

  const validateFile = async (file: File): Promise<string | null> => {
    // Platform-specific pre-validation if platform is selected. Friendly, specific
    // copy ("Story media should be vertical 9:16.", "Images must be JPG or PNG.")
    // so a client rejection never bottoms out at a generic message.
    if (selectedPlatform) {
      const typeOrSizeError = resolveClientMediaError(file, selectedPlatform, placement)
      if (typeOrSizeError) {
        return typeOrSizeError
      }

      // For images, also check dimensions/aspect ratio.
      if (file.type.startsWith('image/')) {
        const dims = await getImageDimensions(file)
        if (dims) {
          const dimError = resolveClientDimensionError(dims.width, dims.height, selectedPlatform, placement)
          if (dimError) {
            return dimError
          }
        }
      }
    } else {
      // Fallback to generic validation
      const type = getMediaTypeFromMime(file.type)
      if (!type) {
        return 'Invalid file type. Please upload an image or video.'
      }

      const maxBytes = type === 'image'
        ? DEFAULT_MAX_IMAGE_SIZE_MB * 1024 * 1024
        : DEFAULT_MAX_VIDEO_SIZE_MB * 1024 * 1024

      if (file.size > maxBytes) {
        const maxMB = type === 'image' ? DEFAULT_MAX_IMAGE_SIZE_MB : DEFAULT_MAX_VIDEO_SIZE_MB
        return `File too large. Maximum size is ${maxMB}MB.`
      }
    }

    return null
  }

  const handleFileSelect = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = Array.from(e.target.files ?? [])
    // Preserve the original behaviour: after a platform-gate rejection, clear the picker
    // so the same file can be re-selected once a platform is chosen.
    const invalidPlatform = selectedPlatform !== 'facebook' && selectedPlatform !== 'instagram'
    await handleFiles(files)
    if (files.length > 0 && invalidPlatform) e.target.value = ''
  }

  // Single shared entry point for the file picker and drag-and-drop. The dropzone hook
  // only extracts files; all validation/upload rules live below so both paths behave
  // identically.
  const handleFiles = async (files: File[]) => {
    if (files.length === 0) return

    if (selectedPlatform !== 'facebook' && selectedPlatform !== 'instagram') {
      onUploadError('Select Facebook or Instagram before uploading media.')
      return
    }

    // Single-media surface: exactly one file is supported. If several are dropped,
    // surface an error through the existing channel instead of silently uploading one.
    if (files.length > 1) {
      onUploadError('You can add one photo or video here. Please drop a single file.')
      return
    }

    await uploadSingleFile(files[0])
  }

  const uploadSingleFile = async (file: File) => {
    // Start a fresh validation session and clear any result from the previously
    // selected media *before* the new file validates. This drops the old error
    // panel synchronously, the instant a new upload starts — not after it finishes.
    const uploadOwnerKey = beginUploadSession()
    setProgress(0)
    setNeutralValidationState(uploadOwnerKey)
    setUploadedMediaId(null)
    setUploadedMimeType(null)

    const error = await validateFile(file)
    if (isStaleUploadOwner(uploadOwnerKey)) return

    if (error) {
      setProgress(0)
      onUploadError(error)
      return
    }

    const type = getMediaTypeFromMime(file.type)!
    setMediaType(type)
    setFileName(file.name)

    // Show preview
    if (type === 'image') {
      const reader = new FileReader()
      reader.onload = (e) => {
        if (!isStaleUploadOwner(uploadOwnerKey)) {
          setPreview(e.target?.result as string)
        }
      }
      reader.readAsDataURL(file)
    } else {
      const objectUrl = URL.createObjectURL(file)
      setPreview(objectUrl)
    }

    try {
      setUploading(true)
      onUploadingChange?.(true)
      setProgress(0)

      // Step 1: server issues a presigned PUT URL and creates a Media row (PendingUpload).
      const { uploadUrl, mediaId, mediaType: returnedMediaType, previewUrl } = await mediaApi.initUpload({
        fileName: file.name,
        contentType: file.type,
        sizeBytes: file.size,
        platform: selectedPlatform === 'facebook' ? 'Facebook' : 'Instagram',
      })
      if (isStaleUploadOwner(uploadOwnerKey)) return

      // Step 2: client uploads bytes directly to object storage (or local endpoint in dev).
      await mediaApi.uploadFile(uploadUrl, file, (progressPercent) => {
        if (!isStaleUploadOwner(uploadOwnerKey)) {
          setProgress(Math.max(0, Math.min(100, Math.round(progressPercent))))
        }
      })
      if (isStaleUploadOwner(uploadOwnerKey)) return

      // Step 3: server verifies the object landed in storage and flips Media row to Uploaded.
      const completeResult = await mediaApi.completeUpload({ mediaId })
      if (isStaleUploadOwner(uploadOwnerKey)) return
      setProgress(100)

      // Store upload info for validation
      setUploadedMediaId(mediaId)
      setUploadedMimeType(file.type)

      // If platform was selected, trigger validation
      if (selectedPlatform) {
        setValidating(true)
        try {
          const platformMap: Record<string, Platform> = {
            facebook: 'Facebook',
            instagram: 'Instagram',
            twitter: 'Twitter',
            linkedin: 'LinkedIn',
          }

          const validationResult = await mediaApi.validateMedia({
            mediaId,
            mimeType: file.type,
            platform: platformMap[selectedPlatform] as Platform,
            placement: placement,
          })
          // A newer upload superseded this one — ignore the stale result.
          if (!isStaleUploadOwner(uploadOwnerKey)) {
            setValidationStatus(validationResult.status)
            setValidationErrors(validationResult.errors)
            setValidationWarnings(validationResult.warnings)
            onValidationChange?.(validationResult.status, validationResult.errors, validationResult.warnings, uploadOwnerKey)
          }
        } catch (err) {
          console.error('Validation failed:', err)
          // Keep upload but show as pending
        } finally {
          if (!isStaleUploadOwner(uploadOwnerKey)) {
            setValidating(false)
          }
        }
      }

      if (isStaleUploadOwner(uploadOwnerKey)) return
      onUploadComplete(mediaId, completeResult.previewUrl || previewUrl, returnedMediaType as MediaType)
    } catch (err) {
      if (isStaleUploadOwner(uploadOwnerKey)) return
      console.error('Upload failed:', err)
      onUploadError(getUploadErrorMessage(err))
      setProgress(0)
      setPreview(null)
      setFileName(null)
      setMediaType(null)
    } finally {
      if (!isStaleUploadOwner(uploadOwnerKey)) {
        setUploading(false)
        onUploadingChange?.(false)
      }
    }
  }

  const handleClear = () => {
    activeUploadOwnerKeyRef.current = ''
    if (mediaType === 'video' && preview) {
      URL.revokeObjectURL(preview)
    }
    setPreview(null)
    setFileName(null)
    setMediaType(null)
    setProgress(0)
    setUploadedMediaId(null)
    setUploadedMimeType(null)
    setValidationStatus('Pending')
    setValidationErrors([])
    setValidationWarnings([])
    if (fileInputRef.current) {
      fileInputRef.current.value = ''
    }
    onClear()
  }

  const handleClick = () => {
    if (!uploading && !disabled && fileInputRef.current) {
      fileInputRef.current.click()
    }
  }

  const handleKeyActivate = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault()
      handleClick()
    }
  }

  // Drag-and-drop shares the exact same handleFiles entry point as the file picker. It
  // is disabled while an upload is in flight (and when the control itself is disabled),
  // so drops during those states are ignored and the active visual never appears.
  const { isDragActive, dropzoneHandlers } = useMediaDropzone({
    disabled: disabled || uploading,
    onFiles: handleFiles,
  })

  const showValidationOverlay = !!selectedPlatform && (uploading || validating)

  return (
    <div className="media-upload">
      <input
        ref={fileInputRef}
        type="file"
        accept="image/jpeg,image/png,video/mp4,video/quicktime"
        onChange={handleFileSelect}
        disabled={uploading || disabled}
        className="file-input-hidden"
      />

      {preview ? (
        <>
          <div className={`media-preview ${mediaType === 'video' ? 'video-preview' : 'image-preview'}`}>
            {mediaType === 'image' ? (
              <img src={preview} alt="Upload preview" />
            ) : (
              <video
                ref={videoRef}
                src={preview}
                controls
                muted
                playsInline
              />
            )}
            <div className="preview-overlay">
              <div className="preview-info">
                <span className={`media-type-badge ${mediaType}`}>
                  {mediaType === 'image' ? 'Photo' : 'Video'}
                </span>
                <span className="preview-filename">{fileName}</span>
              </div>
              <button
                type="button"
                className="clear-btn"
                onClick={handleClear}
                disabled={uploading}
              >
                Remove
              </button>
            </div>
            <MediaValidationBadge
              status={validationStatus}
              showPending={!!selectedPlatform && !showValidationOverlay}
              className="media-preview-validation-badge"
            />
            <MediaValidationOverlay show={showValidationOverlay} />
          </div>

          {/* Shared validation card — same ready/warning/error look as every other
              media surface. Driven by the normalized view (errors take precedence over
              warnings; warnings render as non-blocking recommendations). */}
          <MediaValidationCard
            view={resolveMediaValidationView(validationStatus, validationErrors, validationWarnings, { validating })}
          />
        </>
      ) : (
        <div
          className={`upload-area ${uploading ? 'uploading' : ''} ${disabled ? 'disabled' : ''} ${isDragActive ? 'drag-active' : ''}`}
          onClick={handleClick}
          role="button"
          tabIndex={disabled ? -1 : 0}
          onKeyDown={handleKeyActivate}
          {...dropzoneHandlers}
        >
          <div className="upload-placeholder">
            <span className="upload-icon">+</span>
            {uploading ? (
              <span className="upload-text">Uploading...</span>
            ) : isDragActive ? (
              <span className="upload-text">Drop files here</span>
            ) : (
              <>
                <span className="upload-text">Drag &amp; drop files here</span>
                <span className="upload-separator">or</span>
                <span className="upload-browse">Browse files</span>
              </>
            )}
          </div>
        </div>
      )}

      {uploading && (
        <div className="upload-progress">
          <div className="progress-bar" style={{ width: `${progress}%` }} />
        </div>
      )}
    </div>
  )
}
