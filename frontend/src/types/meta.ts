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

// Connected target display type (unified for UI)
export interface ConnectedTarget {
  id: string
  type: 'facebook_page' | 'instagram'
  name: string
  identifier: string // pageId or @handle
  pictureUrl?: string
}
