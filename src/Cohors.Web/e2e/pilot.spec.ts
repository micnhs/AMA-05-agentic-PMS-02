import { expect, test, type APIRequestContext, type Locator, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import type { Psur, PsurGroupDefinition, RetentionRecord, RetentionView, SafetyCase } from '../src/types'

const home = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..')
const artifacts = process.env.COHORS_UI_ARTIFACT_DIR ?? path.join(home, '.local')
const accounts = Object.fromEntries(readFileSync(process.env.COHORS_TEST_ACCESS_FILE ?? path.join(home, '.local', 'demo-access.txt'), 'utf8').split(/\r?\n/)
  .filter(line => line.includes('.demo : ')).map(line => line.split(' : ')))

async function signIn(client: APIRequestContext, username: string) {
  if (!accounts[username]) throw new Error(`Explicit synthetic test account ${username} is missing. Use scripts\\Test-Ui.ps1 for the isolated browser profile.`)
  const session = await (await client.get('/api/auth/session')).json()
  const response = await client.post('/api/auth/login', { headers: { 'X-CSRF-Token': session.csrfToken },
    data: { username, password: accounts[username] } })
  expect(response.status()).toBe(200)
  return (await (await client.get('/api/auth/session')).json()).csrfToken as string
}
async function command<T = SafetyCase>(client: APIRequestContext, csrf: string, endpoint: string, data: object) {
  const response = await client.post(`/api${endpoint}`, { headers: { 'X-CSRF-Token': csrf, 'Idempotency-Key': crypto.randomUUID() }, data })
  const body = await response.json()
  expect(response.ok(), JSON.stringify(body)).toBeTruthy()
  return body as T
}
async function showCase(page: Page, incidentKey: string, title: string) {
  await page.getByRole('navigation').getByRole('button', { name: 'Fallakten', exact: true }).click()
  await page.getByRole('textbox', { name: 'Fälle durchsuchen' }).fill(incidentKey)
  await page.getByRole('button', { name: title, exact: true }).click()
}
async function submitDialog(dialog: Locator, name = 'Begründet bestätigen') {
  await dialog.getByRole('button', { name, exact: true }).click()
  await expect(dialog).not.toBeVisible()
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
  await page.screenshot({ path: path.join(artifacts, 'cohors-cockpit.png'), fullPage: true })
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
  await page.screenshot({ path: path.join(artifacts, 'cohors-workbench.png'), fullPage: true })

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

test('T16: grouped PSUR stays independently readable, editable and protected by every member scope', async ({ playwright, browser, baseURL }) => {
  test.setTimeout(240_000)
  const pms = await playwright.request.newContext({ baseURL })
  const medical = await playwright.request.newContext({ baseURL })
  const ra = await playwright.request.newContext({ baseURL })
  const limited = await playwright.request.newContext({ baseURL })
  await signIn(pms, 'pms.demo')
  await signIn(medical, 'medical.demo')
  await signIn(ra, 'ra.demo')
  await signIn(limited, 'pms-ortho.demo')
  const pmsContext = await browser.newContext({ storageState: await pms.storageState() })
  const medicalContext = await browser.newContext({ storageState: await medical.storageState() })
  const raContext = await browser.newContext({ storageState: await ra.storageState() })
  const limitedContext = await browser.newContext({ storageState: await limited.storageState() })
  const page = await pmsContext.newPage()
  const medicalPage = await medicalContext.newPage()
  const raPage = await raContext.newPage()
  const limitedPage = await limitedContext.newPage()
  const reference = `DEMO-PSUR-GROUP-UI-${Date.now()}`
  async function openWorkspace(target: Page, group?: string) {
    await target.goto(baseURL!)
    await target.getByRole('navigation').getByRole('button', { name: 'PSUR Workspace', exact: true }).click()
    if (group) await target.getByLabel('PSUR-Serie / versionierte Gruppierung').selectOption(group)
  }
  await openWorkspace(page)
  await page.getByRole('button', { name: 'Neue synthetische Gruppe', exact: true }).click()
  let dialog = page.getByRole('dialog')
  await dialog.locator('[name=reference]').fill(reference)
  await dialog.locator('[name=memberProductIds]').selectOption('cardio,ortho')
  await dialog.locator('[name=rationale]').fill('Ausschließlich synthetischer Browsernachweis einer gemeinsamen Berichtshülle: Beide Implantatfamilien bleiben separat auswertbar. Dies behauptet keine fachliche Eignung einer realen Produktkombination.')
  await dialog.locator('[name=scheduleBasis]').fill('Das festgelegte höchstklassige Leitprodukt ortho bestimmt den Kalender; bestehende Verpflichtungen anderer Serien bleiben ausdrücklich erhalten.')
  await submitDialog(dialog, 'Definition mit aktuellen Profilversionen binden')
  let group: PsurGroupDefinition | undefined
  await expect.poll(async () => {
    group = ((await (await pms.get('/api/psur-groups')).json()) as PsurGroupDefinition[]).find(g => g.reference === reference)
    return group?.members.length
  }).toBe(2)
  await page.getByLabel('PSUR-Serie / versionierte Gruppierung').selectOption(group!.id)
  await page.getByRole('button', { name: 'PSUR-Zyklus anlegen', exact: true }).click()
  dialog = page.getByRole('dialog')
  await dialog.locator('[name=mode]').selectOption('Replay')
  await submitDialog(dialog, 'Neue Fassung und expliziten Modus bestätigen')
  let psur: Psur | undefined
  await expect.poll(async () => {
    psur = ((await (await pms.get('/api/psurs')).json()) as Psur[]).find(p => p.snapshot.group?.definition.id === group!.id)
    return psur?.status
  }, { timeout: 90_000 }).toBe('Draft')
  const initial = psur!
  expect(initial.mode).toBe('Replay')
  expect(initial.snapshot.group?.members.map(m => m.product.productId).sort()).toEqual(['cardio', 'ortho'])
  await page.getByLabel('Basic-UDI-Einzelauswertung').selectOption('cardio')
  await expect(page.getByText('Die folgenden Tabellen und Dokumente zeigen ausschließlich cardio.', { exact: false })).toBeVisible()
  for (const format of ['json', 'pdf', 'zip'] as const) {
    const download = page.waitForEvent('download')
    await page.getByRole('button', { name: format.toUpperCase(), exact: true }).click()
    const file = await (await download).path()
    if (!file) throw new Error(`Authenticated ${format} export has no file.`)
    const bytes = readFileSync(file)
    expect(bytes.byteLength).toBeGreaterThan(100)
    if (format === 'json') {
      expect(bytes.toString('utf8')).toContain(initial.snapshotHash)
      expect(bytes.toString('utf8')).toContain(initial.snapshot.group!.members.find(m => m.product.productId === 'cardio')!.product.basicUdi)
    }
    if (format === 'pdf') expect(bytes.subarray(0, 5).toString()).toBe('%PDF-')
    if (format === 'zip') expect(bytes.subarray(0, 2).toString()).toBe('PK')
    expect((await limited.get(`/api/psurs/${initial.id}/export/${format}?version=${initial.version}`)).status()).toBe(403)
  }
  expect((await limited.get(`/api/psurs/${initial.id}`)).status()).toBe(403)
  expect(((await (await limited.get('/api/psurs')).json()) as Psur[]).some(p => p.id === initial.id)).toBeFalsy()
  await openWorkspace(limitedPage)
  await expect(limitedPage.getByLabel('PSUR-Serie / versionierte Gruppierung').locator('option').filter({ hasText: reference })).toHaveCount(0)

  async function approve(target: Page, client: APIRequestContext, expected: string) {
    await openWorkspace(target, group!.id)
    await target.getByRole('button', { name: 'Mit eigener Rolle freigeben', exact: true }).click()
    const approval = target.getByRole('dialog')
    await approval.locator('[name=reason]').fill('Eigenständig geprüfte synthetische Gruppenfassung: Quellen, getrennte Basic-UDI-Auswertungen, Unsicherheiten und unveränderte Snapshotbindung sind nachvollziehbar.')
    await submitDialog(approval)
    await expect.poll(async () => (await (await client.get(`/api/psurs/${initial.id}`)).json()).status).toBe(expected)
  }
  await approve(medicalPage, medical, 'MedicalApproved')
  await openWorkspace(page, group!.id)
  await page.getByRole('button', { name: 'Text kontrolliert nachbearbeiten', exact: true }).click()
  dialog = page.getByRole('dialog')
  const draft = await dialog.locator('[name=draft]').inputValue()
  expect(draft).toBe(initial.draft)
  const revised = `${draft}\n\nMenschliche Ergänzung: Die getrennten synthetischen Produktdaten erlauben keine gepoolte Inzidenz oder definitive klinische Kausalitätsaussage.`
  await dialog.locator('[name=draft]').fill(revised)
  await dialog.locator('[name=reason]').fill('Die medizinische Unsicherheitsdarstellung konkretisieren, ohne Quellen, Kennzahlen oder eingefrorene Daten zu verändern.')
  await submitDialog(dialog)
  await expect.poll(async () => {
    psur = await (await pms.get(`/api/psurs/${initial.id}`)).json() as Psur
    return psur.draft
  }).toBe(revised)
  expect(psur!.approvals).toHaveLength(0)
  expect(psur!.snapshotHash).toBe(initial.snapshotHash)
  expect(JSON.stringify(psur!.snapshot)).toBe(JSON.stringify(initial.snapshot))
  expect(psur!.scopeHash).not.toBe(initial.scopeHash)
  await approve(medicalPage, medical, 'MedicalApproved')
  await approve(raPage, ra, 'Approved')
  await raPage.getByRole('button', { name: 'BS-Antwort simulieren', exact: true }).click()
  dialog = raPage.getByRole('dialog')
  await dialog.locator('[name=reason]').fill('Ausschließlich simulierte Antwort nach zwei getrennten menschlichen Freigaben; keine reale Benannte Stelle oder Behördenakzeptanz.')
  await submitDialog(dialog)
  await expect(raPage.getByText('SIMULIERTE BS-ANTWORT', { exact: true })).toBeVisible()
  psur = await (await ra.get(`/api/psurs/${initial.id}`)).json() as Psur
  expect(new Set(psur.approvals.map(a => a.actorId)).size).toBe(2)
  expect(psur.snapshotHash).toBe(initial.snapshotHash)
  await raPage.screenshot({ path: path.join(artifacts, 'cohors-psur.png') })
  await Promise.all([pmsContext.close(), medicalContext.close(), raContext.close(), limitedContext.close()])
  await Promise.all([pms.dispose(), medical.dispose(), ra.dispose(), limited.dispose()])
})

test('T18: default preview profile blocks physical raw operations and preserves regulatory lifecycle evidence', async ({ playwright, browser, baseURL }) => {
  const client = await playwright.request.newContext({ baseURL })
  const csrf = await signIn(client, 'quality.demo')
  const context = await browser.newContext({ storageState: await client.storageState() })
  const page = await context.newPage()
  await page.goto(baseURL!)
  await page.getByRole('navigation').getByRole('button', { name: 'Aufbewahrung', exact: true }).click()
  await expect(page.getByText('Physische Löschung deaktiviert', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Testrohobjekt anlegen', exact: true })).toBeDisabled()
  const before = await (await client.get('/api/retention')).json() as RetentionRecord[]
  const denied = await client.post('/api/retention/raw', { headers: { 'X-CSRF-Token': csrf, 'Idempotency-Key': crypto.randomUUID() },
    data: { productId: 'ortho', reference: `DEMO-RAW-DISABLED-${Date.now()}`, syntheticReceivedAt: new Date().toISOString(),
      reason: 'Synthetischer Negativtest: Ein Client darf das unveränderte serverseitig deaktivierte physische Profil nicht einschalten.',
      physicalMode: 'SyntheticLocal' } })
  expect(denied.ok()).toBeFalsy()
  expect((await denied.json()).code).toBe('retention_profile_disabled')
  expect(await (await client.get('/api/retention')).json()).toEqual(before)
  await page.getByRole('button', { name: 'Lifecycle versionieren', exact: true }).click()
  let dialog = page.getByRole('dialog')
  await dialog.locator('[name=lastPlacingOnMarketLocalDate]').fill('2020-06-30')
  await dialog.locator('[name=confirmed]').selectOption('true')
  await dialog.locator('[name=sourceReference]').fill('DEMO-LAST-PLACING-PREVIEW')
  await dialog.locator('[name=reason]').fill('Bestätigtes synthetisches letztes Inverkehrbringen für das Mindestfristbeispiel dokumentieren; der Eingang beziehungsweise Upload wird nicht als Fristbeginn verwendet.')
  await submitDialog(dialog)
  await page.getByRole('button', { name: 'Dokument klassifizieren', exact: true }).click()
  dialog = page.getByRole('dialog')
  const documentReference = await dialog.locator('[name=source]').inputValue()
  await dialog.locator('[name=reason]').fill('Die konkrete genehmigte synthetische Evidenzversion nachvollziehbar klassifizieren; keine regulatorische Löschung oder produktive WORM-Aktivierung.')
  await submitDialog(dialog)
  await page.getByRole('button').filter({ hasText: documentReference }).click()
  await expect(page.getByText('Regulatorische Evidenz wird im Piloten nicht physisch gelöscht.', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Testbytes physisch löschen', exact: true })).toBeDisabled()
  const records = await (await client.get('/api/retention')).json() as RetentionRecord[]
  const evidence = records.find(r => r.reference === documentReference)!
  const view = await (await client.get(`/api/retention/${evidence.id}`)).json() as RetentionView
  expect(view.evaluation.minimumRetainThroughLocalDate).toBe('2035-06-30')
  expect(view.evaluation.canDeleteNow).toBe(false)
  await page.screenshot({ path: path.join(artifacts, 'cohors-retention-disabled.png'), fullPage: true })
  await context.close()
  await client.dispose()
})

test('T18: human lifecycle, hold and scoped raw deletion remain distinct from regulatory evidence',
  { tag: '@physical-retention' }, async ({ playwright, browser, baseURL }) => {
  test.setTimeout(120_000)
  const client = await playwright.request.newContext({ baseURL })
  await signIn(client, 'quality.demo')
  const context = await browser.newContext({ storageState: await client.storageState() })
  const page = await context.newPage()
  await page.goto(baseURL!)
  await page.getByRole('navigation').getByRole('button', { name: 'Aufbewahrung', exact: true }).click()
  await expect(page.getByText('Explizites synthetisches Lokalprofil', { exact: true })).toBeVisible()
  const reference = `DEMO-RAW-UI-${Date.now()}`
  await page.getByRole('button', { name: 'Testrohobjekt anlegen', exact: true }).click()
  let dialog = page.getByRole('dialog')
  await dialog.locator('[name=reference]').fill(reference)
  await dialog.locator('[name=syntheticReceivedAt]').fill(new Date(Date.now() - 8 * 86_400_000).toISOString())
  await dialog.locator('[name=reason]').fill('Ausschließlich servererzeugtes isoliertes Testrohobjekt zum Nachweis des ausdrücklich gewählten synthetischen Sieben-Tage-Profils.')
  await submitDialog(dialog)
  await page.getByRole('button').filter({ hasText: reference }).click()
  let record: RetentionRecord
  async function recordNow() {
    const records = await (await client.get('/api/retention')).json() as RetentionRecord[]
    record = records.find(r => r.reference === reference)!
    return record
  }
  await expect.poll(async () => (await recordNow()).status).toBe('Registered')
  await page.getByRole('button', { name: 'Hold setzen', exact: true }).click()
  dialog = page.getByRole('dialog')
  await dialog.locator('[name=reason]').fill('Synthetischen Hold setzen: Ein abgelaufenes Mindestintervall darf fortbestehende Prüf- und Beweissicherungsgründe nicht übergehen.')
  await submitDialog(dialog)
  await expect(page.getByRole('button', { name: 'Löschung autorisieren', exact: true })).toBeDisabled()
  await page.getByRole('button', { name: 'Hold aufheben', exact: true }).click()
  dialog = page.getByRole('dialog')
  await dialog.locator('[name=reason]').fill('Den kontrolliert gesetzten Testhold begründet aufheben; weitere Quellen-, Kopien- und Freigabeprüfungen bleiben erforderlich.')
  await submitDialog(dialog)
  await page.getByRole('button', { name: 'Löschung autorisieren', exact: true }).click()
  dialog = page.getByRole('dialog')
  await expect(dialog.locator('[name=externalCopiesReviewed]')).toHaveValue('false')
  await dialog.locator('[name=externalCopiesReviewed]').selectOption('true')
  await dialog.locator('[name=externalCopiesReview]').fill('Isolierter Browser-Testbereich ohne Exporte, Backup-, Cache- oder Agentenkopien dieses servererzeugten Rohobjekts; keine unbekannten Bindungen festgestellt.')
  await dialog.locator('[name=reason]').fill('Exakte synthetische Rohdatenbindung, abgelaufenes Testintervall, fehlende Referenzen und eigenständige Kopienprüfung menschlich bestätigen.')
  await submitDialog(dialog)
  await page.getByRole('button', { name: 'Testbytes physisch löschen', exact: true }).click()
  dialog = page.getByRole('dialog')
  await dialog.locator('[name=confirmReference]').fill(reference)
  await dialog.locator('[name=reason]').fill('Nur das explizit benannte synthetische Rohobjekt im isolierten Testprofil entfernen; regulatorische Belege und andere Daten bleiben erhalten.')
  await submitDialog(dialog)
  await expect.poll(async () => (await recordNow()).status).toBe('Deleted')
  await expect(page.getByText('Abgeschlossener lokaler Löschbeleg', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Lifecycle versionieren', exact: true }).click()
  dialog = page.getByRole('dialog')
  await dialog.locator('[name=lastPlacingOnMarketLocalDate]').fill('2020-06-30')
  await dialog.locator('[name=confirmed]').selectOption('true')
  await dialog.locator('[name=sourceReference]').fill('DEMO-LAST-PLACING-UI')
  await dialog.locator('[name=reason]').fill('Expliziter synthetischer Nachweis des letzten Inverkehrbringens, nicht des Uploads; nur das bestätigte Datum bestimmt das Mindestfristbeispiel.')
  await submitDialog(dialog)
  await page.getByRole('button', { name: 'Dokument klassifizieren', exact: true }).click()
  dialog = page.getByRole('dialog')
  const documentReference = await dialog.locator('[name=source]').inputValue()
  await dialog.locator('[name=reason]').fill('Eine konkrete genehmigte synthetische Dokumentversion dem Implantat-Mindestfristbeispiel zuordnen, ohne Inhalte oder Freigaben zu verändern.')
  await submitDialog(dialog)
  await page.getByRole('button').filter({ hasText: documentReference }).click()
  await expect(page.getByText('Regulatorische Evidenz wird im Piloten nicht physisch gelöscht.', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Testbytes physisch löschen', exact: true })).toBeDisabled()
  const records = await (await client.get('/api/retention')).json() as RetentionRecord[]
  const evidence = records.find(r => r.reference === documentReference)!
  const view = await (await client.get(`/api/retention/${evidence.id}`)).json() as RetentionView
  expect(view.evaluation.minimumRetainThroughLocalDate).toBe('2035-06-30')
  expect(view.evaluation.minimumIntervalElapsed).toBe(false)
  await page.screenshot({ path: path.join(artifacts, 'cohors-retention.png'), fullPage: true })
  await context.close()
  await client.dispose()
})
