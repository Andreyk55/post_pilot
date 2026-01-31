import { useState, useEffect } from 'react'
import {
  voiceProfileApi,
  type VoiceProfile,
  type CreateVoiceProfileRequest,
  VoiceProfileError,
} from '../api/voiceProfiles'
import './VoiceProfileModal.css'

interface VoiceProfileModalProps {
  isOpen: boolean
  onClose: () => void
  profileId?: string | null // If provided, edit mode
  onSaved: (profile: VoiceProfile) => void
  onDeleted?: () => void
}

export function VoiceProfileModal({
  isOpen,
  onClose,
  profileId,
  onSaved,
  onDeleted,
}: VoiceProfileModalProps) {
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [doRules, setDoRules] = useState('')
  const [dontRules, setDontRules] = useState('')
  const [bannedWords, setBannedWords] = useState('')
  const [examplePosts, setExamplePosts] = useState('')

  const [loading, setLoading] = useState(false)
  const [loadingProfile, setLoadingProfile] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false)

  const isEditMode = !!profileId

  // Load profile data when editing
  useEffect(() => {
    if (isOpen && profileId) {
      setLoadingProfile(true)
      setError(null)
      voiceProfileApi
        .getProfile(profileId)
        .then((profile) => {
          setName(profile.name)
          setDescription(profile.description || '')
          setDoRules(profile.doRules || '')
          setDontRules(profile.dontRules || '')
          setBannedWords(profile.bannedWords || '')
          setExamplePosts(profile.examplePosts || '')
        })
        .catch((err) => {
          setError(err instanceof VoiceProfileError ? err.message : 'Failed to load profile')
        })
        .finally(() => setLoadingProfile(false))
    } else if (isOpen && !profileId) {
      // Reset form for create mode
      setName('')
      setDescription('')
      setDoRules('')
      setDontRules('')
      setBannedWords('')
      setExamplePosts('')
      setError(null)
    }
  }, [isOpen, profileId])

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!name.trim()) {
      setError('Name is required')
      return
    }

    setLoading(true)
    setError(null)

    try {
      const request: CreateVoiceProfileRequest = {
        name: name.trim(),
        description: description.trim() || null,
        doRules: doRules.trim() || null,
        dontRules: dontRules.trim() || null,
        bannedWords: bannedWords.trim() || null,
        examplePosts: examplePosts.trim() || null,
      }

      let savedProfile: VoiceProfile
      if (isEditMode) {
        savedProfile = await voiceProfileApi.updateProfile(profileId!, request)
      } else {
        savedProfile = await voiceProfileApi.createProfile(request)
      }

      onSaved(savedProfile)
      onClose()
    } catch (err) {
      setError(err instanceof VoiceProfileError ? err.message : 'Failed to save profile')
    } finally {
      setLoading(false)
    }
  }

  const handleDelete = async () => {
    if (!profileId) return

    setLoading(true)
    setError(null)

    try {
      await voiceProfileApi.deleteProfile(profileId)
      onDeleted?.()
      onClose()
    } catch (err) {
      setError(err instanceof VoiceProfileError ? err.message : 'Failed to delete profile')
    } finally {
      setLoading(false)
      setShowDeleteConfirm(false)
    }
  }

  if (!isOpen) return null

  return (
    <div className="voice-profile-modal-overlay" onClick={onClose}>
      <div className="voice-profile-modal" onClick={(e) => e.stopPropagation()}>
        <div className="voice-profile-modal-header">
          <h2>{isEditMode ? 'Edit Voice Profile' : 'Create Voice Profile'}</h2>
          <button type="button" className="modal-close-btn" onClick={onClose}>
            &times;
          </button>
        </div>

        {loadingProfile ? (
          <div className="voice-profile-loading">
            <div className="voice-profile-spinner"></div>
            Loading profile...
          </div>
        ) : (
          <form onSubmit={handleSubmit} className="voice-profile-form">
            {error && <div className="voice-profile-error">{error}</div>}

            <div className="form-group">
              <label htmlFor="vp-name">Name *</label>
              <input
                id="vp-name"
                type="text"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="e.g., Brand Voice, Casual Personal"
                maxLength={100}
                disabled={loading}
                required
              />
            </div>

            <div className="form-group">
              <label htmlFor="vp-description">Brand/Audience Description</label>
              <textarea
                id="vp-description"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                placeholder="Describe your brand voice and target audience..."
                maxLength={1000}
                disabled={loading}
                rows={3}
              />
              <span className="field-hint">Who are you speaking to? What's your brand personality?</span>
            </div>

            <div className="form-group">
              <label htmlFor="vp-do-rules">Do Rules (Style Guidelines)</label>
              <textarea
                id="vp-do-rules"
                value={doRules}
                onChange={(e) => setDoRules(e.target.value)}
                placeholder="Use active voice&#10;Include statistics when possible&#10;Start with a hook"
                maxLength={2000}
                disabled={loading}
                rows={4}
              />
              <span className="field-hint">One rule per line. What should the AI do?</span>
            </div>

            <div className="form-group">
              <label htmlFor="vp-dont-rules">Don't Rules (Avoid These)</label>
              <textarea
                id="vp-dont-rules"
                value={dontRules}
                onChange={(e) => setDontRules(e.target.value)}
                placeholder="Don't use jargon&#10;Avoid passive voice&#10;Never be condescending"
                maxLength={2000}
                disabled={loading}
                rows={4}
              />
              <span className="field-hint">One rule per line. What should the AI avoid?</span>
            </div>

            <div className="form-group">
              <label htmlFor="vp-banned-words">Banned Words/Phrases</label>
              <textarea
                id="vp-banned-words"
                value={bannedWords}
                onChange={(e) => setBannedWords(e.target.value)}
                placeholder="synergy, leverage, game-changer, disrupt"
                maxLength={1000}
                disabled={loading}
                rows={2}
              />
              <span className="field-hint">Comma-separated or one per line. These will never appear.</span>
            </div>

            <div className="form-group">
              <label htmlFor="vp-examples">Example Posts</label>
              <textarea
                id="vp-examples"
                value={examplePosts}
                onChange={(e) => setExamplePosts(e.target.value)}
                placeholder="Here's an example post that shows our voice...&#10;&#10;And here's another example with a different approach..."
                maxLength={5000}
                disabled={loading}
                rows={6}
              />
              <span className="field-hint">Separate examples with a blank line. AI will match this style.</span>
            </div>

            <div className="voice-profile-modal-actions">
              {isEditMode && (
                <button
                  type="button"
                  className="btn-delete"
                  onClick={() => setShowDeleteConfirm(true)}
                  disabled={loading}
                >
                  Delete
                </button>
              )}
              <div className="action-spacer" />
              <button type="button" className="btn-cancel" onClick={onClose} disabled={loading}>
                Cancel
              </button>
              <button type="submit" className="btn-save" disabled={loading || !name.trim()}>
                {loading ? (
                  <>
                    <div className="voice-profile-spinner" style={{width: '12px', height: '12px', marginRight: '0.5rem'}}></div>
                    Saving...
                  </>
                ) : isEditMode ? 'Save Changes' : 'Create Profile'}
              </button>
            </div>
          </form>
        )}

        {/* Delete confirmation dialog */}
        {showDeleteConfirm && (
          <div className="delete-confirm-overlay">
            <div className="delete-confirm-dialog">
              <p>Are you sure you want to delete this voice profile?</p>
              <p className="delete-warning">This action cannot be undone.</p>
              <div className="delete-confirm-actions">
                <button
                  type="button"
                  className="btn-cancel"
                  onClick={() => setShowDeleteConfirm(false)}
                  disabled={loading}
                >
                  Cancel
                </button>
                <button
                  type="button"
                  className="btn-delete-confirm"
                  onClick={handleDelete}
                  disabled={loading}
                >
                  {loading ? (
                    <>
                      <div className="voice-profile-spinner" style={{width: '12px', height: '12px', marginRight: '0.5rem'}}></div>
                      Deleting...
                    </>
                  ) : 'Delete'}
                </button>
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
