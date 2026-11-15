import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import type { SafetyCase } from '../src/types'

const home = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..')
const accounts = Object.fromEntries(readFileSync(path.join(home, '.local', 'demo-access.txt'), 'utf8').split(/\r?\n/)
  .filter(line => line.includes('.demo : ')).map(line => line.split(' : ')))

async function signIn(client: APIRequestContext, username: string) {
  const session = await (await client.get('/api/auth/session')).json()
  const response = await client.post('/api/auth/login', { headers: { 'X-CSRF-Token': session.csrfToken },
    data: { username, password: accounts[username] } })
  expect(response.status()).toBe(200)
  return (await (await client.get('/api/auth/session')).json()).csrfToken as string
}
async function command(client: APIRequestContext, csrf: string, endpoint: string, data: object) {
  const response = await client.post(`/api${endpoint}`, { headers: { 'X-CSRF-Token': csrf, 'Idempotency-Key': crypto.randomUUID() }, data })
  const body = await response.json()
  expect(response.ok(), JSON.stringify(body)).toBeTruthy()
  return body as SafetyCase
}
async function showCase(page: Page, incidentKey: string, title: string) {
  await page.getByRole('navigation').getByRole('button', { name: 'Fallakten', exact: true }).click()
  await page.getByRole('textbox', { name: 'Fälle durchsuchen' }).fill(incidentKey)
  await page.getByRole('button', { name: title, exact: true }).click()
}

test('SQL-backed complaint → grounded assessment → distinct approvals → unknown/reconcile → verified audit', async ({ playwright, browser, baseURL }) => {
  test.setTimeout(150_000)
  const pms = await playwright.request.newContext({ baseURL })
  const medical = await playwright.request.newContext({ baseURL })
  const ra = await playwright.request.newContext({ baseURL })
  const pmsCsrf = await signIn(pms, 'pms.demo')
  await signIn(medical, 'medical.demo')
  const raCsrf = await signIn(ra, 'ra.demo')
  const incidentKey = `UI-${Date.now()}`
  const body = { incidentKey, sourceId: crypto.randomUUID(), title: 'Synthetischer Pilotfall: vorzeitige Revision',
    description: 'SYNTHETISCH: Ein vorzeitiger Funktionsverlust mit Revisionsbedarf wurde berichtet. Ein kausaler Produktbeitrag ist nicht bestätigt. Klinische Befunde und technische Explantatprüfung stehen aus.',
    productId: 'ortho', udi: null, batch: 'DEMO-LOT', country: 'IE', jurisdiction: 'EU-IE',
    seriousness: 'DeathOrSeriousDeterioration', occurredAt: null, awarenessAt: new Date(Date.now() - 86_400_000).toISOString(),
    scenario: 'ui-acceptance', synthetic: true }
  let c = await command(pms, pmsCsrf, '/cases', body)
  const due = c.deadlines[0].legalDue
  c = await command(pms, pmsCsrf, '/cases', { ...body, sourceId: crypto.randomUUID() })
  expect(c.sources).toHaveLength(2)
  expect(c.udi).toBeNull()
  expect(c.deadlines[0].legalDue).toBe(due)

  const context = await browser.newContext({ storageState: await pms.storageState(), viewport: { width: 1512, height: 982 } })
  const page = await context.newPage()
  await page.goto(baseURL!)
  await expect(page.getByRole('heading', { name: 'Sicherheit im Blick.' })).toBeVisible()
  await page.screenshot({ path: path.join(home, '.local', 'cohors-cockpit.png'), fullPage: true })
  await showCase(page, incidentKey, body.title)
  await page.getByRole('button', { name: 'Analyse beauftragen', exact: true }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Analyse starten', exact: true }).click()
  await expect.poll(async () => {
    c = await (await pms.get(`/api/cases/${c.id}`)).json()
    return c.agentRuns.find(r => r.role === 'CausalityRisk')?.status
  }).toBe('Completed')
  expect(c.currentAssessment?.mode).toBe('Replay')
  expect(c.currentAssessment?.content.sections).toHaveLength(12)
  expect(c.currentAssessment?.quality.findings.some(f => f.code === 'clinical_gap')).toBeTruthy()
  expect(c.currentAssessment?.quality.findings.some(f => f.severity === 'Critical')).toBeFalsy()
  expect(c.deadlines[0].legalDue).toBe(due)
  await expect(page.getByRole('heading', { name: 'Assessment v1', exact: true })).toBeVisible()
  await page.screenshot({ path: path.join(home, '.local', 'cohors-workbench.png'), fullPage: true })

  c = await command(pms, pmsCsrf, `/cases/${c.id}/report`, { expectedVersion: c.version, kind: 'Initial', jurisdiction: 'EU-IE',
    language: 'en', preliminary: true, openQuestions: 'Gezielte klinische Befunde und technische Ursachenabklärung bleiben offen.',
    reason: 'Vorläufige Meldung bei unverändert laufender Frist; keine definitive Kausalitätsbehauptung.' })
  const forbidden = await pms.post(`/api/cases/${c.id}/approve/medical`, { headers: { 'X-CSRF-Token': pmsCsrf, 'Idempotency-Key': crypto.randomUUID() },
    data: { expectedVersion: c.version, reason: 'Client role spoof must not grant medical review.', role: 'MedicalSafetyReviewer' } })
  expect(forbidden.status()).toBe(403)
  const medicalContext = await browser.newContext({ storageState: await medical.storageState(), timezoneId: 'Europe/Berlin' })
  const medicalPage = await medicalContext.newPage()
  await medicalPage.goto(baseURL!)
  await showCase(medicalPage, incidentKey, body.title)
  await medicalPage.getByRole('tab', { name: 'Human Review & Meldung' }).click()
  await medicalPage.getByRole('button', { name: 'MIR-Pflichtdaten bearbeiten' }).click()
  let dialog = medicalPage.getByRole('dialog')
  const eventDate = body.awarenessAt.slice(0, 10)
  const nextDate = new Date(Date.now() + 7 * 86_400_000).toISOString().slice(0, 10)
  await dialog.locator('[name=eventDateFrom]').fill(eventDate)
  await dialog.locator('[name=eventDateTo]').fill(eventDate)
  await dialog.locator('[name=eventClassification]').selectOption('Unanticipated serious deterioration in state of health')
  await dialog.locator('[name=deviceProblemCode]').fill('ASYN')
  await dialog.locator('[name=deviceLocation]').selectOption('Unknown')
  await dialog.locator('[name=nextReportDate]').fill(nextDate)
  await dialog.locator('[name=preliminaryAnalysis]').fill('SYNTHETISCH: Technische und klinische Gegenhypothesen bleiben offen; der Originalhinweis beweist keine produktbezogene Kausalität.')
  await dialog.locator('[name=initialCorrectiveAction]').fill('SYNTHETISCH: Der Eingang wurde gesichert und ein versionierter Untersuchungsentwurf dokumentiert; keine reale Maßnahme behauptet.')
  await dialog.locator('[name=furtherInvestigation]').fill('SYNTHETISCH: Gezielte klinische Befunde und technische Explantatprüfung begründet nachverfolgen.')
  await dialog.locator('[name=evidenceIds]').fill(c.evidence.filter(e => e.approved).map(e => e.id).join('\n'))
  await dialog.locator('[name=rationale]').fill('SYNTHETISCHE BROWSERTESTANGABEN: ASYN ist nur eine schemafähige Testkennung, keine fachlich validierte IMDRF-Kodierung. Der feste Folgetermin wird ausdrücklich menschlich zugesagt.')
  await dialog.locator('[name=reason]').fill('Menschlich eingegebene synthetische Pflichtdaten mit konkreten vorhandenen Fundstellen versionieren.')
  await dialog.getByRole('button', { name: 'Versioniert speichern und gegen Original-XSD prüfen' }).click()
  await expect(medicalPage.getByText('Struktur geprüft', { exact: true })).toBeVisible()
  await expect.poll(async () => {
    c = await (await medical.get(`/api/cases/${c.id}`)).json()
    return c.currentReport?.mir?.valid
  }).toBe(true)
  expect(c.currentReport?.mir?.followUpSchedule?.reportDate).toBe(nextDate)
  const downloadPromise = medicalPage.waitForEvent('download')
  await medicalPage.getByRole('button', { name: 'Geprüftes XML herunterladen' }).click()
  const xmlPath = await (await downloadPromise).path()
  if (!xmlPath) throw new Error('Authenticated MIR download produced no file.')
  const xml = readFileSync(xmlPath, 'utf8')
  expect(xml).toContain('SIMULATION ONLY')
  expect(xml).toContain(`<reportNextDate>${nextDate}</reportNextDate>`)
  await medicalPage.getByRole('button', { name: 'Medizinisch freigeben', exact: true }).click()
  dialog = medicalPage.getByRole('dialog')
  await dialog.locator('[name=reason]').fill('Medizinische Erstbewertung, synthetische MIR-Pflichtdaten und Quellen geprüft; Kausalität bleibt vorläufig.')
  await dialog.getByRole('button', { name: 'Medizinisch freigeben', exact: true }).click()
  await expect.poll(async () => (await (await medical.get(`/api/cases/${c.id}`)).json()).currentReport?.state).toBe('MedicalApproved')
  const raContext = await browser.newContext({ storageState: await ra.storageState(), timezoneId: 'Europe/Berlin' })
  const raPage = await raContext.newPage()
  await raPage.goto(baseURL!)
  await showCase(raPage, incidentKey, body.title)
  await raPage.getByRole('tab', { name: 'Human Review & Meldung' }).click()
  await raPage.getByRole('button', { name: 'Regulatorisch freigeben', exact: true }).click()
  dialog = raPage.getByRole('dialog')
  await dialog.locator('[name=reason]').fill('Empfänger, Jurisdiktion, Sprache, XML, feste Terminzusage und Paketversionen unabhängig geprüft.')
  await dialog.getByRole('button', { name: 'Regulatorisch freigeben', exact: true }).click()
  await expect.poll(async () => {
    c = await (await ra.get(`/api/cases/${c.id}`)).json()
    return c.currentReport?.state
  }).toBe('Frozen')
  expect(c.currentReport?.state).toBe('Frozen')
  expect(new Set(c.approvals.map(a => a.actorId)).size).toBe(2)
  expect(new Set(c.approvals.map(a => a.scopeHash)).size).toBe(1)
  await raPage.getByRole('button', { name: 'Simuliert übermitteln', exact: true }).click()
  dialog = raPage.getByRole('dialog')
  await dialog.locator('[name=scenario]').selectOption('Unknown')
  await dialog.locator('[name=reason]').fill('Nur simulierte Übermittlung; absichtlich unklarer Rückkanal bei möglichem Empfang.')
  await dialog.getByRole('button', { name: 'An Simulator übermitteln' }).click()
  await expect.poll(async () => {
    c = await (await ra.get(`/api/cases/${c.id}`)).json()
    return c.currentReport?.state
  }).toBe('TransmissionUnknown')
  expect(c.submissions[0].status).toBe('TransmissionUnknown')
  expect(c.submissions[0].receiptReference).toBeNull()
  const duplicate = await ra.post(`/api/cases/${c.id}/submit`, { headers: { 'X-CSRF-Token': raCsrf, 'Idempotency-Key': crypto.randomUUID() },
    data: { expectedVersion: c.version, scenario: 'Accept', reason: 'Attempted blind duplicate must not be sent.' } })
  expect(duplicate.ok()).toBeFalsy()
  await raPage.getByRole('button', { name: 'Empfang abgleichen', exact: true }).click()
  dialog = raPage.getByRole('dialog')
  await dialog.locator('[name=reason]').fill('Persistierte Korrelations-ID beim Simulator abgeglichen; kein weiterer Sendeversuch.')
  await dialog.getByRole('button', { name: 'Korrelations-ID abgleichen' }).click()
  await expect.poll(async () => {
    c = await (await ra.get(`/api/cases/${c.id}`)).json()
    return c.currentReport?.state
  }).toBe('SimulatedAccepted')
  expect(c.submissions).toHaveLength(1)
  expect(c.submissions[0].attempts).toBe(1)
  expect(c.submissions[0].status).toBe('SimulatedAccepted')
  expect(c.deadlines[0].status).toBe('SimulatedReported')
  expect(c.deadlines[0].legalDue).toBe(due)
  expect(c.followUps).toHaveLength(1)
  expect(c.followUps![0].status).toBe('Open')
  expect(c.followUps![0].sourceReportHash).toBe(c.currentReport?.scopeHash)
  await expect(raPage.locator('.case-context')).toContainText('ZUGESAGTER FOLGETERMIN')
  await raPage.getByRole('navigation').getByRole('button', { name: 'Fristen', exact: true }).click()
  const followUpCard = raPage.locator('.deadline-card').filter({ hasText: incidentKey })
  await expect(followUpCard).toContainText('Zugesagter Folgetermin')
  await expect(followUpCard).toContainText('Simuliert erledigt')
  await expect(followUpCard).toContainText('Europe/Dublin')
  const manifest = await pms.get(`/api/cases/${c.id}/manifest`)
  expect(manifest.status()).toBe(200)
  const audit = await manifest.json()
  expect(audit.hashChainVerified).toBeTruthy()
  expect(audit.events.length).toBe(c.version)
  expect(audit.events.map((e: { action: string }) => e.action)).toContain('SubmissionReconciled')
  await Promise.all([context.close(), medicalContext.close(), raContext.close()])
  await Promise.all([pms.dispose(), medical.dispose(), ra.dispose()])
})

test('authentication, CSRF and reduced-motion presentation have no browser-role bypass', async ({ request, browser, baseURL }) => {
  expect((await request.get('/api/cases')).status()).toBe(401)
  expect((await request.post('/api/auth/login', { data: { username: 'pms.demo', password: 'invalid' } })).status()).toBe(400)
  const context = await browser.newContext({ reducedMotion: 'reduce', viewport: { width: 390, height: 844 } })
  const page = await context.newPage()
  await page.goto(baseURL!)
  await expect(page.getByLabel('Benutzername')).toBeVisible()
  await expect(page.getByRole('combobox')).toHaveCount(0)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy()
  await context.close()
})

test('T10: Dublin legal day remains Dublin legal day in a Berlin browser', async ({ playwright, browser, baseURL }) => {
  const client = await playwright.request.newContext({ baseURL })
  const csrf = await signIn(client, 'pms.demo')
  const incidentKey = `TZ-${Date.now()}`
  const c = await command(client, csrf, '/cases', { incidentKey, sourceId: crypto.randomUUID(),
    title: 'T10 Dublin-Frist in einem Berliner Browser', description: 'SYNTHETISCH: Unabhängiger Referenzfall für das Zwei-Tages-Profil am Freitag.',
    productId: 'ortho', udi: null, batch: null, country: 'IE', jurisdiction: 'EU-IE', seriousness: 'PublicHealthThreat',
    occurredAt: null, awarenessAt: '2026-11-13T10:00:00Z', scenario: 'timezone-regression', synthetic: true })
  expect(c.deadlines[0].legalDue).toBe('2026-11-17T23:59:59.9999999+00:00')
  const context = await browser.newContext({ storageState: await client.storageState(), timezoneId: 'Europe/Berlin' })
  const page = await context.newPage()
  await page.goto(baseURL!)
  await page.getByRole('navigation').getByRole('button', { name: 'Fallakten', exact: true }).click()
  await page.getByRole('textbox', { name: 'Fälle durchsuchen' }).fill(incidentKey)
  const row = page.getByRole('row').filter({ hasText: incidentKey })
  await expect(row).toContainText('17. Nov. 2026')
  await expect(row).not.toContainText('18. Nov. 2026')
  await row.getByRole('button', { name: c.title, exact: true }).click()
  await expect(page.locator('.case-context')).toContainText('17. Nov. 2026, 23:59')
  await expect(page.locator('.case-context')).toContainText('Europe/Dublin')
  await page.getByRole('navigation').getByRole('button', { name: 'Fristen', exact: true }).click()
  const card = page.locator('.deadline-card').filter({ hasText: incidentKey })
  await expect(card).toContainText('17. Nov. 2026, 23:59')
  await expect(card).not.toContainText('18. Nov. 2026')
  const after = await (await client.get(`/api/cases/${c.id}`)).json() as SafetyCase
  expect(after.deadlines[0].legalDue).toBe(c.deadlines[0].legalDue)
  await context.close()
  await client.dispose()
})
