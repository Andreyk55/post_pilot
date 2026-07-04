import { Link } from 'react-router-dom'

export function LegalPageFooter() {
  return (
    <footer className="privacy-links" aria-label="Legal page footer">
      <p className="privacy-links-home">
        <Link to="/">Back to Publish Harbor</Link>
      </p>
      <nav className="privacy-links-nav" aria-label="Legal pages">
        <Link to="/privacy">Privacy Policy</Link>
        <Link to="/terms">Terms of Service</Link>
        <Link to="/data-deletion">Data Deletion</Link>
      </nav>
    </footer>
  )
}
