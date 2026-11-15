import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './e2e',
  timeout: 90_000,
  expect: { timeout: 25_000 },
  workers: 1,
  reporter: [['list'], ['junit', { outputFile: '../../.local/e2e-results.xml' }]],
  use: {
    baseURL: process.env.COHORS_URL ?? 'http://localhost:5187',
    channel: process.env.COHORS_BROWSER ?? 'msedge',
    viewport: { width: 1512, height: 982 },
    trace: 'off',
    screenshot: 'only-on-failure',
  },
})
