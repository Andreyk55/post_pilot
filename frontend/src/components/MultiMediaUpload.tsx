import { useState, useRef, useEffect } from 'react'
import { mediaApi, type MediaType, type ValidationStatus, type MediaValidationError, type MediaValidationWarning, type Platform } from '../api/media'
import { getImageDimensions } from '../constants/mediaValidationRules'
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
import { MediaValidationBadge, MediaValidationCard, MediaValidationOverlay } from './MediaValidationStatus'
import { resolveClientMediaError, resolveClientDimensionError } from '../utils/mediaRequirements'
import { aggregateMediaValidationViews } from '../utils/mediaValidationView'
import { getUploadErrorMessage } from '../utils/uploadError'
import { createUploadClientId } from '../utils/uploadClientId'
import './MultiMediaUpload.css'

export interface UploadedMediaItem {
  id: string
  mediaId: string
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

type PendingUploadMediaType = 'image' | 'video' | null

type PendingUpload = {
  id: string
  fileName: string
  mediaType: PendingUploadMediaType
  previewUrl: string
}

const getPendingUploadMediaType = (file: File): PendingUploadMediaType => {
  if (file.type.startsWith('image/')) return 'image'
  if (file.type.startsWith('video/')) return 'video'
  return null
}

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
  const [progress, setProgress] = useState(0)
  const fileInputRef = useRef<HTMLInputElement>(null)

  // Optimistic media tiles for files currently uploading/validating. They keep the
  // selected media visible immediately, with the validating badge layered on top.
  // The previous validation-error panel stays hidden until the new result is
  // finalized so stale errors do not sit under fresh media.
  const [pendingUploads, setPendingUploads] = useState<PendingUpload[]>([])
  const pendingPreviewUrlsRef = useRef<string[]>([])

  const replacePendingUploads = (nextPendingUploads: PendingUpload[]) => {
    pendingPreviewUrlsRef.current.forEach(url => URL.revokeObjectURL(url))
    pendingPreviewUrlsRef.current = nextPendingUploads.map(pending => pending.previewUrl)
    setPendingUploads(nextPendingUploads)
  }

  useEffect(() => () => {
    pendingPreviewUrlsRef.current.forEach(url => URL.revokeObjectURL(url))
    pendingPreviewUrlsRef.current = []
  }, [])

  // Latest committed items, read at upload-completion time so an item removed while
  // a new upload is in flight is never resurrected by the append below. Synced in an
  // effect rather than during render (the only reader runs after async awaits, well
  // after commit, so the timing is equivalent) which also keeps the ref write out of
  // the render path.
  const itemsRef = useRef(items)
  useEffect(() => {
    itemsRef.current = items
  }, [items])

  // Per-component session prefix for upload-ownership keys. Created once via the
  // lazy useState initializer (React calls it a single time) so it is never
  // regenerated on re-render and no impure call runs in the render path.
  const [uploadSessionInstance] = useState(createUploadClientId)
  const uploadSessionCounterRef = useRef(0)
  const activeUploadOwnerKeyRef = useRef<string>('')

  const beginUploadSession = (): string => {
    const uploadOwnerKey = `${uploadSessionInstance}:${++uploadSessionCounterRef.current}`
    activeUploadOwnerKeyRef.current = uploadOwnerKey
    return uploadOwnerKey
  }

  const isStaleUploadOwner = (uploadOwnerKey: string) => activeUploadOwnerKeyRef.current !== uploadOwnerKey

  useEffect(() => {
    return () => {
      activeUploadOwnerKeyRef.current = ''
    }
  }, [])

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

    if (!isFacebook && !isInstagram) {
      setUploadError('Select Facebook or Instagram before uploading media.')
      return
    }

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
    const uploadOwnerKey = beginUploadSession()
    setProgress(0)
    setUploading(true)
    onUploadingChange?.(true)

    // Show the incoming files as pending cards right away and hide any prior
    // validation error while the new media is uploading/validating.
    replacePendingUploads(
      filesToUpload.map(file => ({
        id: createUploadClientId(),
        fileName: file.name,
        mediaType: getPendingUploadMediaType(file),
        previewUrl: URL.createObjectURL(file),
      })),
    )

    const newItems: UploadedMediaItem[] = []
    for (const [fileIndex, file] of filesToUpload.entries()) {
      // Pre-validate with friendly, specific copy (shared with the single-media
      // uploader) so a client rejection never bottoms out at a generic message.
      if (selectedPlatform) {
        const typeOrSizeError = resolveClientMediaError(file, selectedPlatform, 'Feed')
        if (isStaleUploadOwner(uploadOwnerKey)) return
        if (typeOrSizeError) {
          setUploadError(`${file.name}: ${typeOrSizeError}`)
          setProgress(0)
          continue
        }
        if (file.type.startsWith('image/')) {
          const dims = await getImageDimensions(file)
          if (isStaleUploadOwner(uploadOwnerKey)) return
          if (dims) {
            const dimError = resolveClientDimensionError(dims.width, dims.height, selectedPlatform, 'Feed')
            if (dimError) {
              setUploadError(`${file.name}: ${dimError}`)
              setProgress(0)
              continue
            }
          }
        }
      }

      try {
        // Step 1: server issues a presigned PUT URL and creates a Media row (PendingUpload).
        const { uploadUrl, mediaId, mediaType, previewUrl } = await mediaApi.initUpload({
          fileName: file.name,
          contentType: file.type,
          sizeBytes: file.size,
          platform: isFacebook ? 'Facebook' : 'Instagram',
        })
        if (isStaleUploadOwner(uploadOwnerKey)) return

        // Step 2: client uploads bytes directly to object storage (or local endpoint in dev).
        await mediaApi.uploadFile(uploadUrl, file, (progressPercent) => {
          if (!isStaleUploadOwner(uploadOwnerKey)) {
            const currentFileProgress = Math.max(0, Math.min(100, progressPercent)) / 100
            setProgress(Math.round(((fileIndex + currentFileProgress) / filesToUpload.length) * 100))
          }
        })
        if (isStaleUploadOwner(uploadOwnerKey)) return

        // Step 3: server verifies the object landed in storage and flips Media row to Uploaded.
        const completeResult = await mediaApi.completeUpload({ mediaId })
        if (isStaleUploadOwner(uploadOwnerKey)) return

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
              mediaId,
              mimeType: file.type,
              platform: platformMap[selectedPlatform] as Platform,
              placement: 'Feed',
            })
            if (isStaleUploadOwner(uploadOwnerKey)) return
            validationStatus = result.status
            validationErrors = result.errors
            validationWarnings = result.warnings
          } catch {
            // Keep as pending
          }
        }

        newItems.push({
          id: createUploadClientId(),
          mediaId,
          mediaType: mediaType as MediaType,
          fileName: file.name,
          previewUrl: completeResult.previewUrl || previewUrl,
          validationStatus,
          validationErrors,
          validationWarnings,
        })
      } catch (err) {
        if (isStaleUploadOwner(uploadOwnerKey)) return
        console.error(`Upload failed for ${file.name}:`, err)
        // Preserve the server/API/network message when present; only fall back to a
        // per-file generic when there is genuinely no detail.
        setUploadError(getUploadErrorMessage(err, `Couldn't upload ${file.name}. Please try again.`))
        setProgress(0)
      }
    }

    if (isStaleUploadOwner(uploadOwnerKey)) return

    if (newItems.length > 0) {
      // Merge against the latest items (not the closure snapshot) so a removal that
      // happened during this upload is respected instead of being clobbered.
      onItemsChange([...itemsRef.current, ...newItems])
      setProgress(100)
    } else {
      setProgress(0)
    }

    replacePendingUploads([])
    setUploading(false)
    onUploadingChange?.(false)
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

  const itemCount = items.length

  // One normalized view for every item — the same shared card the single-media
  // uploader renders, so warnings get a visible explanation (not just a bare badge)
  // and Facebook/Instagram look identical. Worst status wins; each message is
  // prefixed with its item label.
  const aggregatedValidationView = aggregateMediaValidationViews(
    items.map((item, index) => ({
      status: item.validationStatus,
      errors: item.validationErrors,
      warnings: item.validationWarnings,
      label: `${item.mediaType === 'Video' ? 'Video' : 'Image'} ${index + 1}`,
    })),
  )

  // While media is uploading/validating we suppress the previous validation
  // results so a stale error never sits below a freshly added (pending) item.
  const isUploadingMedia = uploading || pendingUploads.length > 0
  const showValidationCard = !isUploadingMedia

  // Determine accepted file types for the <input>
  const getAcceptTypes = (): string => {
    if (isInstagram) {
      // Instagram supports mixed media carousels — images (JPG/PNG; PNG is auto-converted
      // to JPEG by the backend) + video (MP4/MOV).
      return 'image/jpeg,image/png,video/mp4,video/quicktime'
    }
    // Facebook: accept video only when empty; images-only once images exist
    if (isFacebook) {
      if (items.length === 0) return 'image/jpeg,image/png,video/mp4,video/quicktime'
      if (items.some(i => i.mediaType === 'Image')) return 'image/jpeg,image/png'
      return 'image/jpeg,image/png,video/mp4,video/quicktime'
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
      {(items.length > 0 || pendingUploads.length > 0) && (
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
              {/* Shared validation badge — same Validating/Valid/Invalid/Warning
                  visual as the single-media (Story) uploader. The full error text
                  stays available on hover via the tooltip. */}
              <MediaValidationBadge
                status={item.validationStatus}
                title={item.validationErrors.map(e => e.message).join(', ') || undefined}
                className="carousel-item-badge"
              />
            </div>
          ))}

          {/* Pending (uploading/validating) media preview cards */}
          {pendingUploads.map(pending => (
            <div key={pending.id} className="carousel-item pending">
              {pending.mediaType === 'video' ? (
                <>
                  <video
                    src={pending.previewUrl}
                    className="carousel-thumbnail"
                    muted
                    playsInline
                    preload="metadata"
                  />
                  <span className="carousel-video-indicator" aria-hidden="true">&#9654;</span>
                </>
              ) : pending.mediaType === 'image' ? (
                <img src={pending.previewUrl} alt={pending.fileName} className="carousel-thumbnail" />
              ) : (
                <div className="carousel-thumbnail carousel-thumbnail--pending" />
              )}
              <div className="carousel-item-filename">{pending.fileName}</div>
              <MediaValidationOverlay show />
            </div>
          ))}

          {/* Add more button */}
          {canAddMore && !disabled && !isUploadingMedia && (
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
      {items.length === 0 && pendingUploads.length === 0 && (
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
        </div>
      )}

      {/* Shared validation card — identical ready/warning/error look to the
          single-media uploader, now surfacing warnings (with an explanation) too.
          Hidden while a new upload is in flight so a stale result never sits below a
          freshly added (pending) item. */}
      {showValidationCard && <MediaValidationCard view={aggregatedValidationView} />}

      {uploading && (
        <div className="upload-progress">
          <div className="progress-bar" style={{ width: `${progress}%` }} />
        </div>
      )}

      {uploadError && <div className="carousel-upload-error">{uploadError}</div>}
    </div>
  )
}
