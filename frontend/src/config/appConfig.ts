import commonConfig from '../../config/common.json'
import localConfig from '../../config/local.json'
import devConfig from '../../config/dev.json'
import prodConfig from '../../config/prod.json'

export interface AppConfig {
  apiBaseUrl: string
}

function loadEnvConfig(): Partial<AppConfig> {
  const mode = import.meta.env.MODE
  switch (mode) {
    case 'development':
      return localConfig
    case 'staging':
      return devConfig
    case 'production':
      return prodConfig
    default:
      return {}
  }
}

function buildConfig(): AppConfig {
  const envConfig = loadEnvConfig()

  const merged: AppConfig = {
    apiBaseUrl: envConfig.apiBaseUrl ?? commonConfig.apiBaseUrl,
  }

  // Allow VITE_API_URL env var to override config files
  if (import.meta.env.VITE_API_URL) {
    merged.apiBaseUrl = import.meta.env.VITE_API_URL
  }

  return merged
}

export const config: AppConfig = buildConfig()
