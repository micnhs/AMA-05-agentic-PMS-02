import { expect, test, type APIRequestContext, type BrowserContext, type Locator, type Page } from '@playwright/test'
import { mkdirSync, readFileSync, rmdirSync, writeFileSync } from 'node:fs'
import { createHash } from 'node:crypto'
import { inflateRawSync } from 'node:zlib'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import type { Capa, CaseSummary, Evidence, PeriodicSummaryReport, ProductGovernance, Psur, PsurGroupDefinition, PsurReview, RetentionRecord, RetentionView, SafetyCase, SummaryAgreement } from '../src/types'

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

function readSmallAuditZip(bytes: Buffer) {
  const end = bytes.length - 22
  expect(bytes.readUInt32LE(end)).toBe(0x06054b50)
  expect(bytes.readUInt16LE(end + 20)).toBe(0)
  const count = bytes.readUInt16LE(end + 10)
  expect(count).toBeLessThan(0xffff)
  let position = bytes.readUInt32LE(end + 16)
  const entries = new Map<string, Buffer>()
  for (let i = 0; i < count; i++) {
    expect(bytes.readUInt32LE(position)).toBe(0x02014b50)
    const method = bytes.readUInt16LE(position + 10)
    const compressedSize = bytes.readUInt32LE(position + 20)
    const size = bytes.readUInt32LE(position + 24)
    const nameLength = bytes.readUInt16LE(position + 28)
    const name = bytes.subarray(position + 46, position + 46 + nameLength).toString('utf8')
    const local = bytes.readUInt32LE(position + 42)
    expect(bytes.readUInt32LE(local)).toBe(0x04034b50)
    const start = local + 30 + bytes.readUInt16LE(local + 26) + bytes.readUInt16LE(local + 28)
    const compressed = bytes.subarray(start, start + compressedSize)
    expect([0, 8]).toContain(method)
    const content = method === 8 ? inflateRawSync(compressed) : compressed
    expect(content.length).toBe(size)
    expect(entries.has(name)).toBe(false)
    entries.set(name, content)
    position += 46 + nameLength + bytes.readUInt16LE(position + 30) + bytes.readUInt16LE(position + 32)
  }
  expect(position).toBe(end)
  expect(entries.size).toBe(count)
  return entries
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
  await raPage.getByRole('button', { name: 'PSUR-Eingang simulieren', exact: true }).click()
  dialog = raPage.getByRole('dialog')
  await dialog.locator('[name=reason]').fill('Ausschließlich simulierte Antwort nach zwei getrennten menschlichen Freigaben; keine reale Benannte Stelle oder Behördenakzeptanz.')
  await submitDialog(dialog)
  await expect(raPage.getByText('SIMULIERTE EINGANGSQUITTUNG', { exact: true })).toBeVisible()
  psur = await (await ra.get(`/api/psurs/${initial.id}`)).json() as Psur
  expect(new Set(psur.approvals.map(a => a.actorId)).size).toBe(2)
  expect(psur.snapshotHash).toBe(initial.snapshotHash)
  await raPage.screenshot({ path: path.join(artifacts, 'cohors-psur.png') })
  await Promise.all([pmsContext.close(), medicalContext.close(), raContext.close(), limitedContext.close()])
  await Promise.all([pms.dispose(), medical.dispose(), ra.dispose(), limited.dispose()])
})

test('T16: BS questions retain multi-source replies, distinct reviews, action gates and historical group snapshots', async ({ playwright, browser, baseURL }) => {
  test.setTimeout(300_000)
  const pms = await playwright.request.newContext({ baseURL })
  const medical = await playwright.request.newContext({ baseURL })
  const ra = await playwright.request.newContext({ baseURL })
  const quality = await playwright.request.newContext({ baseURL })
  const limited = await playwright.request.newContext({ baseURL })
  const pmsCsrf = await signIn(pms, 'pms.demo')
  const medicalCsrf = await signIn(medical, 'medical.demo')
  const raCsrf = await signIn(ra, 'ra.demo')
  const qualityCsrf = await signIn(quality, 'quality.demo')
  await signIn(limited, 'pms-cardio.demo')
  const profiles: ProductGovernance[] = await (await pms.get('/api/products/profiles')).json()
  const profile = profiles.find(p => p.productId === 'cardio')
  if (!profile) throw new Error('Explicit synthetic Cardio lead profile is missing.')
  const group = await command<PsurGroupDefinition>(pms, pmsCsrf, '/psur-groups', { expectedVersion: 0,
    reference: `DEMO-PSUR-GROUP-BS-${Date.now()}`, leadProductId: 'cardio', memberProductIds: ['cardio', 'ortho'],
    firstPeriodStart: profile.firstPeriodStart,
    rationale: 'Ausschließlich synthetische Gruppierung für den BS-Rückfragenachweis. Beide Implantatfamilien bleiben separat auswertbar; keine Eignungsaussage zu realen Produktkombinationen.',
    scheduleBasis: 'Expliziter Jahresplan des festen Cardio-Leitprodukts; die bestehende Ortho-Leitserie und ihre Pflichten bleiben erhalten.' })
  const [year, month, day] = group.firstPeriodStart.split('-').map(Number)
  let report = await command<Psur>(pms, pmsCsrf, '/psurs', { productId: 'cardio', leadProductId: 'cardio',
    start: group.firstPeriodStart, end: new Date(Date.UTC(year + 1, month - 1, day - 1)).toISOString().slice(0, 10),
    region: 'Worldwide', groupId: group.id, groupVersion: group.version, mode: 'Replay' })
  const rationale = 'Synthetischer Nachweis: Originalbericht, getrennte Basic-UDI-Quellen, offene Unsicherheiten und konkrete Maßnahmen werden nachvollziehbar geprüft. Keine Risikoakzeptanz.'
  report = await command<Psur>(medical, medicalCsrf, `/psurs/${report.id}/approve`, { expectedVersion: report.version, reason: rationale })
  report = await command<Psur>(ra, raCsrf, `/psurs/${report.id}/approve`, { expectedVersion: report.version, reason: rationale })
  report = await command<Psur>(ra, raCsrf, `/psurs/${report.id}/simulate`, { expectedVersion: report.version, reason: rationale })
  const originalReport = JSON.stringify(report)
  const cases: CaseSummary[] = await (await pms.get('/api/cases')).json()
  const sourceCase = cases.find(c => c.productId === 'cardio' && c.synthetic)
  if (!sourceCase) throw new Error('A real persisted synthetic Cardio case is required for the action binding.')
  const owner = (await (await pms.get('/api/auth/session')).json()).actor.id
  let capa = await command<Capa>(pms, pmsCsrf, '/capas', { caseId: sourceCase.id, productId: 'cardio',
    title: 'Synthetische Nachweisprüfung zur BS-Rückfrage', action: 'Drei synthetische Soll/Ist-Vergleiche der Dokumentation nachvollziehbar durchführen.',
    rationale, ownerId: owner, effectivenessCriterion: 'Drei einzeln dokumentierte Folgeprüfungen ohne Wiederholung der konkret benannten Abweichung.',
    dueAt: new Date(Date.now() + 10 * 86400000).toISOString() })
  const pmsContext = await browser.newContext({ storageState: await pms.storageState() })
  const medicalContext = await browser.newContext({ storageState: await medical.storageState() })
  const raContext = await browser.newContext({ storageState: await ra.storageState() })
  const page = await pmsContext.newPage()
  const medicalPage = await medicalContext.newPage()
  const raPage = await raContext.newPage()
  async function openWorkspace(target: Page) {
    await target.goto(baseURL!)
    await target.getByRole('navigation').getByRole('button', { name: 'PSUR Workspace', exact: true }).click()
    await target.getByLabel('Produktfamilie / festes Leitprodukt').selectOption('cardio')
    await target.getByLabel('PSUR-Serie / versionierte Gruppierung').selectOption(group.id)
    return target.getByRole('region', { name: 'BS-Rückfragen und Maßnahmen', exact: true })
  }
  let panel = await openWorkspace(raPage)
  await panel.getByRole('button', { name: 'BS-Rückfrage erfassen', exact: true }).click()
  let dialog = raPage.getByRole('dialog')
  const reference = `DEMO-BS-UI-${Date.now()}`
  const due = new Date(Date.now() + 2 * 86400000).toISOString()
  await dialog.locator('[name=reference]').fill(reference)
  await dialog.locator('[name=question]').fill('SYNTHETISCH: Die getrennte Basic-UDI-Auswertung und die damit verknüpfte Dokumentationsmaßnahme konkret begründen; keine gepoolte Inzidenz behaupten.')
  await dialog.locator('[name=dueAt]').fill(due)
  await dialog.locator('[name=scheduleBasis]').fill('Nur für diese synthetische Browserprüfung ausdrücklich vereinbarter Termin; keine pauschale gesetzliche Tagesfrist.')
  await dialog.locator('[name=actionRequired]').selectOption('true')
  await dialog.locator('[name=reason]').fill(rationale)
  await submitDialog(dialog)
  let review: PsurReview
  async function loadReview() {
    const values: PsurReview[] = await (await pms.get(`/api/psurs/${report.id}/reviews`)).json()
    const value = values.find(r => r.reference === reference)
    if (!value) throw new Error('The visible synthetic question was not persisted.')
    review = value
    return value
  }
  await expect.poll(async () => (await loadReview()).status).toBe('QuestionOpen')
  expect((await limited.get(`/api/psur-reviews/${review!.id}`)).status()).toBe(403)
  panel = await openWorkspace(page)
  await panel.getByRole('button', { name: 'BS-Antwort bearbeiten', exact: true }).click()
  dialog = page.getByRole('dialog')
  const response = 'SYNTHETISCHE ANTWORT: Die ausgewählten versionierten Quellen belegen die getrennte Darstellung beider Produktfamilien. Die konkrete Dokumentationsprüfung bleibt eine offene Maßnahme bis zum unabhängigen Wirksamkeitsnachweis; keine definitive klinische Kausalität.'
  const sources = report.snapshot.group!.members.map(m => m.snapshot.directory!.entries.find(e => e.kind === 'evidence')!)
  const sourceKeys = sources.map(s => `${report.id}:${s.kind}:${s.source.id}@${s.source.version}`)
  await dialog.locator('[name=response]').fill(response)
  await dialog.locator('[name=sources]').selectOption(sourceKeys)
  await dialog.locator('[name=actions]').selectOption(`capa:${capa.id}`)
  await dialog.locator('[name=reason]').fill(rationale)
  await submitDialog(dialog)
  await expect.poll(async () => (await loadReview()).status).toBe('ReplyDraft')
  expect(review!.draft!.sources).toHaveLength(2)
  expect(review!.draft!.actions[0].source.version).toBe(capa.version)
  async function approve(target: Page, expected: string) {
    const region = await openWorkspace(target)
    await region.getByRole('button', { name: 'BS-Antwort mit eigener Rolle freigeben', exact: true }).click()
    const modal = target.getByRole('dialog')
    await modal.locator('[name=reason]').fill(rationale)
    await submitDialog(modal)
    await expect.poll(async () => (await loadReview()).status).toBe(expected)
  }
  await approve(medicalPage, 'MedicalApproved')
  const oldScope = review!.scopeHash
  panel = await openWorkspace(page)
  await panel.getByRole('button', { name: 'BS-Antwort bearbeiten', exact: true }).click()
  dialog = page.getByRole('dialog')
  expect(await dialog.locator('[name=sources]').evaluate(element => {
    if (!(element instanceof HTMLSelectElement)) throw new Error('Expected the actual native source selector.')
    return Array.from(element.selectedOptions).map(option => option.value)
  })).toEqual(sourceKeys)
  await dialog.locator('[name=response]').fill(response + ' Medizinische Präzisierung: Der synthetische Wirksamkeitsnachweis ist getrennt zu beurteilen.')
  await dialog.locator('[name=reason]').fill(rationale)
  await submitDialog(dialog)
  await loadReview()
  expect(review!.approvals).toHaveLength(0)
  expect(review!.scopeHash).not.toBe(oldScope)
  expect(review!.draft!.sources).toHaveLength(2)
  await approve(medicalPage, 'MedicalApproved')
  await approve(raPage, 'ReplyApproved')
  panel = raPage.getByRole('region', { name: 'BS-Rückfragen und Maßnahmen', exact: true })
  await panel.getByRole('button', { name: 'BS-Antwortempfang simulieren', exact: true }).click()
  dialog = raPage.getByRole('dialog')
  await dialog.locator('[name=reason]').fill(rationale)
  await submitDialog(dialog)
  await expect.poll(async () => (await loadReview()).status).toBe('ReplySubmitted')
  expect(new Set(review!.submissions[0].approvals.map(a => a.actorId)).size).toBe(2)
  expect(review!.assessments).toHaveLength(0)
  await panel.getByRole('button', { name: 'BS-Bewertung dokumentieren', exact: true }).click()
  dialog = raPage.getByRole('dialog')
  await dialog.locator('[name=outcome]').selectOption('NoFurtherQuestion')
  await dialog.locator('[name=reason]').fill(rationale)
  await dialog.getByRole('button', { name: 'Begründet bestätigen', exact: true }).click()
  await expect(dialog.getByRole('alert')).toContainText('Offene oder unwirksame')
  await dialog.getByRole('button', { name: 'Abbrechen', exact: true }).click()
  expect((await loadReview()).status).toBe('ReplySubmitted')
  capa = await command<Capa>(quality, qualityCsrf, `/capas/${capa.id}/verify`, { expectedVersion: capa.version,
    evidence: 'Synthetischer Prüfbericht: Alle drei einzeln dokumentierten Soll/Ist-Vergleiche erfüllen das zuvor festgelegte Kriterium ohne die beanstandete Abweichung.',
    effective: true, reason: rationale })
  await panel.getByRole('button', { name: 'BS-Bewertung dokumentieren', exact: true }).click()
  dialog = raPage.getByRole('dialog')
  await dialog.locator('[name=outcome]').selectOption('NoFurtherQuestion')
  await dialog.locator('[name=reason]').fill(rationale)
  await submitDialog(dialog)
  await expect.poll(async () => (await loadReview()).status).toBe('QuestionClosed')
  expect(review!.assessments[0].actionEvidence[0].source.version).toBe(capa.version)
  expect(new Date(review!.questions[0].dueAt).toISOString()).toBe(due)
  expect(JSON.stringify(await (await pms.get(`/api/psurs/${report.id}`)).json())).toBe(originalReport)
  const download = raPage.waitForEvent('download')
  await panel.getByRole('button', { name: 'BS-Vorgang als JSON', exact: true }).click()
  const file = await (await download).path()
  if (!file) throw new Error('No authenticated BS correspondence export file was produced.')
  const exported = JSON.parse(readFileSync(file, 'utf8'))
  expect(exported.format).toBe('cohors-psur-review-export-1')
  expect(exported.review.originalReport.snapshotHash).toBe(report.snapshotHash)
  expect(exported.review.status).toBe('QuestionClosed')
  expect(exported.boundary).toContain('SYNTHETISCH')
  expect((await limited.get(`/api/psur-reviews/${review!.id}/export?version=${review!.version}`)).status()).toBe(403)
  await expect(panel.getByRole('heading', { name: 'Letzte ausdrücklich dokumentierte BS-Bewertung', exact: true })).toBeVisible()
  await expect(panel.getByText(/Bei dieser Bewertung geprüft:.*Verified/)).toBeVisible()
  await expect(panel.getByRole('heading', { name: 'Eingefrorene eingereichte Antwort', exact: true })).toBeVisible()
  await panel.scrollIntoViewIfNeeded()
  await panel.screenshot({ path: path.join(artifacts, 'cohors-psur-bs-cycle.png') })
  await openWorkspace(page)
  await page.getByRole('button', { name: 'Neue Fassung derselben Periode', exact: true }).click()
  dialog = page.getByRole('dialog')
  await dialog.locator('[name=revisionReason]').fill('Den später dokumentierten synthetischen BS-Vorgang mit seinen unveränderten Quellen und Maßnahmen in einem neuen Snapshot binden; keine Rückdatierung des Stichtagsstatus.')
  await submitDialog(dialog, 'Neue Fassung und expliziten Modus bestätigen')
  let next: Psur | undefined
  await expect.poll(async () => {
    const reports: Psur[] = await (await pms.get('/api/psurs')).json()
    next = reports.find(p => p.snapshot.series?.reference === group.reference && p.snapshot.series.reportVersion === 2)
    return next?.status
  }).toBe('Draft')
  if (!next) throw new Error('The visible refreeze did not produce a separate PSUR edition.')
  const history = next.snapshot.group?.regulatoryHistory
  expect(history?.reviews).toHaveLength(1)
  expect(history!.reviews[0].currentSource).toMatchObject({ id: review!.id, version: review!.version })
  expect(history!.reviews[0].selection).toBe('PostCutoffAddendum')
  expect(history!.reviews[0].statusAtCutoff).toBe('NotRecordedAtCutoff')
  expect(history!.reviews[0].currentAtFreeze.status).toBe('QuestionClosed')
  expect(next.snapshot.group!.members.every(m => m.snapshot.regulatoryHistory?.reviews.length === 0)).toBe(true)
  expect(next.draft).toContain(`[psurReview:${review!.id}@${review!.version}]`)
  expect(next.approvals).toHaveLength(0)
  expect(JSON.stringify(await (await pms.get(`/api/psurs/${report.id}`)).json())).toBe(originalReport)
  const frozenPanel = page.getByRole('region', { name: 'Eingefrorene BS-Historie', exact: true })
  await frozenPanel.getByText(`${reference} · Stichtag NotRecordedAtCutoff · Freeze QuestionClosed`, { exact: true }).click()
  await expect(frozenPanel.getByText(/PostCutoffAddendum:/)).toBeVisible()
  await expect(frozenPanel.getByText(/Damals geprüfter Zustand:.*Verified/)).toBeVisible()
  const snapshotDownload = page.waitForEvent('download')
  await page.getByRole('button', { name: 'JSON', exact: true }).click()
  const snapshotFile = await (await snapshotDownload).path()
  if (!snapshotFile) throw new Error('No authenticated snapshot export was downloaded.')
  const nextExport = JSON.parse(readFileSync(snapshotFile, 'utf8')) as { report: Psur }
  expect(nextExport.report.snapshotHash).toBe(next.snapshotHash)
  expect(nextExport.report.snapshot.group!.regulatoryHistory).toEqual(history)
  expect((await limited.get(`/api/psurs/${next.id}/export/json?version=${next.version}`)).status()).toBe(403)
  const pdfDownload = page.waitForEvent('download')
  await page.getByRole('button', { name: 'PDF', exact: true }).click()
  const pdfFile = path.join(artifacts, 'cohors-psur-bs-snapshot.pdf')
  await (await pdfDownload).saveAs(pdfFile)
  expect(readFileSync(pdfFile).subarray(0, 5).toString('ascii')).toBe('%PDF-')
  await frozenPanel.screenshot({ path: path.join(artifacts, 'cohors-psur-bs-snapshot.png') })
  await Promise.all([pmsContext.close(), medicalContext.close(), raContext.close()])
  await Promise.all([pms.dispose(), medical.dispose(), ra.dispose(), quality.dispose(), limited.dispose()])
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

test('L03: auditor downloads complete product history and original bytes; member-only scope is denied', async ({ playwright, browser, baseURL }) => {
  test.setTimeout(120_000)
  const pms = await playwright.request.newContext({ baseURL })
  const auditor = await playwright.request.newContext({ baseURL })
  const limited = await playwright.request.newContext({ baseURL })
  const pmsCsrf = await signIn(pms, 'pms.demo')
  await signIn(auditor, 'auditor.demo')
  await signIn(limited, 'pms-cardio.demo')
  const profiles = await (await pms.get('/api/products/profiles')).json() as ProductGovernance[]
  const profile = profiles.find(p => p.productId === 'ortho')!
  const group = await command<PsurGroupDefinition>(pms, pmsCsrf, '/psur-groups', {
    expectedVersion: 0, reference: `DEMO-PSUR-GROUP-AUDIT-UI-${Date.now()}`, leadProductId: 'ortho',
    memberProductIds: ['ortho', 'cardio'], firstPeriodStart: profile.firstPeriodStart,
    rationale: 'Synthetische Mehrfamilien-Testdefinition für den vollständigen historischen Auditscope; keine reale Produktgruppierung.',
    scheduleBasis: 'Unveränderter synthetischer Zeitplananker; keine neue Einreichung oder Verschiebung bestehender Pflichten.'
  })
  const denied = await limited.get('/api/products/cardio/audit')
  expect(denied.status()).toBe(403)
  expect(denied.headers()['content-type']).toContain('json')
  const context = await browser.newContext({ storageState: await auditor.storageState(), viewport: { width: 1512, height: 982 } })
  try {
    const page = await context.newPage()
    await page.goto(baseURL!)
    await expect(page.getByRole('button', { name: 'Beschwerde aufnehmen', exact: true })).toHaveCount(0)
    await page.getByRole('navigation').getByRole('button', { name: 'Audit Explorer', exact: true }).click()
    await expect(page.getByRole('heading', { name: 'Produktbezogenes Auditpaket', exact: true })).toBeVisible()
    await page.getByLabel('Audit-Produktfamilie').selectOption('cardio')
    const waiting = page.waitForEvent('download')
    await page.getByRole('button', { name: 'Vollständiges Produkt-Auditpaket (ZIP)', exact: true }).click()
    const download = await waiting
    const file = path.join(artifacts, 'cohors-product-audit.zip')
    await download.saveAs(file)
    await expect(page.getByRole('status').filter({ hasText: 'Geprüfte lokale Lesekopie heruntergeladen' })).toBeVisible()
    const entries = readSmallAuditZip(readFileSync(file))
    const checksums = JSON.parse(entries.get('checksums.json')!.toString('utf8')) as Record<string, string>
    expect(Object.keys(checksums)).toHaveLength(entries.size - 1)
    for (const [name, digest] of Object.entries(checksums))
      expect(createHash('sha256').update(entries.get(name)!).digest('hex')).toBe(digest)
    const manifest = JSON.parse(entries.get('manifest.json')!.toString('utf8')) as {
      contract: string; requestedProductId: string; products: string[];
      aggregates: { id: string; kind: string; products: string[]; versions: { version: number; hash: string; path: string }[] }[];
      objects: { hash: string; path: string; uses: string[] }[]
    }
    expect(manifest.contract).toBe('cohors-product-audit-1')
    expect(manifest.requestedProductId).toBe('cardio')
    expect(manifest.products.sort()).toEqual(['cardio', 'ortho'])
    const definition = manifest.aggregates.find(a => a.id === group.id)!
    expect(definition.kind).toBe('psurGroup')
    expect(definition.products.sort()).toEqual(['cardio', 'ortho'])
    expect(JSON.parse(entries.get(definition.versions[0].path)!.toString('utf8'))).toEqual(group)
    expect(manifest.objects.length).toBeGreaterThan(0)
    for (const source of manifest.objects) {
      expect(createHash('sha256').update(entries.get(source.path)!).digest('hex')).toBe(source.hash)
      expect(source.uses.length).toBeGreaterThan(0)
    }
    for (const aggregate of manifest.aggregates)
      for (const version of aggregate.versions)
        expect(createHash('sha256').update(entries.get(version.path)!).digest('hex')).toBe(version.hash)
    const verification = JSON.parse(entries.get('verification.json')!.toString('utf8'))
    expect(verification.state).toBe('VerifiedLocalReadOnlyCut')
    expect(verification.externalLedgerDigestVerification).toContain('Not performed')
    expect(verification.recovery).toContain('Not performed')
    await page.screenshot({ path: path.join(artifacts, 'cohors-product-audit.png'), fullPage: true })
  } finally {
    await context.close()
    await pms.dispose(); await auditor.dispose(); await limited.dispose()
  }
})

test('C12: separate periodic summary uses an explicit agreement, distinct human reviews and a labelled export without closing case deadlines', async ({ playwright, browser, baseURL }) => {
  test.setTimeout(180_000)
  const pms = await playwright.request.newContext({ baseURL })
  const medical = await playwright.request.newContext({ baseURL })
  const ra = await playwright.request.newContext({ baseURL })
  const limited = await playwright.request.newContext({ baseURL })
  const contexts: BrowserContext[] = []
  const reason = 'SYNTHETISCH: Die konkret gebundenen Fall-, Quellen- und Vereinbarungsfassungen werden menschlich geprüft. Keine klinische Wahrheitsbehauptung, keine reale Übermittlung und keine Aufhebung einer Einzelpflicht.'
  try {
    const pmsCsrf = await signIn(pms, 'pms.demo')
    const medicalCsrf = await signIn(medical, 'medical.demo')
    const raCsrf = await signIn(ra, 'ra.demo')
    await signIn(limited, 'pms-cardio.demo')
    const today = new Date().toISOString().slice(0, 10)
    const day = (offset: number) => new Date(Date.parse(`${today}T12:00:00Z`) + offset * 86_400_000).toISOString().slice(0, 10)
    const start = day(-7), end = day(-1), eventDay = day(-6)
    const sourceDocuments = await (await ra.get('/api/evidence')).json() as Evidence[]
    const evidence = sourceDocuments.find(e => e.productId === 'ortho' && e.kind === 'RiskFile' && e.approved && e.synthetic && e.validFrom < `${day(-8)}T12:00:00Z`)
    if (!evidence) throw new Error('A genuinely earlier valid synthetic RiskFile is required; do not backdate the prerequisite fixture.')
    let c = await command(pms, pmsCsrf, '/cases', {
      incidentKey: `UI-SUMMARY-${Date.now()}`, sourceId: crypto.randomUUID(), title: 'Synthetischer Fall für eine getrennte Sammelmeldung',
      description: reason, productId: 'ortho', udi: null, batch: 'DEMO-SUMMARY-LOT', country: 'IE', jurisdiction: 'EU-IE',
      seriousness: 'OtherSerious', occurredAt: `${eventDay}T08:00:00Z`, awarenessAt: `${day(-5)}T10:00:00Z`,
      scenario: 'ui-summary', synthetic: true,
    })
    c = await command(medical, medicalCsrf, `/cases/${c.id}/information`, {
      expectedVersion: c.version, question: 'Welche gezielten synthetischen Revisionsbefunde grenzen die offenen Gegenhypothesen ein?',
      minimalData: 'Minimaler synthetischer Revisionsbefund ohne Patientendaten', relevance: reason,
      recipientRole: 'Simulierter klinischer Ansprechpartner', manufacturerBasis: reason, disclosureBasis: reason,
      dueAt: `${day(7)}T12:00:00Z`,
    })
    const requestId = c.informationRequests.at(-1)!.id
    c = await command(medical, medicalCsrf, `/cases/${c.id}/information/${requestId}/approve`, { expectedVersion: c.version, reason })
    c = await command(ra, raCsrf, `/cases/${c.id}/information/${requestId}/approve`, { expectedVersion: c.version, reason })
    c = await command(pms, pmsCsrf, `/cases/${c.id}/information/respond`, {
      expectedVersion: c.version, requestId, response: 'SYNTHETISCH: Gezielter Revisionsbefund liegt vor. Mechanische und klinische Gegenhypothesen bleiben getrennt zu prüfen; keine definitive Kausalitätsbehauptung.', reason,
    })
    c = await command(pms, pmsCsrf, `/cases/${c.id}/assessment`, { expectedVersion: c.version, mode: 'Replay', reason })
    await expect.poll(async () => {
      c = await (await pms.get(`/api/cases/${c.id}`)).json()
      return c.currentAssessment?.quality.findings.every(f => !['Major', 'Critical'].includes(f.severity)) && c.agentRuns.at(-1)?.status === 'Completed'
    }).toBe(true)
    c = await command(pms, pmsCsrf, `/cases/${c.id}/report`, { expectedVersion: c.version, kind: 'Initial',
      jurisdiction: 'EU-IE', language: 'de', preliminary: true, openQuestions: reason, reason })
    c = await command(medical, medicalCsrf, `/cases/${c.id}/mir`, { expectedVersion: c.version, reason, facts: {
      eventDateFrom: eventDay, eventDateTo: eventDay, nextReportDate: day(7), eventClassification: 'All other reportable incidents',
      deviceProblemCode: 'ZZZ', deviceLocation: 'Unknown', preliminaryAnalysis: reason, initialCorrectiveAction: reason,
      furtherInvestigation: reason, evidenceIds: c.evidence.filter(e => e.approved).map(e => e.id),
      rationale: `${reason} ZZZ ist ausschließlich eine strukturgeeignete Testkennung, kein fachlich validierter IMDRF-Code.`,
    } })
    c = await command(medical, medicalCsrf, `/cases/${c.id}/approve/medical`, { expectedVersion: c.version, reason, findingDisposition: reason })
    c = await command(ra, raCsrf, `/cases/${c.id}/approve/regulatory`, { expectedVersion: c.version, reason })
    const before = c
    const raContext = await browser.newContext({ storageState: await ra.storageState(), timezoneId: 'UTC', viewport: { width: 1512, height: 982 } })
    contexts.push(raContext)
    const page = await raContext.newPage()
    const openDesk = async (target: Page) => {
      await target.goto(baseURL!)
      await target.getByRole('navigation').getByRole('button', { name: 'Regulatory Desk', exact: true }).click()
      await expect(target.getByRole('heading', { name: 'Periodische Sammelmeldungen', exact: true })).toBeVisible()
    }
    await openDesk(page)
    await page.getByRole('button', { name: 'Sammelvereinbarung dokumentieren', exact: true }).click()
    let dialog = page.getByRole('dialog')
    const reference = `DEMO-UI-SUMMARY-${Date.now()}`
    await dialog.locator('[name=productId]').selectOption('ortho')
    await dialog.locator('[name=reference]').fill(reference)
    await dialog.locator('[name=authorityName]').selectOption('HPRA-SIMULATOR')
    await dialog.locator('[name=jurisdiction]').selectOption('EU-IE')
    await dialog.locator('[name=language]').selectOption('de')
    await dialog.locator('[name=agreementText]').fill(`${reason} Das ausdrücklich gewählte JSON-Pilotformat mit Fallübersicht, Quellen, Untersuchungsstand und Maßnahmen wird alle sieben Kalendertage rein synthetisch vereinbart.`)
    await dialog.locator('[name=contentRequirements]').fill(`${reason} Gefordert sind getrennte Fallfassungen, Quellen, begründete Gemeinsamkeiten und ausdrücklich offengelegte Lücken.`)
    await dialog.locator('[name=frequencyDays]').fill('7')
    await dialog.locator('[name=validFrom]').fill(start)
    await dialog.locator('[name=validThrough]').fill(end)
    await dialog.locator('[name=agreedAt]').fill(`${day(-8)}T12:00`)
    await dialog.locator('[name=prerequisite]').selectOption('CommonAndWellDocumented')
    await dialog.locator('[name=prerequisiteRationale]').fill(reason)
    await dialog.locator('[name=document]').selectOption(`${evidence.id}@${evidence.version}`)
    await dialog.locator('[name=reason]').fill(reason)
    await dialog.getByRole('button', { name: 'Begründet bestätigen', exact: true }).click()
    await expect(dialog.getByRole('alert')).toContainText('noch gueltigem Folgetag')
    expect(((await (await ra.get('/api/summary-agreements')).json()) as SummaryAgreement[]).some(a => a.reference === reference)).toBe(false)
    await dialog.locator('[name=validThrough]').fill(today)
    await submitDialog(dialog)
    const agreement = (await (await ra.get('/api/summary-agreements')).json() as SummaryAgreement[]).find(a => a.reference === reference)
    if (!agreement) throw new Error('The UI did not persist its explicit summary agreement.')
    expect(agreement.validThrough).toBe(today)
    const card = page.locator('article.source-run').filter({ has: page.getByRole('heading', { name: `${reference} · v1`, exact: true }) })
    await expect(card).toContainText('Letzter berichtsfähiger Zeitraum:')
    await expect(card).toContainText('Ein erst am Ablauftag endendes Intervall ist nicht mehr berichtsfähig.')
    await card.getByRole('radio').check()
    await page.getByRole('checkbox', { name: new RegExp(c.reference) }).check()
    await page.getByRole('button', { name: 'Sammelentwurf erstellen', exact: true }).click()
    dialog = page.getByRole('dialog')
    await dialog.locator('[name=start]').fill(start)
    await dialog.locator('[name=end]').fill(end)
    await expect(dialog.locator('[name^="similarity-"]')).toHaveCount(1)
    await dialog.locator('[name^="similarity-"]').fill(reason)
    for (const name of ['incidentOverview', 'investigationFindings', 'benefitRiskAndActions', 'agreementComplianceAndGaps', 'reason'])
      await dialog.locator(`[name=${name}]`).fill(reason)
    await submitDialog(dialog)
    const load = async () => {
      const report = (await (await ra.get('/api/summary-reports')).json() as PeriodicSummaryReport[]).find(r => r.agreement.id === agreement.id)
      if (!report) throw new Error('The separately identified summary report was not persisted.')
      return report
    }
    let report = await load()
    const forged = await pms.post(`/api/summary-reports/${report.id}/approve`, {
      headers: { 'X-CSRF-Token': pmsCsrf, 'Idempotency-Key': crypto.randomUUID() },
      data: { expectedVersion: report.version, reason, role: 'MedicalSafetyReviewer' },
    })
    expect(forged.status()).toBe(403)
    expect((await limited.get(`/api/summary-reports/${report.id}`)).status()).toBe(403)
    const medicalContext = await browser.newContext({ storageState: await medical.storageState() })
    contexts.push(medicalContext)
    const medicalPage = await medicalContext.newPage()
    await openDesk(medicalPage)
    await medicalPage.getByTestId(`summary-${report.id}`).getByRole('button', { name: 'Sammelmeldung fachlich freigeben', exact: true }).click()
    await medicalPage.getByRole('dialog').locator('[name=reason]').fill(reason)
    await submitDialog(medicalPage.getByRole('dialog'))
    expect((await load()).status).toBe('MedicalApproved')
    await openDesk(page)
    await page.getByTestId(`summary-${report.id}`).getByRole('button', { name: 'Sammelmeldung fachlich freigeben', exact: true }).click()
    await page.getByRole('dialog').locator('[name=reason]').fill(reason)
    await submitDialog(page.getByRole('dialog'))
    report = await load()
    expect(report.status).toBe('Frozen')
    expect(new Set(report.approvals.map(a => a.actorId)).size).toBe(2)
    const frozenJson = report.frozenJson
    await page.getByTestId(`summary-${report.id}`).getByRole('button', { name: 'Sammelmeldung simulieren', exact: true }).click()
    await page.getByRole('dialog').locator('[name=reason]').fill(reason)
    await submitDialog(page.getByRole('dialog'))
    report = await load()
    expect(report.status).toBe('SimulatedAccepted')
    expect(report.frozenJson).toBe(frozenJson)
    const download = page.waitForEvent('download')
    await page.getByTestId(`summary-${report.id}`).getByRole('button', { name: 'Gekennzeichnetes Sammelpaket exportieren', exact: true }).click()
    const file = await (await download).path()
    if (!file) throw new Error('No authenticated summary export bytes were downloaded.')
    const bytes = readFileSync(file)
    const exported = JSON.parse(bytes.toString('utf8')) as { boundary: string; report: PeriodicSummaryReport }
    expect(exported.boundary).toContain('Kein MIR, kein PSUR')
    expect(exported.report.frozenJson).toBe(frozenJson)
    expect(createHash('sha256').update(exported.report.frozenJson!, 'utf8').digest('hex')).toBe(report.scopeHash)
    expect(exported.report.agreementSource.version).toBe(agreement.version)
    const current = await (await pms.get(`/api/cases/${c.id}`)).json() as SafetyCase
    expect(current).toEqual(before)
    expect(current.deadlines.every(d => d.status === 'Open')).toBe(true)
    expect((await limited.get(`/api/summary-reports/${report.id}/export`)).status()).toBe(403)
    writeFileSync(path.join(artifacts, 'cohors-periodic-summary.json'), bytes)
    await page.screenshot({ path: path.join(artifacts, 'cohors-periodic-summary.png'), fullPage: true })
  } finally {
    for (const context of contexts) await context.close()
    await Promise.all([pms, medical, ra, limited].map(client => client.dispose()))
  }
})

test('T18: human lifecycle, hold, native I/O diagnosis and controlled raw reconciliation remain distinct from regulatory evidence',
  { tag: '@physical-retention' }, async ({ playwright, browser, baseURL }) => {
  test.setTimeout(120_000)
  if (!process.env.COHORS_UI_ARTIFACT_DIR || !process.env.COHORS_TEST_ACCESS_FILE || new URL(baseURL!).port === '5187')
    throw new Error('Physical fault fixture requires the explicit isolated Test-Ui runner, never the published preview.')
  const isolated = path.join(path.resolve(artifacts), 'runtime', '.local')
  expect(path.resolve(process.env.COHORS_TEST_ACCESS_FILE)).toBe(path.join(isolated, 'demo-access.txt'))
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
  const authorized = await recordNow()
  expect(authorized.status).toBe('Authorized')
  expect(authorized.object.hash).toMatch(/^[a-f0-9]{64}$/)
  expect(authorized.object.objectKey).toBe(`raw-quarantine/${authorized.object.hash}`)
  const rawPath = path.join(isolated, 'blobs', 'raw-quarantine', authorized.object.hash)
  const original = readFileSync(rawPath)
  expect(createHash('sha256').update(original).digest('hex')).toBe(authorized.object.hash)
  const collision = path.join(isolated, 'blobs', '.deletion-receipts', `${authorized.object.hash}.json`)
  mkdirSync(path.dirname(collision), { recursive: true })
  mkdirSync(collision)
  let failure: RetentionRecord
  try {
    await page.getByRole('button', { name: 'Testbytes physisch löschen', exact: true }).click()
    dialog = page.getByRole('dialog')
    await dialog.locator('[name=confirmReference]').fill(reference)
    await dialog.locator('[name=reason]').fill('Den isolierten physischen Fehlerpfad prüfen; ein fehlgeschlagener Journalschritt darf keine erfolgreiche Entfernung vortäuschen.')
    const responsePromise = page.waitForResponse(r => r.request().method() === 'POST' && r.url().endsWith(`/retention/${authorized.id}/delete`))
    await dialog.getByRole('button', { name: 'Begründet bestätigen', exact: true }).click()
    const response = await responsePromise
    expect(response.ok()).toBe(false)
    expect((await response.json()).code).toBe('retention_incomplete')
    await expect(dialog).toBeVisible()
    await dialog.getByRole('button', { name: 'Abbrechen', exact: true }).click()
    await expect.poll(async () => (await recordNow()).status).toBe('DeletionFailed')
    failure = await recordNow()
    expect(failure.errorCode).toBe('retention_object_io')
    expect(failure.deletion).toBeNull()
    expect(failure.objectIoFailures).toHaveLength(1)
    const detail = failure.objectIoFailures![0]
    expect(detail.step).toBe('PrepareJournal')
    expect(['IOException', 'UnauthorizedAccessException']).toContain(detail.exceptionType)
    expect(detail.hResult).toMatch(/^0x[0-9A-F]{8}$/)
    expect(Object.keys(detail).sort()).toEqual(['exceptionType', 'hResult', 'step'])
    expect(readFileSync(rawPath)).toEqual(original)
    await expect(page.getByText(`I/O-Schritt: ${detail.step} · ${detail.exceptionType} · HRESULT ${detail.hResult}`, { exact: true })).toBeVisible()
    await page.screenshot({ path: path.join(artifacts, 'cohors-retention-io-failed.png'), fullPage: true })
  } finally { rmdirSync(collision) }
  await page.getByRole('button', { name: 'Teilvorgang abgleichen', exact: true }).click()
  dialog = page.getByRole('dialog')
  await dialog.locator('[name=confirmReference]').fill(reference)
  await dialog.locator('[name=reason]').fill('Nur das explizit benannte synthetische Rohobjekt im isolierten Testprofil entfernen; regulatorische Belege und andere Daten bleiben erhalten.')
  await submitDialog(dialog)
  await expect.poll(async () => (await recordNow()).status).toBe('Deleted')
  const reconciled = await recordNow()
  expect(reconciled.attempts).toBe(failure.attempts + 1)
  expect(reconciled.objectIoFailures).toBeUndefined()
  expect(reconciled.deletion!.operationId).toBe(authorized.authorization!.id)
  writeFileSync(path.join(artifacts, 'cohors-retention-io.json'), JSON.stringify({
    scope: 'Actual isolated directory-collision fault and controlled human reconciliation; not the historical I/O cause',
    objectHash: authorized.object.hash, authorizationId: authorized.authorization!.id,
    failedVersion: failure.version, failures: failure.objectIoFailures, completedVersion: reconciled.version,
    finalReceipt: reconciled.deletion
  }, null, 2))
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
