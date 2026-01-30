import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // Proxy media files to backend to avoid CORS issues with video canvas extraction
      '/api/media/files': {
        target: 'http://localhost:5122',
        changeOrigin: true,
      },
    },
  },
})
