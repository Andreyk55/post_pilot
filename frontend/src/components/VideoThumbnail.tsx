import { useState, useEffect, useRef } from 'react'

interface VideoThumbnailProps {
  videoUrl: string
  className?: string
  /** Time in seconds to capture the thumbnail from (default: 1) */
  captureTime?: number
}

// Simple in-memory cache for thumbnails
const thumbnailCache = new Map<string, string>()

export function VideoThumbnail({ videoUrl, className = '', captureTime = 1 }: VideoThumbnailProps) {
  const [thumbnail, setThumbnail] = useState<string | null>(null)
  const [isLoading, setIsLoading] = useState(true)
  const [hasError, setHasError] = useState(false)
  const videoRef = useRef<HTMLVideoElement | null>(null)
  const canvasRef = useRef<HTMLCanvasElement | null>(null)

  useEffect(() => {
    // Check cache first
    const cached = thumbnailCache.get(videoUrl)
    if (cached) {
      setThumbnail(cached)
      setIsLoading(false)
      return
    }

    // Create video element for thumbnail extraction
    const video = document.createElement('video')
    videoRef.current = video
    video.crossOrigin = 'anonymous'
    video.preload = 'metadata'
    video.muted = true
    video.playsInline = true

    const canvas = document.createElement('canvas')
    canvasRef.current = canvas

    const handleLoadedMetadata = () => {
      // Seek to capture time, but don't go past video duration
      const seekTime = Math.min(captureTime, video.duration * 0.25, video.duration - 0.1)
      video.currentTime = Math.max(0, seekTime)
    }

    const handleSeeked = () => {
      try {
        // Set canvas size to match video dimensions
        canvas.width = video.videoWidth
        canvas.height = video.videoHeight

        // Draw the video frame to canvas
        const ctx = canvas.getContext('2d')
        if (ctx) {
          ctx.drawImage(video, 0, 0, canvas.width, canvas.height)

          // Convert to data URL
          const dataUrl = canvas.toDataURL('image/jpeg', 0.7)

          // Cache the result
          thumbnailCache.set(videoUrl, dataUrl)

          setThumbnail(dataUrl)
          setIsLoading(false)
        }
      } catch {
        setHasError(true)
        setIsLoading(false)
      }

      // Clean up
      video.src = ''
      video.load()
    }

    const handleError = () => {
      setHasError(true)
      setIsLoading(false)
    }

    video.addEventListener('loadedmetadata', handleLoadedMetadata)
    video.addEventListener('seeked', handleSeeked)
    video.addEventListener('error', handleError)

    // Start loading the video
    video.src = videoUrl

    return () => {
      video.removeEventListener('loadedmetadata', handleLoadedMetadata)
      video.removeEventListener('seeked', handleSeeked)
      video.removeEventListener('error', handleError)
      video.src = ''
      video.load()
    }
  }, [videoUrl, captureTime])

  if (hasError) {
    // Fallback to placeholder on error
    return (
      <div className={`video-thumbnail video-thumbnail-fallback ${className}`}>
        <svg className="video-play-icon" viewBox="0 0 24 24" fill="currentColor">
          <path d="M8 5v14l11-7z"/>
        </svg>
      </div>
    )
  }

  if (isLoading) {
    return (
      <div className={`video-thumbnail video-thumbnail-loading ${className}`}>
        <div className="thumbnail-spinner" />
      </div>
    )
  }

  return (
    <div className={`video-thumbnail ${className}`}>
      <img src={thumbnail || ''} alt="Video thumbnail" />
      <div className="video-thumbnail-overlay">
        <svg className="video-play-icon" viewBox="0 0 24 24" fill="currentColor">
          <path d="M8 5v14l11-7z"/>
        </svg>
      </div>
    </div>
  )
}
