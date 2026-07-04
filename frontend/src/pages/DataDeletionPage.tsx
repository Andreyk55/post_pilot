import { Link } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import dataDeletionMarkdown from '../../content/data-deletion.md?raw'
import './PrivacyPage.css'

export function DataDeletionPage() {
  return (
    <div className="privacy-page">
      <article className="privacy-card">
        <ReactMarkdown>{dataDeletionMarkdown}</ReactMarkdown>
        <p className="privacy-links">
          <Link to="/">Back to Publish Harbor</Link>
        </p>
      </article>
    </div>
  )
}
