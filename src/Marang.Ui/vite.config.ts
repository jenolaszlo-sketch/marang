import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', 'MARANG_')
  const apiTarget = env.MARANG_API_ORIGIN || 'http://127.0.0.1:5107'
  return {
    base: '/ui/',
    plugins: [react()],
    server: {
      port: 5173,
      strictPort: true,
      proxy: { '/api': apiTarget },
    },
    preview: { proxy: { '/api': apiTarget } },
  }
})
