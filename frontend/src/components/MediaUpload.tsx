import { useState, useRef, useEffect } from 'react'
import { mediaApi, type MediaType, type ValidationStatus, type MediaValidationError, type MediaValidationWarning, type Platform, type Placement } from '../api/media'
import { preValidateFile, preValidateImageDimensions, getImageDimensions, getClientValidationRule } from '../constants/mediaValidationRules'
import type { PlatformId } from '../constants/validationLimits'
import './MediaUpload.css'

interface MediaUploadProps {
  onUploadComplete: (storageKey: string, mediaType: MediaType) => void
  onUploadError: (error: string) => void
  onClear: () => void
  onUploadingChange?: (isUploading: boolean) => void
  onValidationChange?: (status: ValidationStatus, errors: MediaValidationError[], warnings: MediaValidationWarning[]) => void
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
  const [uploadedStorageKey, setUploadedStorageKey] = useState<string | null>(null)
  const [uploadedMimeType, setUploadedMimeType] = useState<string | null>(null)
  const [validationStatus, setValidationStatus] = useState<ValidationStatus>('Pending')
  const [validationErrors, setValidationErrors] = useState<MediaValidationError[]>([])
  const [validationWarnings, setValidationWarnings] = useState<MediaValidationWarning[]>([])
  const [validating, setValidating] = useState(false)

  const fileInputRef = useRef<HTMLInputElement>(null)
  const videoRef = useRef<HTMLVideoElement>(null)

  // Validate/re-validate when platform changes (including first selection after upload)
  useEffect(() => {
    if (uploadedStorageKey && uploadedMimeType && selectedPlatform) {
      revalidateMedia()
    }
  }, [selectedPlatform])

  const revalidateMedia = async () => {
    if (!uploadedStorageKey || !uploadedMimeType || !selectedPlatform) return

    try {
      setValidating(true)
      setValidationStatus('Pending')

      const platformMap: Record<string, Platform> = {
        facebook: 'Facebook',
        instagram: 'Instagram',
        twitter: 'Twitter',
        linkedin: 'LinkedIn',
      }

      const result = await mediaApi.validateMedia({
        storageKey: uploadedStorageKey,
        mimeType: uploadedMimeType,
        platform: platformMap[selectedPlatform] as Platform,
        placement: placement,
      })

      setValidationStatus(result.status)
      setValidationErrors(result.errors)
      setValidationWarnings(result.warnings)
      onValidationChange?.(result.status, result.errors, result.warnings)
    } catch (err) {
      console.error('Re-validation failed:', err)
    } finally {
      setValidating(false)
    }
  }

  const getMediaTypeFromMime = (type: string): 'image' | 'video' | null => {
    if (type.startsWith('image/')) return 'image'
    if (type.startsWith('video/')) return 'video'
    return null
  }

  const validateFile = async (file: File): Promise<string | null> => {
    // Platform-specific pre-validation if platform is selected
    if (selectedPlatform) {
      const errors = preValidateFile(file, selectedPlatform, placement)
      if (errors.length > 0) {
        return errors[0]
      }

      // For images, also check dimensions
      if (file.type.startsWith('image/')) {
        const dims = await getImageDimensions(file)
        if (dims) {
          const dimErrors = preValidateImageDimensions(dims.width, dims.height, selectedPlatform, placement)
          if (dimErrors.length > 0) {
            return dimErrors[0]
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
    const file = e.target.files?.[0]
    if (!file) return

    // Reset validation state
    setValidationStatus('Pending')
    setValidationErrors([])
    setValidationWarnings([])
    setUploadedStorageKey(null)
    setUploadedMimeType(null)

    const error = await validateFile(file)
    if (error) {
      onUploadError(error)
      return
    }

    const type = getMediaTypeFromMime(file.type)!
    setMediaType(type)
    setFileName(file.name)

    // Show preview
    if (type === 'image') {
      const reader = new FileReader()
      reader.onload = (e) => setPreview(e.target?.result as string)
      reader.readAsDataURL(file)
    } else {
      const objectUrl = URL.createObjectURL(file)
      setPreview(objectUrl)
    }

    try {
      setUploading(true)
      onUploadingChange?.(true)
      setProgress(10)

      // Step 1: server issues a presigned PUT URL and creates a Media row (PendingUpload).
      const { uploadUrl, storageKey, mediaId, mediaType: returnedMediaType } = await mediaApi.initUpload({
        fileName: file.name,
        contentType: file.type,
        sizeBytes: file.size,
      })
      setProgress(20)

      // Step 2: client uploads bytes directly to object storage (or local endpoint in dev).
      await mediaApi.uploadFile(uploadUrl, file, (progressPercent) => {
        setProgress(20 + Math.round(progressPercent * 0.5))
      })
      setProgress(75)

      // Step 3: server verifies the object landed in storage and flips Media row to Uploaded.
      await mediaApi.completeUpload({ mediaId })
      setProgress(85)

      // Store upload info for validation
      setUploadedStorageKey(storageKey)
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
            storageKey: storageKey,
            mimeType: file.type,
            platform: platformMap[selectedPlatform] as Platform,
            placement: placement,
          })
          setValidationStatus(validationResult.status)
          setValidationErrors(validationResult.errors)
          setValidationWarnings(validationResult.warnings)
          onValidationChange?.(validationResult.status, validationResult.errors, validationResult.warnings)
        } catch (err) {
          console.error('Validation failed:', err)
          // Keep upload but show as pending
        } finally {
          setValidating(false)
        }
      }

      setProgress(100)
      onUploadComplete(storageKey, returnedMediaType as MediaType)
    } catch (err) {
      console.error('Upload failed:', err)
      onUploadError(err instanceof Error ? err.message : 'Failed to upload file. Please try again.')
      setPreview(null)
      setFileName(null)
      setMediaType(null)
    } finally {
      setUploading(false)
      onUploadingChange?.(false)
    }
  }

  const handleClear = () => {
    if (mediaType === 'video' && preview) {
      URL.revokeObjectURL(preview)
    }
    setPreview(null)
    setFileName(null)
    setMediaType(null)
    setProgress(0)
    setUploadedStorageKey(null)
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

  const getValidationBadge = () => {
    if (validating) {
      return <span className="validation-badge validating">Validating...</span>
    }

    switch (validationStatus) {
      case 'Valid':
        return <span className="validation-badge valid">Valid</span>
      case 'Invalid':
        return <span className="validation-badge invalid">Invalid</span>
      case 'Warning':
        return <span className="validation-badge warning">Warning</span>
      case 'Pending':
      default:
        return selectedPlatform ? <span className="validation-badge pending">Pending</span> : null
    }
  }

  // Get dynamic hints based on selected platform
  const getUploadHints = () => {
    if (selectedPlatform) {
      const imageRule = getClientValidationRule(selectedPlatform, placement, 'Image')
      const videoRule = getClientValidationRule(selectedPlatform, placement, 'Video')

      const imageMaxMB = imageRule ? Math.round(imageRule.maxBytes / (1024 * 1024)) : DEFAULT_MAX_IMAGE_SIZE_MB
      const videoMaxMB = videoRule ? Math.round(videoRule.maxBytes / (1024 * 1024)) : DEFAULT_MAX_VIDEO_SIZE_MB

      return (
        <>
          <span className="upload-hint">Images: max {imageMaxMB}MB</span>
          <span className="upload-hint">Videos: max {videoMaxMB}MB</span>
        </>
      )
    }

    return (
      <>
        <span className="upload-hint">Images: JPG, PNG, GIF (max {DEFAULT_MAX_IMAGE_SIZE_MB}MB)</span>
        <span className="upload-hint">Videos: MP4 (max {DEFAULT_MAX_VIDEO_SIZE_MB}MB)</span>
      </>
    )
  }

  return (
    <div className="media-upload">
      <input
        ref={fileInputRef}
        type="file"
        accept="image/*,video/*"
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
                {getValidationBadge()}
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
          </div>

          {/* Validation errors - outside preview for proper display */}
          {validationErrors.length > 0 && (
            <div className="validation-errors">
              {validationErrors.map((err, i) => (
                <div key={i} className="validation-error">
                  {err.message}
                </div>
              ))}
            </div>
          )}

          {/* Validation warnings - outside preview for proper display */}
          {validationWarnings.length > 0 && validationErrors.length === 0 && (
            <div className="validation-warnings">
              {validationWarnings.map((warn, i) => (
                <div key={i} className="validation-warning">
                  {warn.message}
                </div>
              ))}
            </div>
          )}
        </>
      ) : (
        <div
          className={`upload-area ${uploading ? 'uploading' : ''} ${disabled ? 'disabled' : ''}`}
          onClick={handleClick}
          role="button"
          tabIndex={disabled ? -1 : 0}
          onKeyDown={(e) => e.key === 'Enter' && handleClick()}
        >
          <div className="upload-placeholder">
            <span className="upload-icon">+</span>
            <span className="upload-text">
              {uploading ? 'Uploading...' : 'Add Media'}
            </span>
            {getUploadHints()}
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
