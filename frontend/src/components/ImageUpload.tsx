import { useState, useRef } from 'react'
import { mediaApi } from '../api/media'
import './ImageUpload.css'

interface ImageUploadProps {
  onUploadComplete: (storageKey: string) => void
  onUploadError: (error: string) => void
  onClear: () => void
  onUploadingChange?: (isUploading: boolean) => void
}

const ALLOWED_TYPES = ['image/jpeg', 'image/png', 'image/gif']
const MAX_SIZE_MB = 10
const MAX_SIZE_BYTES = MAX_SIZE_MB * 1024 * 1024

export function ImageUpload({ onUploadComplete, onUploadError, onClear, onUploadingChange }: ImageUploadProps) {
  const [uploading, setUploading] = useState(false)
  const [preview, setPreview] = useState<string | null>(null)
  const [progress, setProgress] = useState(0)
  const [fileName, setFileName] = useState<string | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)

  const validateFile = (file: File): string | null => {
    if (!ALLOWED_TYPES.includes(file.type)) {
      return 'Invalid file type. Please upload a JPG, PNG, or GIF image.'
    }
    if (file.size > MAX_SIZE_BYTES) {
      return `File too large. Maximum size is ${MAX_SIZE_MB}MB.`
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

    // Show preview immediately
    const reader = new FileReader()
    reader.onload = (e) => setPreview(e.target?.result as string)
    reader.readAsDataURL(file)
    setFileName(file.name)

    try {
      setUploading(true)
      onUploadingChange?.(true)
      setProgress(10)

      // Step 1: server issues a presigned PUT URL and creates a Media row (PendingUpload).
      const { uploadUrl, storageKey, mediaId } = await mediaApi.initUpload({
        fileName: file.name,
        contentType: file.type,
        sizeBytes: file.size,
      })
      setProgress(30)

      // Step 2: client uploads bytes directly to object storage (or local endpoint in dev).
      await mediaApi.uploadFile(uploadUrl, file)
      setProgress(80)

      // Step 3: server verifies the object landed in storage and flips Media row to Uploaded.
      await mediaApi.completeUpload({ mediaId })
      setProgress(100)

      onUploadComplete(storageKey)
    } catch (err) {
      console.error('Upload failed:', err)
      onUploadError(err instanceof Error ? err.message : 'Failed to upload image. Please try again.')
      setPreview(null)
      setFileName(null)
    } finally {
      setUploading(false)
      onUploadingChange?.(false)
    }
  }

  const handleClear = () => {
    setPreview(null)
    setFileName(null)
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
    <div className="image-upload">
      <input
        ref={fileInputRef}
        type="file"
        accept="image/jpeg,image/png,image/gif"
        onChange={handleFileSelect}
        disabled={uploading}
        className="file-input-hidden"
      />

      {preview ? (
        <div className="image-preview">
          <img src={preview} alt="Upload preview" />
          <div className="preview-overlay">
            <span className="preview-filename">{fileName}</span>
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
              {uploading ? 'Uploading...' : 'Add Image'}
            </span>
            <span className="upload-hint">JPG, PNG, or GIF (max {MAX_SIZE_MB}MB)</span>
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
