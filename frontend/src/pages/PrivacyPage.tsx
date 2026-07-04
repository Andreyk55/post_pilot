import ReactMarkdown from 'react-markdown'
import privacyMarkdown from '../../content/privacy.md?raw'
import { LegalPageFooter } from '../components/LegalPageFooter'
import './PrivacyPage.css'

export function PrivacyPage() {
  return (
    <div className="privacy-page">
      <article className="privacy-card">
        <ReactMarkdown>{privacyMarkdown}</ReactMarkdown>
        <LegalPageFooter />
      </article>
    </div>
  )
}
