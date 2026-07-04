import { Link } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import privacyMarkdown from '../../content/privacy.md?raw'
import './PrivacyPage.css'

export function PrivacyPage() {
  return (
    <div className="privacy-page">
      <article className="privacy-card">
        <ReactMarkdown>{privacyMarkdown}</ReactMarkdown>
        <p className="privacy-links">
          <Link to="/">Back to Publish Harbor</Link>
        </p>
      </article>
    </div>
  )
}
