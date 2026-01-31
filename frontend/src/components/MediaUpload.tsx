import { useState, useRef } from 'react'
import { mediaApi, type MediaType } from '../api/media'
import './MediaUpload.css'

interface MediaUploadProps {
  onUploadComplete: (s3Key: string, mediaType: MediaType) => void
  onUploadError: (error: string) => void
  onClear: () => void
  onUploadingChange?: (isUploading: boolean) => void
}

const ALLOWED_IMAGE_TYPES = ['image/jpeg', 'image/png', 'image/gif']
const ALLOWED_VIDEO_TYPES = ['video/mp4']
const ALLOWED_TYPES = [...ALLOWED_IMAGE_TYPES, ...ALLOWED_VIDEO_TYPES]

const MAX_IMAGE_SIZE_MB = 20
const MAX_VIDEO_SIZE_MB = 200
const MAX_IMAGE_SIZE_BYTES = MAX_IMAGE_SIZE_MB * 1024 * 1024
const MAX_VIDEO_SIZE_BYTES = MAX_VIDEO_SIZE_MB * 1024 * 1024

export function MediaUpload({ onUploadComplete, onUploadError, onClear, onUploadingChange }: MediaUploadProps) {
  const [uploading, setUploading] = useState(false)
  const [preview, setPreview] = useState<string | null>(null)
  const [progress, setProgress] = useState(0)
  const [fileName, setFileName] = useState<string | null>(null)
  const [mediaType, setMediaType] = useState<'image' | 'video' | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const videoRef = useRef<HTMLVideoElement>(null)

  const getMediaType = (type: string): 'image' | 'video' | null => {
    if (ALLOWED_IMAGE_TYPES.includes(type)) return 'image'
    if (ALLOWED_VIDEO_TYPES.includes(type)) return 'video'
    return null
  }

  const validateFile = (file: File): string | null => {
    const type = getMediaType(file.type)
    if (!type) {
      return 'Invalid file type. Please upload an image (JPG, PNG, GIF) or video (MP4).'
    }

    if (type === 'image' && file.size > MAX_IMAGE_SIZE_BYTES) {
      return `Image too large. Maximum size is ${MAX_IMAGE_SIZE_MB}MB.`
    }

    if (type === 'video' && file.size > MAX_VIDEO_SIZE_BYTES) {
      return `Video too large. Maximum size is ${MAX_VIDEO_SIZE_MB}MB.`
    }

    return null
  }

  const handleFileSelect = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file) return

    const error = validateFile(file)
    if (error) {
      onUploadError(error)
      return
    }

    const type = getMediaType(file.type)!
    setMediaType(type)
    setFileName(file.name)

    // Show preview
    if (type === 'image') {
      const reader = new FileReader()
      reader.onload = (e) => setPreview(e.target?.result as string)
      reader.readAsDataURL(file)
    } else {
      // For video, create object URL for preview
      const objectUrl = URL.createObjectURL(file)
      setPreview(objectUrl)
    }

    try {
      setUploading(true)
      onUploadingChange?.(true)
      setProgress(10)

      // Get pre-signed upload URL
      const { uploadUrl, s3Key, mediaType: returnedMediaType } = await mediaApi.generateUploadUrl({
        fileName: file.name,
        contentType: file.type,
      })
      setProgress(30)

      // Upload directly to S3 (or local endpoint in dev)
      await mediaApi.uploadFile(uploadUrl, file, (progressPercent) => {
        // Scale progress from 30% to 90% during upload
        setProgress(30 + Math.round(progressPercent * 0.6))
      })
      setProgress(100)

      onUploadComplete(s3Key, returnedMediaType as MediaType)
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
    // Clean up video object URL if needed
    if (mediaType === 'video' && preview) {
      URL.revokeObjectURL(preview)
    }
    setPreview(null)
    setFileName(null)
    setMediaType(null)
    setProgress(0)
    if (fileInputRef.current) {
      fileInputRef.current.value = ''
    }
    onClear()
  }

  const handleClick = () => {
    if (!uploading && fileInputRef.current) {
      fileInputRef.current.click()
    }
  }

  return (
    <div className="media-upload">
      <input
        ref={fileInputRef}
        type="file"
        accept={ALLOWED_TYPES.join(',')}
        onChange={handleFileSelect}
        disabled={uploading}
        className="file-input-hidden"
      />

      {preview ? (
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
                {mediaType === 'image' ? 'Image' : 'Video'}
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
        </div>
      ) : (
        <div
          className={`upload-area ${uploading ? 'uploading' : ''}`}
          onClick={handleClick}
          role="button"
          tabIndex={0}
          onKeyDown={(e) => e.key === 'Enter' && handleClick()}
        >
          <div className="upload-placeholder">
            <span className="upload-icon">+</span>
            <span className="upload-text">
              {uploading ? 'Uploading...' : 'Add Media'}
            </span>
            <span className="upload-hint">
              Images: JPG, PNG, GIF (max {MAX_IMAGE_SIZE_MB}MB)
            </span>
            <span className="upload-hint">
              Videos: MP4 (max {MAX_VIDEO_SIZE_MB}MB)
            </span>
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
