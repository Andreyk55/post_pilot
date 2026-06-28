import { useEffect, useState, type ReactNode } from 'react'

const failedAvatarUrls = new Set<string>()

interface AvatarImageProps {
  src?: string | null
  alt: string
  fallback: ReactNode
  className?: string
}

export function isUnstableMetaAvatarUrl(src?: string | null) {
  if (!src) return false

  try {
    const hostname = new URL(src).hostname.toLowerCase()
    return hostname === 'fbcdn.net'
      || hostname.endsWith('.fbcdn.net')
      || hostname === 'fbsbx.com'
      || hostname.endsWith('.fbsbx.com')
  } catch {
    return false
  }
}

export function AvatarImage({ src, alt, fallback, className }: AvatarImageProps) {
  const [isBroken, setIsBroken] = useState(false)

  useEffect(() => {
    setIsBroken(false)
  }, [src])

  if (!src || isBroken || failedAvatarUrls.has(src) || isUnstableMetaAvatarUrl(src)) {
    return <>{fallback}</>
  }

  return (
    <img
      className={className}
      src={src}
      alt={alt}
      onError={() => {
        failedAvatarUrls.add(src)
        setIsBroken(true)
      }}
    />
  )
}
