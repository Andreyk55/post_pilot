import './PlaceholderPage.css'

interface PlaceholderPageProps {
  title: string
  icon: string
}

export function PlaceholderPage({ title, icon }: PlaceholderPageProps) {
  return (
    <div className="placeholder-page">
      <span className="placeholder-icon">{icon}</span>
      <h1>{title}</h1>
      <p>This feature is coming soon...</p>
    </div>
  )
}