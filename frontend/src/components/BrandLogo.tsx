import './BrandLogo.css'

type BrandLogoVariant = 'icon' | 'full'

interface BrandLogoProps {
  variant: BrandLogoVariant
  alt?: string
  className?: string
}

const BRAND_LOGO_SRC: Record<BrandLogoVariant, string> = {
  icon: '/branding/icon-transparent.png',
  full: '/branding/logo-transparent.png',
}

export function BrandLogo({ variant, alt = 'Publish Harbor', className = '' }: BrandLogoProps) {
  const classes = ['brand-logo', `brand-logo--${variant}`, className].filter(Boolean).join(' ')

  return <img src={BRAND_LOGO_SRC[variant]} alt={alt} className={classes} />
}
