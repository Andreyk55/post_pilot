// Meta OAuth connection types

export type MetaConnectionStatus =
  | 'disconnected'
  | 'authorizing'
  | 'selecting_pages'
  | 'selecting_instagram'
  | 'connected'

export interface FacebookPage {
  id: string
  name: string
  category?: string
  pictureUrl?: string
  accessToken?: string
}

export interface InstagramAccount {
  id: string
  username: string
  name?: string
  profilePictureUrl?: string
  pageId: string // The Facebook Page this IG account is linked to
  pageName: string
}

export interface MetaConnection {
  id: string
  userId: string
  accessToken: string
  tokenExpiresAt: string
  connectedAt: string
  pages: ConnectedPage[]
  instagramAccounts: ConnectedInstagramAccount[]
  // Stable Meta account identity (FB user id + display name). Nullable on
  // rows that predate the provider-identity migration.
  providerAccountId?: string | null
  providerAccountName?: string | null
}

export interface ConnectedPage {
  id: string
  pageId: string
  name: string
  category?: string
  pictureUrl?: string
  accessToken: string
}

export interface ConnectedInstagramAccount {
  id: string
  igBusinessId: string
  username: string
  name?: string
  profilePictureUrl?: string
  pageId: string
  pageName: string
}

// API request/response types

export interface MetaOAuthStartResponse {
  authUrl: string
  state: string
}

export interface MetaOAuthCallbackRequest {
  code: string
  state: string
}

export interface MetaOAuthCallbackResponse {
  tempToken: string
  pages: FacebookPage[]
}

// OAuth Complete (identity-level connection only)
export interface MetaOAuthCompleteRequest {
  code: string
  state: string
}

export interface MetaOAuthCompleteResponse {
  connection: MetaConnection
}

export interface MetaDiscoverInstagramRequest {
  tempToken: string
  pageIds: string[]
}

export interface MetaDiscoverInstagramResponse {
  instagramAccounts: InstagramAccount[]
}

export interface MetaSaveConnectionRequest {
  tempToken: string
  selectedPageIds: string[]
  selectedInstagramIds: string[]
}

export interface MetaSaveConnectionResponse {
  connection: MetaConnection
}

export interface MetaConnectionResponse {
  connection: MetaConnection | null
  isConnected: boolean
}

export interface MetaUpdatePagesRequest {
  selectedPageIds: string[]
  selectedInstagramIds: string[]
}

// Instagram eligibility discovery
export type InstagramEligibilityStatus =
  | 'Connected'
  | 'NotLinked'
  | 'MissingPermission'
  | 'NotProfessional'
  | 'Unknown'

export interface InstagramEligibilityDto {
  pageId: string
  pageName: string
  igUserId: string | null
  igUsername: string | null
  igDisplayName: string | null
  igProfilePictureUrl: string | null
  eligibilityStatus: InstagramEligibilityStatus
  reason: string
}

export interface InstagramDiscoveryResponse {
  pages: InstagramEligibilityDto[]
  totalPages: number
  linkedCount: number
}

// Connected target display type (unified for UI)
export interface ConnectedTarget {
  id: string
  type: 'facebook_page' | 'instagram'
  name: string
  identifier: string // pageId or @handle
  pictureUrl?: string
}

// Validation limits
export interface ValidationLimitsResponse {
  voiceProfile: VoiceProfileLimits
  post: PostLimits
  media: MediaLimits
}

export interface VoiceProfileLimits {
  nameMinLength: number
  nameMaxLength: number
  descriptionMaxLength: number
  doRulesMaxLength: number
  dontRulesMaxLength: number
  bannedWordsMaxLength: number
  examplePostsMaxLength: number
  totalMaxLength: number
}

export interface PostLimits {
  textMaxLength: number
  titleMaxLength: number
  maxHashtags: number
  maxMediaFiles: number
}

export interface MediaLimits {
  imageMaxBytes: number
  videoMaxBytes: number
}
