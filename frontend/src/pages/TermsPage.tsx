import ReactMarkdown from 'react-markdown'
import termsMarkdown from '../../content/terms.md?raw'
import { LegalPageFooter } from '../components/LegalPageFooter'
import './PrivacyPage.css'

export function TermsPage() {
  return (
    <div className="privacy-page">
      <article className="privacy-card">
        <ReactMarkdown>{termsMarkdown}</ReactMarkdown>
        <LegalPageFooter />
      </article>
    </div>
  )
}
