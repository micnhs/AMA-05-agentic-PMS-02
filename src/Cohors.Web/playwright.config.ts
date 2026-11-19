import { defineConfig } from '@playwright/test'
import path from 'node:path'

export default defineConfig({
  testDir: './e2e',
  timeout: 90_000,
  expect: { timeout: 25_000 },
  workers: 1,
  outputDir: process.env.COHORS_UI_ARTIFACT_DIR
    ? path.join(process.env.COHORS_UI_ARTIFACT_DIR, 'playwright-results') : './test-results',
  projects: [
    { name: 'preview', grepInvert: /@physical-retention|D0[12]:|S0[123456]:|R01:|E0[12345]:/ },
    { name: 'audit-export', grep: /L03:/ },
    { name: 'summary-report', grep: /C12:/ },
    { name: 'case-documents', grep: /UI0[12]:/ },
    { name: 'report-prefill', grep: /V01:/ },
    { name: 'information-lifecycle', grep: /R01:|V01:/ },
    { name: 'eudamed-configuration', grep: /E0[12]:/ },
    { name: 'eudamed-dossiers', grep: /E0[345]:/ },
    { name: 'cockpit-calendar', grep: /D0[12]:|T10:|SQL-backed complaint/ },
    { name: 'signal-review', grep: /S0[123456]:/ },
    { name: 'physical-retention', grep: /@physical-retention/ },
  ],
  reporter: [['list'], ['junit', { outputFile: process.env.COHORS_UI_ARTIFACT_DIR
    ? path.join(process.env.COHORS_UI_ARTIFACT_DIR, 'e2e-results.xml') : '../../.local/e2e-results.xml' }]],
  use: {
    baseURL: process.env.COHORS_URL ?? 'http://localhost:5187',
    channel: process.env.COHORS_BROWSER ?? 'msedge',
    viewport: { width: 1512, height: 982 },
    trace: 'off',
    screenshot: 'only-on-failure',
  },
})
