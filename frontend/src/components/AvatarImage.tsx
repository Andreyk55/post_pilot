import { useEffect, useState, type ReactNode } from 'react'

interface AvatarImageProps {
  src?: string | null
  alt: string
  fallback: ReactNode
  className?: string
}

export function AvatarImage({ src, alt, fallback, className }: AvatarImageProps) {
  const [isBroken, setIsBroken] = useState(false)

  useEffect(() => {
    setIsBroken(false)
  }, [src])

  if (!src || isBroken) {
    return <>{fallback}</>
  }

  return <img className={className} src={src} alt={alt} onError={() => setIsBroken(true)} />
}