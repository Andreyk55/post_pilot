/**
 * Browser-based video frame extraction utility.
 * Uses HTML5 video + canvas to extract frames without server-side dependencies.
 */

export interface ExtractedFrame {
  timestampSeconds: number
  dataUrl: string // base64 data URL (data:image/jpeg;base64,...)
}

export interface FrameExtractionOptions {
  /** Number of frames to extract (default: 6) */
  frameCount?: number
  /** Output image width (default: 640) */
  width?: number
  /** Output image quality 0-1 (default: 0.85) */
  quality?: number
  /** Progress callback */
  onProgress?: (progress: number) => void
}

/**
 * Extracts evenly-distributed frames from a video URL.
 * Works entirely in the browser using video element + canvas.
 */
export async function extractVideoFrames(
  videoUrl: string,
  options: FrameExtractionOptions = {}
): Promise<ExtractedFrame[]> {
  const { frameCount = 6, width = 640, quality = 0.85, onProgress } = options

  return new Promise((resolve, reject) => {
    const video = document.createElement('video')
    video.crossOrigin = 'anonymous'
    video.muted = true
    video.preload = 'metadata'

    // Handle CORS issues gracefully
    video.onerror = (e) => {
      console.error('Video load error:', e)
      reject(new Error('Failed to load video. The video may not support cross-origin access.'))
    }

    video.onloadedmetadata = async () => {
      const duration = video.duration

      if (!duration || duration <= 0 || !isFinite(duration)) {
        reject(new Error('Invalid video duration'))
        return
      }

      const canvas = document.createElement('canvas')
      const ctx = canvas.getContext('2d')

      if (!ctx) {
        reject(new Error('Failed to create canvas context'))
        return
      }

      // Calculate aspect ratio and set canvas size
      const aspectRatio = video.videoHeight / video.videoWidth
      canvas.width = width
      canvas.height = Math.round(width * aspectRatio)

      // Calculate timestamps for evenly distributed frames
      // Skip first and last 5% to avoid black frames
      const startTime = duration * 0.05
      const endTime = duration * 0.95
      const interval = (endTime - startTime) / (frameCount - 1)

      const timestamps: number[] = []
      for (let i = 0; i < frameCount; i++) {
        timestamps.push(startTime + i * interval)
      }

      const frames: ExtractedFrame[] = []

      // Extract frames sequentially
      for (let i = 0; i < timestamps.length; i++) {
        const timestamp = timestamps[i]

        try {
          const dataUrl = await seekAndCapture(video, timestamp, canvas, ctx, quality)
          frames.push({
            timestampSeconds: timestamp,
            dataUrl,
          })

          if (onProgress) {
            onProgress(((i + 1) / timestamps.length) * 100)
          }
        } catch {
          console.warn(`Failed to extract frame at ${timestamp}s, skipping`)
        }
      }

      // Clean up
      video.src = ''
      video.load()

      if (frames.length === 0) {
        reject(new Error('Failed to extract any frames from video'))
      } else {
        resolve(frames)
      }
    }

    video.src = videoUrl
    video.load()
  })
}

/**
 * Seeks to a specific timestamp and captures a frame.
 */
function seekAndCapture(
  video: HTMLVideoElement,
  timestamp: number,
  canvas: HTMLCanvasElement,
  ctx: CanvasRenderingContext2D,
  quality: number
): Promise<string> {
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      reject(new Error(`Seek timeout at ${timestamp}s`))
    }, 10000) // 10 second timeout per frame

    const handleSeeked = () => {
      clearTimeout(timeout)
      video.removeEventListener('seeked', handleSeeked)

      try {
        // Draw the current frame to canvas
        ctx.drawImage(video, 0, 0, canvas.width, canvas.height)

        // Get the data URL - this can throw SecurityError if canvas is tainted
        const dataUrl = canvas.toDataURL('image/jpeg', quality)
        resolve(dataUrl)
      } catch (err) {
        if (err instanceof Error && err.name === 'SecurityError') {
          reject(new Error('Failed to extract frame due to cross-origin restrictions. The video server must allow CORS.'))
        } else {
          reject(err)
        }
      }
    }

    video.addEventListener('seeked', handleSeeked)
    video.currentTime = timestamp
  })
}

/**
 * Extracts a single frame at a specific timestamp.
 */
export async function extractSingleFrame(
  videoUrl: string,
  timestampSeconds: number,
  options: Omit<FrameExtractionOptions, 'frameCount' | 'onProgress'> = {}
): Promise<ExtractedFrame> {
  const { width = 640, quality = 0.85 } = options

  return new Promise((resolve, reject) => {
    const video = document.createElement('video')
    video.crossOrigin = 'anonymous'
    video.muted = true
    video.preload = 'metadata'

    video.onerror = (e) => {
      console.error('Video load error:', e)
      reject(new Error('Failed to load video'))
    }

    video.onloadedmetadata = () => {
      const canvas = document.createElement('canvas')
      const ctx = canvas.getContext('2d')

      if (!ctx) {
        reject(new Error('Failed to create canvas context'))
        return
      }

      const aspectRatio = video.videoHeight / video.videoWidth
      canvas.width = width
      canvas.height = Math.round(width * aspectRatio)

      seekAndCapture(video, timestampSeconds, canvas, ctx, quality)
        .then((dataUrl) => {
          video.src = ''
          video.load()
          resolve({ timestampSeconds, dataUrl })
        })
        .catch((err) => {
          video.src = ''
          video.load()
          reject(err)
        })
    }

    video.src = videoUrl
    video.load()
  })
}

/**
 * Converts a data URL to base64 string (without the prefix).
 */
export function dataUrlToBase64(dataUrl: string): string {
  const parts = dataUrl.split(',')
  return parts.length > 1 ? parts[1] : dataUrl
}

/**
 * Gets the MIME type from a data URL.
 */
export function getMimeTypeFromDataUrl(dataUrl: string): string {
  const match = dataUrl.match(/^data:([^;]+);/)
  return match ? match[1] : 'image/jpeg'
}
