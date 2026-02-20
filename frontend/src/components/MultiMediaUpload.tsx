import { useState, useRef } from 'react'
import { mediaApi, type MediaType, type ValidationStatus, type MediaValidationError, type MediaValidationWarning, type Platform } from '../api/media'
import { preValidateFile, preValidateImageDimensions, getImageDimensions } from '../constants/mediaValidationRules'
import {
  validateInstagramSelection,
  getInstagramMediaMode,
  getInstagramUploaderLabel,
  getInstagramFormatHint,
} from '../utils/instagramMediaValidation'
import {
  validateFacebookSelection,
  getFacebookMediaMode,
  getFacebookUploaderLabel,
  getFacebookFormatHint,
} from '../utils/facebookMediaValidation'
import type { PlatformId } from '../constants/validationLimits'
import './MultiMediaUpload.css'

export interface UploadedMediaItem {
  id: string
  s3Key: string
  mediaType: MediaType
  fileName: string
  previewUrl: string
  validationStatus: ValidationStatus
  validationErrors: MediaValidationError[]
  validationWarnings: MediaValidationWarning[]
}

interface MultiMediaUploadProps {
  items: UploadedMediaItem[]
  onItemsChange: (items: UploadedMediaItem[]) => void
  onUploadingChange?: (isUploading: boolean) => void
  selectedPlatform?: PlatformId | null
  disabled?: boolean
  maxItems?: number
  minItems?: number
}

const MAX_CAROUSEL_IMAGES = 10

export function MultiMediaUpload({
  items,
  onItemsChange,
  onUploadingChange,
  selectedPlatform,
  disabled = false,
  maxItems = MAX_CAROUSEL_IMAGES,
  minItems = 2,
}: MultiMediaUploadProps) {
  const [uploading, setUploading] = useState(false)
  const [uploadError, setUploadError] = useState<string | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)

  const isInstagram = selectedPlatform === 'instagram'
  const isFacebook = selectedPlatform === 'facebook'

  // Instagram media mode for dynamic labels
  const igMediaMode = isInstagram
    ? getInstagramMediaMode(items.map(i => ({ name: i.fileName, type: i.mediaType === 'Video' ? 'video/mp4' : 'image/jpeg' })))
    : null

  // Facebook media mode for dynamic labels
  const fbMediaMode = isFacebook
    ? getFacebookMediaMode(items.map(i => ({ name: i.fileName, type: i.mediaType === 'Video' ? 'video/mp4' : 'image/jpeg' })))
    : null

  // For Facebook with a video selected, don't allow adding more (FB = 1 video max)
  // For Instagram with videos, allow adding more videos (IG video carousel)
  const fbHasVideo = isFacebook && items.length === 1 && items[0].mediaType === 'Video'
  const canAddMore = fbHasVideo ? false : items.length < maxItems

  const handleFileSelect = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = Array.from(e.target.files || [])
    if (files.length === 0) return

    // Reset file input
    if (fileInputRef.current) fileInputRef.current.value = ''

    // --- Instagram-specific validation via pure function ---
    if (isInstagram) {
      const existingAsInfo = items.map(i => ({
        name: i.fileName,
        type: i.mediaType === 'Video' ? 'video/mp4' : 'image/jpeg',
      }))
      const newAsInfo = files.map(f => ({ name: f.name, type: f.type }))
      const result = validateInstagramSelection(existingAsInfo, newAsInfo)

      if (!result.ok) {
        setUploadError(result.errorMessage)
        return
      }
      if (result.errorMessage) {
        // Partial accept (e.g. truncated to fit 10)
        setUploadError(result.errorMessage)
      } else {
        setUploadError(null)
      }

      // Determine which new files to actually upload based on result.nextFiles
      // nextFiles = existing + accepted new, so accepted new = nextFiles.slice(existing.length)
      const acceptedCount = result.nextFiles.length - items.length
      const filesToUpload = files.slice(0, acceptedCount)

      if (filesToUpload.length === 0) return

      await uploadFiles(filesToUpload)
      return
    }

    // --- Facebook-specific validation via pure function ---
    if (isFacebook) {
      const existingAsInfo = items.map(i => ({
        name: i.fileName,
        type: i.mediaType === 'Video' ? 'video/mp4' : 'image/jpeg',
      }))
      const newAsInfo = files.map(f => ({ name: f.name, type: f.type }))
      const result = validateFacebookSelection(existingAsInfo, newAsInfo)

      if (!result.ok) {
        setUploadError(result.errorMessage)
        return
      }
      if (result.errorMessage) {
        setUploadError(result.errorMessage)
      } else {
        setUploadError(null)
      }

      const acceptedCount = result.nextFiles.length - items.length
      const filesToUpload = files.slice(0, acceptedCount)

      if (filesToUpload.length === 0) return

      await uploadFiles(filesToUpload)
      return
    }

    // --- Other platform validation (original logic) ---
    const remainingSlots = maxItems - items.length
    const filesToUpload = files.slice(0, remainingSlots)

    if (files.length > remainingSlots) {
      setUploadError(`Can only add ${remainingSlots} more image(s). Max ${maxItems} total.`)
    } else {
      setUploadError(null)
    }

    await uploadFiles(filesToUpload)
  }

  const uploadFiles = async (filesToUpload: File[]) => {
    setUploading(true)
    onUploadingChange?.(true)

    const newItems: UploadedMediaItem[] = []
    for (const file of filesToUpload) {
      // Pre-validate
      if (selectedPlatform) {
        const errors = preValidateFile(file, selectedPlatform, 'Feed')
        if (errors.length > 0) {
          setUploadError(`${file.name}: ${errors[0]}`)
          continue
        }
        if (file.type.startsWith('image/')) {
          const dims = await getImageDimensions(file)
          if (dims) {
            const dimErrors = preValidateImageDimensions(dims.width, dims.height, selectedPlatform, 'Feed')
            if (dimErrors.length > 0) {
              setUploadError(`${file.name}: ${dimErrors[0]}`)
              continue
            }
          }
        }
      }

      try {
        // Get upload URL
        const { uploadUrl, s3Key, mediaType } = await mediaApi.generateUploadUrl({
          fileName: file.name,
          contentType: file.type,
        })

        // Upload to S3
        await mediaApi.uploadFile(uploadUrl, file)

        // Create preview
        const previewUrl = await createPreview(file)

        // Validate on server
        let validationStatus: ValidationStatus = 'Pending'
        let validationErrors: MediaValidationError[] = []
        let validationWarnings: MediaValidationWarning[] = []

        if (selectedPlatform) {
          const platformMap: Record<string, Platform> = {
            facebook: 'Facebook',
            instagram: 'Instagram',
            twitter: 'Twitter',
            linkedin: 'LinkedIn',
          }
          try {
            const result = await mediaApi.validateMedia({
              storageKey: s3Key,
              mimeType: file.type,
              platform: platformMap[selectedPlatform] as Platform,
              placement: 'Feed',
            })
            validationStatus = result.status
            validationErrors = result.errors
            validationWarnings = result.warnings
          } catch {
            // Keep as pending
          }
        }

        newItems.push({
          id: crypto.randomUUID(),
          s3Key,
          mediaType: mediaType as MediaType,
          fileName: file.name,
          previewUrl,
          validationStatus,
          validationErrors,
          validationWarnings,
        })
      } catch (err) {
        console.error(`Upload failed for ${file.name}:`, err)
        setUploadError(`Failed to upload ${file.name}`)
      }
    }

    if (newItems.length > 0) {
      onItemsChange([...items, ...newItems])
    }

    setUploading(false)
    onUploadingChange?.(false)
  }

  const createPreview = (file: File): Promise<string> => {
    return new Promise((resolve) => {
      const reader = new FileReader()
      reader.onload = (e) => resolve(e.target?.result as string)
      reader.readAsDataURL(file)
    })
  }

  const handleRemove = (id: string) => {
    onItemsChange(items.filter(item => item.id !== id))
    setUploadError(null)
  }

  const handleMoveUp = (index: number) => {
    if (index === 0) return
    const newItems = [...items]
    ;[newItems[index - 1], newItems[index]] = [newItems[index], newItems[index - 1]]
    onItemsChange(newItems)
  }

  const handleMoveDown = (index: number) => {
    if (index === items.length - 1) return
    const newItems = [...items]
    ;[newItems[index], newItems[index + 1]] = [newItems[index + 1], newItems[index]]
    onItemsChange(newItems)
  }

  const handleClick = () => {
    if (!uploading && !disabled && canAddMore && fileInputRef.current) {
      fileInputRef.current.click()
    }
  }

  const invalidItems = items.filter(item => item.validationStatus === 'Invalid')
  const hasInvalidItems = invalidItems.length > 0
  const itemCount = items.length

  // Determine accepted file types for the <input>
  const getAcceptTypes = (): string => {
    if (isInstagram) {
      // Instagram supports mixed media carousels — always allow both images and videos
      return 'image/jpeg,image/png,video/mp4'
    }
    // Facebook: accept video only when empty; images-only once images exist
    if (isFacebook) {
      if (items.length === 0) return 'image/jpeg,image/png,video/mp4,video/quicktime,video/x-msvideo'
      if (items.some(i => i.mediaType === 'Image')) return 'image/jpeg,image/png'
      return 'image/jpeg,image/png,video/mp4,video/quicktime,video/x-msvideo'
    }
    return 'image/jpeg,image/png'
  }

  // Instagram: dynamic upload text and hint
  const getUploadText = (): string => {
    if (uploading) return 'Uploading...'
    if (isInstagram && igMediaMode) {
      return getInstagramUploaderLabel(igMediaMode, itemCount)
    }
    if (isFacebook && fbMediaMode) {
      return getFacebookUploaderLabel(fbMediaMode, itemCount)
    }
    return `Add Images (2-10 for carousel)`
  }

  const getUploadHint = (): string => {
    if (isInstagram && igMediaMode) {
      return getInstagramFormatHint(igMediaMode)
    }
    if (isFacebook && fbMediaMode) {
      return getFacebookFormatHint(fbMediaMode)
    }
    return 'JPEG, PNG only. Select multiple files.'
  }

  // Dynamic status bar text
  const getStatusText = (): string => {
    if (isInstagram) {
      const imageCount = items.filter(i => i.mediaType === 'Image').length
      const videoCount = items.filter(i => i.mediaType === 'Video').length
      if (imageCount > 0 && videoCount > 0) {
        return `${imageCount} photo${imageCount !== 1 ? 's' : ''} + ${videoCount} video${videoCount !== 1 ? 's' : ''}`
      }
      if (items.every(i => i.mediaType === 'Video')) {
        return `${itemCount} video${itemCount !== 1 ? 's' : ''}`
      }
      return `${itemCount} photo${itemCount !== 1 ? 's' : ''}`
    }
    if (isFacebook) {
      if (itemCount === 1 && items[0].mediaType === 'Video') return '1 video'
      return `${itemCount} photo${itemCount !== 1 ? 's' : ''}`
    }
    return `${itemCount} image${itemCount !== 1 ? 's' : ''}`
  }

  const getStatusBadge = (): string | null => {
    if (itemCount < minItems) return null
    if (isInstagram) {
      const hasImages = items.some(i => i.mediaType === 'Image')
      const hasVideos = items.some(i => i.mediaType === 'Video')
      if (hasImages && hasVideos) return 'Mixed Carousel'
      if (items.every(i => i.mediaType === 'Video')) return 'Video Carousel'
      return 'Carousel'
    }
    if (selectedPlatform === 'facebook') return 'Multi-photo'
    return 'Carousel'
  }

  const getStatusHint = (): string | null => {
    if (itemCount !== 1) return null
    if (isInstagram) {
      if (items[0].mediaType === 'Video') return 'Add more photos or videos for carousel, or publish as Reel'
      return 'Add 1 more photo or video for carousel'
    }
    if (isFacebook) {
      if (items[0].mediaType === 'Video') return 'Will publish as video post'
      return 'Add 1 more for multi-photo'
    }
    return 'Add 1 more for carousel'
  }

  // For video items, show video preview differently
  const isItemVideo = (item: UploadedMediaItem) => item.mediaType === 'Video'

  return (
    <div className="multi-media-upload">
      <input
        ref={fileInputRef}
        type="file"
        accept={getAcceptTypes()}
        onChange={handleFileSelect}
        disabled={uploading || disabled || !canAddMore}
        className="file-input-hidden"
        multiple
      />

      {/* Media grid */}
      {items.length > 0 && (
        <div className="carousel-grid">
          {items.map((item, index) => (
            <div key={item.id} className={`carousel-item ${item.validationStatus === 'Invalid' ? 'invalid' : ''}`}>
              {isItemVideo(item) ? (
                <>
                  <video src={item.previewUrl} className="carousel-thumbnail" muted />
                  <span className="carousel-video-indicator">&#9654;</span>
                </>
              ) : (
                <img src={item.previewUrl} alt={`Image ${index + 1}`} className="carousel-thumbnail" />
              )}
              <div className="carousel-item-overlay">
                <span className="carousel-order">{index + 1}</span>
                <div className="carousel-item-actions">
                  <button
                    type="button"
                    className="carousel-action-btn"
                    onClick={() => handleMoveUp(index)}
                    disabled={index === 0}
                    title="Move up"
                  >
                    ▲
                  </button>
                  <button
                    type="button"
                    className="carousel-action-btn"
                    onClick={() => handleMoveDown(index)}
                    disabled={index === items.length - 1}
                    title="Move down"
                  >
                    ▼
                  </button>
                  <button
                    type="button"
                    className="carousel-action-btn remove"
                    onClick={() => handleRemove(item.id)}
                    title="Remove"
                  >
                    ✕
                  </button>
                </div>
              </div>
              {item.validationStatus === 'Invalid' && (
                <div className="carousel-item-error" title={item.validationErrors.map(e => e.message).join(', ')}>
                  ⚠
                </div>
              )}
            </div>
          ))}

          {/* Add more button */}
          {canAddMore && !disabled && (
            <div
              className={`carousel-add-btn ${uploading ? 'uploading' : ''}`}
              onClick={handleClick}
              role="button"
              tabIndex={0}
              onKeyDown={(e) => e.key === 'Enter' && handleClick()}
            >
              <span className="carousel-add-icon">+</span>
              <span className="carousel-add-text">Add</span>
            </div>
          )}
        </div>
      )}

      {/* Empty state / initial upload */}
      {items.length === 0 && (
        <div
          className={`upload-area ${uploading ? 'uploading' : ''} ${disabled ? 'disabled' : ''}`}
          onClick={handleClick}
          role="button"
          tabIndex={disabled ? -1 : 0}
          onKeyDown={(e) => e.key === 'Enter' && handleClick()}
        >
          <div className="upload-placeholder">
            <span className="upload-icon">+</span>
            <span className="upload-text">{getUploadText()}</span>
            <span className="upload-hint">{getUploadHint()}</span>
          </div>
        </div>
      )}

      {/* Status bar */}
      {items.length > 0 && (
        <div className="carousel-status-bar">
          <span className="carousel-count">{getStatusText()}</span>
          {getStatusBadge() && (
            <span className="carousel-badge">{getStatusBadge()}</span>
          )}
          {getStatusHint() && (
            <span className="carousel-hint">{getStatusHint()}</span>
          )}
          {hasInvalidItems && (
            <span className="carousel-warning">
              {invalidItems.length} item{invalidItems.length !== 1 ? 's' : ''} failed validation
            </span>
          )}
        </div>
      )}

      {/* Validation error details */}
      {hasInvalidItems && (
        <div className="carousel-validation-errors">
          {invalidItems.map(item => (
            <div key={item.id} className="carousel-validation-error-item">
              <span className="carousel-validation-error-name">
                {item.mediaType === 'Video' ? 'Reel' : item.fileName}:
              </span>
              {item.validationErrors.length > 0
                ? item.validationErrors.map((err, i) => (
                    <span key={i} className="carousel-validation-error-msg">{err.message}</span>
                  ))
                : <span className="carousel-validation-error-msg">Validation failed</span>
              }
            </div>
          ))}
        </div>
      )}

      {uploading && (
        <div className="upload-progress">
          <div className="progress-bar" style={{ width: '60%' }} />
        </div>
      )}

      {uploadError && <div className="carousel-upload-error">{uploadError}</div>}
    </div>
  )
}
