import { useState, useEffect } from 'react'
import type {
  FacebookPage,
  InstagramAccount,
  MetaConnectionStatus,
} from '../../types/meta'
import { metaApi, MetaApiError } from '../../api/meta'
import { PageSelectionStep } from './PageSelectionStep'
import { InstagramSelectionStep } from './InstagramSelectionStep'
import './MetaConnectionWizard.css'

interface MetaConnectionWizardProps {
  isOpen: boolean
  onClose: () => void
  onComplete: () => void
  initialStatus?: MetaConnectionStatus
  // For manage flow - pre-populate with existing selections
  existingPageIds?: string[]
  existingInstagramIds?: string[]
  isManageMode?: boolean
}

type WizardStep = 'pages' | 'instagram' | 'saving'

export function MetaConnectionWizard({
  isOpen,
  onClose,
  onComplete,
  existingPageIds = [],
  existingInstagramIds = [],
  isManageMode = false,
}: MetaConnectionWizardProps) {
  const [step, setStep] = useState<WizardStep>('pages')
  const [pages, setPages] = useState<FacebookPage[]>([])
  const [selectedPageIds, setSelectedPageIds] = useState<string[]>(existingPageIds)
  const [instagramAccounts, setInstagramAccounts] = useState<InstagramAccount[]>([])
  const [selectedInstagramIds, setSelectedInstagramIds] = useState<string[]>(existingInstagramIds)
  const [tempToken, setTempToken] = useState<string>('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [discoveringInstagram, setDiscoveringInstagram] = useState(false)

  // Load pages on mount for manage mode
  useEffect(() => {
    if (isOpen && isManageMode) {
      loadExistingPages()
    }
  }, [isOpen, isManageMode])

  // Listen for OAuth callback messages
  useEffect(() => {
    const handleMessage = async (event: MessageEvent) => {
      // Verify origin for security
      if (event.origin !== window.location.origin) return

      if (event.data?.type === 'META_OAUTH_CALLBACK') {
        const { code, state } = event.data
        await handleOAuthCallback(code, state)
      }
    }

    window.addEventListener('message', handleMessage)
    return () => window.removeEventListener('message', handleMessage)
  }, [])

  const loadExistingPages = async () => {
    try {
      setLoading(true)
      setError(null)
      const response = await metaApi.getAvailablePages()
      setPages(response.pages)
    } catch (err) {
      setError('Failed to load pages. Please try again.')
      console.error('Error loading pages:', err)
    } finally {
      setLoading(false)
    }
  }

  const handleOAuthCallback = async (code: string, state: string) => {
    try {
      setLoading(true)
      setError(null)
      const response = await metaApi.handleCallback(code, state)
      setTempToken(response.tempToken)
      setPages(response.pages)
      setStep('pages')
    } catch (err) {
      setError('Failed to connect to Meta. Please try again.')
      console.error('OAuth callback error:', err)
    } finally {
      setLoading(false)
    }
  }

  const handlePageSelectionComplete = async (pageIds: string[]) => {
    setSelectedPageIds(pageIds)
    setDiscoveringInstagram(true)
    setError(null)

    try {
      const response = await metaApi.discoverInstagram({
        tempToken: isManageMode ? '' : tempToken, // Empty for manage mode (uses stored tokens)
        pageIds,
      })
      setInstagramAccounts(response.instagramAccounts)
      setStep('instagram')
    } catch (err) {
      setError('Failed to discover Instagram accounts. You can continue without Instagram.')
      console.error('Instagram discovery error:', err)
      // Still allow proceeding to Instagram step even on error
      setInstagramAccounts([])
      setStep('instagram')
    } finally {
      setDiscoveringInstagram(false)
    }
  }

  const handleInstagramSelectionComplete = async (igIds: string[]) => {
    setSelectedInstagramIds(igIds)
    setStep('saving')
    setError(null)

    try {
      setLoading(true)
      if (isManageMode) {
        await metaApi.updateConnection({
          selectedPageIds,
          selectedInstagramIds: igIds,
        })
      } else {
        await metaApi.saveConnection({
          tempToken,
          selectedPageIds,
          selectedInstagramIds: igIds,
        })
      }
      onComplete()
    } catch (err) {
      // 409 = the account/page is owned by another workspace (or this workspace
      // already has an active connection). Surface the server's exact message so
      // the user knows to disconnect it from the owning workspace first — not a
      // generic failure.
      if (err instanceof MetaApiError && err.status === 409) {
        setError(err.message)
      } else {
        setError('Failed to save connection. Please try again.')
      }
      setStep('instagram')
      console.error('Save connection error:', err)
    } finally {
      setLoading(false)
    }
  }

  const handleBack = () => {
    if (step === 'instagram') {
      setStep('pages')
    }
  }

  const handleClose = () => {
    // Reset state on close
    setStep('pages')
    setSelectedPageIds(existingPageIds)
    setSelectedInstagramIds(existingInstagramIds)
    setInstagramAccounts([])
    setError(null)
    onClose()
  }

  if (!isOpen) return null

  return (
    <div className="wizard-overlay" onClick={handleClose}>
      <div className="wizard-modal" onClick={e => e.stopPropagation()}>
        <div className="wizard-header">
          <h2>{isManageMode ? 'Manage Meta Connection' : 'Connect to Meta'}</h2>
          <button className="wizard-close-btn" onClick={handleClose}>
            &times;
          </button>
        </div>

        <div className="wizard-steps">
          <div className={`wizard-step-indicator ${step === 'pages' ? 'active' : step === 'instagram' || step === 'saving' ? 'completed' : ''}`}>
            <span className="step-number">1</span>
            <span className="step-label">Select Pages</span>
          </div>
          <div className="wizard-step-connector" />
          <div className={`wizard-step-indicator ${step === 'instagram' ? 'active' : step === 'saving' ? 'completed' : ''}`}>
            <span className="step-number">2</span>
            <span className="step-label">Instagram</span>
          </div>
        </div>

        {error && (
          <div className="wizard-error">
            <span className="error-icon">!</span>
            {error}
          </div>
        )}

        <div className="wizard-content">
          {loading && step === 'pages' && pages.length === 0 ? (
            <div className="wizard-loading">
              <div className="spinner large" />
              <p>Loading your pages...</p>
            </div>
          ) : step === 'pages' ? (
            <PageSelectionStep
              pages={pages}
              selectedPageIds={selectedPageIds}
              onContinue={handlePageSelectionComplete}
              loading={discoveringInstagram}
            />
          ) : step === 'instagram' ? (
            <InstagramSelectionStep
              instagramAccounts={instagramAccounts}
              selectedInstagramIds={selectedInstagramIds}
              onBack={handleBack}
              onComplete={handleInstagramSelectionComplete}
              loading={loading}
            />
          ) : (
            <div className="wizard-loading">
              <div className="spinner large" />
              <p>Saving your connection...</p>
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
