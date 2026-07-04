import { Link } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import termsMarkdown from '../../content/terms.md?raw'
import './PrivacyPage.css'

export function TermsPage() {
  return (
    <div className="privacy-page">
      <article className="privacy-card">
        <ReactMarkdown>{termsMarkdown}</ReactMarkdown>
        <p className="privacy-links">
          <Link to="/">Back to Publish Harbor</Link>
        </p>
      </article>
    </div>
  )
}
