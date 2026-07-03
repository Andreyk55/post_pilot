import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // Proxy authenticated media routes to the backend in local development.
      '/api/media': {
        target: 'http://localhost:5122',
        changeOrigin: true,
      },
    },
  },
})
