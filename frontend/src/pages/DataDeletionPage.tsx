import ReactMarkdown from 'react-markdown'
import dataDeletionMarkdown from '../../content/data-deletion.md?raw'
import { LegalPageFooter } from '../components/LegalPageFooter'
import './PrivacyPage.css'

export function DataDeletionPage() {
  return (
    <div className="privacy-page">
      <article className="privacy-card">
        <ReactMarkdown>{dataDeletionMarkdown}</ReactMarkdown>
        <LegalPageFooter />
      </article>
    </div>
  )
}
