import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'

export default defineConfig({
  plugins: [react()],
  build: { outDir: fileURLToPath(new URL('../Cohors.Api/wwwroot', import.meta.url)), emptyOutDir: true },
  server: { port: 5188, strictPort: true, proxy: { '/api': 'http://localhost:5187' } },
})
