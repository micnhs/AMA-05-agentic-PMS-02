import { expect, test, type APIRequestContext, type BrowserContext, type Locator, type Page } from '@playwright/test'
import { mkdirSync, readFileSync, rmdirSync, writeFileSync } from 'node:fs'
import { createHash } from 'node:crypto'
import { inflateRawSync } from 'node:zlib'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import type { CalendarSnapshot, Capa, CaseProcessView, CaseSummary, ChangeRequest, CockpitSnapshot, Evidence, PeriodicSummaryReport, ProductGovernance, Psur, PsurGroupDefinition, PsurReview, RetentionRecord, RetentionView, SafetyCase, SignalView, SummaryAgreement } from '../src/types'
import type { ActorApplication, IntegrationWorkspace } from '../src/EudamedWorkspace'

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

function requireWorkspaceIsolation(baseURL: string | undefined) {
  if (!baseURL || new URL(baseURL).port === '5187' || !process.env.COHORS_TEST_ACCESS_FILE || !process.env.COHORS_UI_ARTIFACT_DIR)
    throw new Error('Cockpit/calendar acceptance requires the private Test-Ui.ps1 fixture, never the user-active demo.')
}

const eudamedReason = 'SYNTHETISCH: Die konkrete Antragsrevision, Referenzzuordnung und Kanalwirkung werden menschlich begruendet geprueft. Keine amtliche SRN, externe Sendung oder reale Servicefreigabe.'
async function integration(client: APIRequestContext, product = 'ortho'): Promise<IntegrationWorkspace> {
  const response = await client.get(`/api/eudamed/${product}/workspace`)
  expect(response.status()).toBe(200)
  return response.json()
}

test('E01: persisted actor corrections and rejection, simulator activation and blocked Domibus draft', async ({ playwright, browser, baseURL }) => {
  test.setTimeout(210_000)
  requireWorkspaceIsolation(baseURL)
  const pms = await playwright.request.newContext({ baseURL })
  const ra = await playwright.request.newContext({ baseURL })
  let pmsContext: BrowserContext | undefined
  let raContext: BrowserContext | undefined
  try {
    const pmsCsrf = await signIn(pms, 'pms.demo')
    const raCsrf = await signIn(ra, 'ra.demo')
    pmsContext = await browser.newContext({ storageState: await pms.storageState() })
    raContext = await browser.newContext({ storageState: await ra.storageState() })
    const author = await pmsContext.newPage()
    const reviewer = await raContext.newPage()
    const errors: string[] = []
    const externalRequests: string[] = []
    for (const page of [author, reviewer]) {
      page.setDefaultTimeout(25_000)
      page.on('pageerror', failure => errors.push(failure.message))
      page.on('request', request => { if (new URL(request.url()).origin !== new URL(baseURL!).origin) externalRequests.push(request.url()) })
      await page.goto(baseURL!)
      await page.getByRole('navigation').getByRole('button', { name: 'EUDAMED Integration', exact: true }).click()
      await page.getByRole('combobox', { name: 'Integrations-Produktscope', exact: true }).selectOption('ortho')
    }
    const title = `DEMO Actor E01 ${Date.now()}`
    await author.getByRole('button', { name: 'Neuer Actor-Antrag', exact: true }).click()
    let dialog = author.getByRole('dialog')
    await dialog.getByLabel('Synthetische Organisation (DEMO oder SYNTHETISCH)', { exact: true }).fill(title)
    await dialog.getByLabel('Nachvollziehbare Begruendung', { exact: true }).fill(eudamedReason)
    await submitDialog(dialog, 'Antragsfassung speichern')
    let application = (await integration(pms)).actors.find(value => value.details.organization === title)
    if (!application) throw new Error('The actor created in the UI is absent from the persisted workspace.')
    const id = application.id
    const authored = author.locator(`[data-actor-id="${id}"]`)
    const reviewed = reviewer.locator(`[data-actor-id="${id}"]`)
    expect(application.status).toBe('Draft')
    await expect(authored.getByRole('button', { name: 'Lokal einreichen', exact: true })).toHaveCount(0)
    const denied = await pms.post(`/api/eudamed/actors/${id}/submit`, { headers: { 'X-CSRF-Token': pmsCsrf, 'Idempotency-Key': crypto.randomUUID() },
      data: { expectedVersion: application.version, scenario: 'Approve', reason: eudamedReason } })
    expect(denied.status()).toBe(403)
    for (const scenario of [
      { input: 'Correction', status: 'CorrectionRequired', label: 'Korrektur erforderlich' },
      { input: 'Reject', status: 'Rejected', label: 'Antrag abgelehnt' },
      { input: 'Approve', status: 'SimulatedApproved', label: 'Nur Simulator freigeschaltet' },
    ]) {
      if (application.status !== 'Draft') {
        await author.getByRole('button', { name: 'Stand neu lesen', exact: true }).click()
        await expect(authored).toContainText(`v${application.version}`)
        await authored.getByRole('button', { name: 'Antrag korrigieren', exact: true }).click()
        dialog = author.getByRole('dialog')
        await dialog.getByLabel('Synthetische Anschrift', { exact: true }).fill(`SYNTHETISCH: Teststrasse 0, Testort, Revision ${application.version}`)
        await dialog.getByLabel('Nachvollziehbare Begruendung', { exact: true }).fill(eudamedReason)
        await submitDialog(dialog, 'Antragsfassung speichern')
      }
      await reviewer.getByRole('button', { name: 'Stand neu lesen', exact: true }).click()
      await expect(reviewed.getByText('Entwurf', { exact: true })).toBeVisible()
      await reviewed.getByRole('button', { name: 'Lokal einreichen', exact: true }).click()
      dialog = reviewer.getByRole('dialog')
      await dialog.getByLabel('Explizites Registrierungsszenario', { exact: true }).selectOption(scenario.input)
      await dialog.getByLabel('Nachvollziehbare Begruendung', { exact: true }).fill(eudamedReason)
      await submitDialog(dialog, 'Antrag dem Simulator uebergeben')
      await expect.poll(async () => {
        application = (await integration(ra)).actors.find(value => value.id === id)!
        return application.status
      }).toBe(scenario.status)
      await expect(reviewed.getByText(scenario.label, { exact: true })).toBeVisible()
    }
    if (!application) throw new Error('The final actor revision is missing.')
    expect(application.rounds.map(round => round.status)).toEqual([
      'Submitted', 'CorrectionRequired', 'Submitted', 'Rejected', 'Submitted', 'SimulatedApproved',
    ])
    expect(application.version).toBe(9)
    expect(application.simulatedIdentity).toMatch(/^DEMO-EUDAMED-MF-/)
    await reviewer.getByRole('button', { name: 'Referenzen zuordnen', exact: true }).click()
    dialog = reviewer.getByRole('dialog')
    await dialog.getByLabel('Freigeschalteter MF-Testactor', { exact: true }).selectOption(id)
    await dialog.getByLabel('Explizite UDI Issuing Agency', { exact: true }).selectOption('GS1')
    await dialog.getByLabel('Nachvollziehbare Begruendung', { exact: true }).fill(eudamedReason)
    await submitDialog(dialog, 'Versionierte Zuordnung bestaetigen')
    const references = (await integration(ra)).references.find(value => value.actorApplication.id === id)
    if (!references) throw new Error('Explicit reference mapping was not persisted.')
    expect(references.actorApplication.version).toBe(application.version)
    expect(references.certificate.originalHash).toMatch(/^[a-f0-9]{64}$/)
    await reviewer.getByRole('button', { name: 'Simulator konfigurieren', exact: true }).click()
    dialog = reviewer.getByRole('dialog')
    await dialog.getByLabel('Bestaetigte Actor-/Geraete-/Zertifikatszuordnung', { exact: true }).selectOption(references.id)
    await dialog.getByLabel('Nachvollziehbare Begruendung', { exact: true }).fill(eudamedReason)
    await submitDialog(dialog, 'Nur Konfigurationsentwurf speichern')
    await reviewer.getByRole('button', { name: 'Entwurf aktivieren', exact: true }).click()
    dialog = reviewer.getByRole('dialog')
    await dialog.getByLabel('Auswirkung auf gebundene Auftraege', { exact: true }).selectOption('confirm')
    await dialog.getByLabel('Nachvollziehbare Begruendung', { exact: true }).fill(eudamedReason)
    await submitDialog(dialog, 'Profilwechsel bewusst bestaetigen')
    const activated = (await integration(ra)).activeChannel
    expect(activated?.enabled).toBe(true)
    expect(activated?.profile.mode).toBe('Simulator')
    await reviewer.reload()
    await expect(reviewer.getByRole('heading', { name: 'Sicherheit im Blick.', exact: true })).toBeVisible()
    await reviewer.getByRole('navigation').getByRole('button', { name: 'EUDAMED Integration', exact: true }).click()
    await expect(reviewer.getByRole('region', { name: 'Tatsaechlich aktiver Modus' })).toContainText('Ausdruecklich aktiviert')
    await reviewer.getByRole('button', { name: 'Domibus-Entwurf erfassen', exact: true }).click()
    dialog = reviewer.getByRole('dialog')
    await dialog.getByLabel('Nachvollziehbare Begruendung', { exact: true }).fill(eudamedReason)
    await submitDialog(dialog, 'Nur Konfigurationsentwurf speichern')
    await expect(reviewer.getByRole('region', { name: 'Tatsaechlich aktiver Modus' })).toContainText('Simulator')
    await expect(reviewer.getByRole('region', { name: 'Gewuenschter Konfigurationsentwurf' })).toContainText('Domibus')
    await expect(reviewer.getByRole('button', { name: 'Entwurf aktivieren', exact: true })).toBeDisabled()
    const pending = await integration(ra)
    expect(pending.activeChannel).toEqual(activated)
    expect(pending.draftReadiness?.canActivate).toBe(false)
    expect(pending.referenceCatalog.endpoints).toEqual([])
    const refused = await ra.post('/api/eudamed/ortho/activate', { headers: { 'X-CSRF-Token': raCsrf, 'Idempotency-Key': crypto.randomUUID() },
      data: { expectedVersion: activated!.version, draftVersion: pending.draft!.version, confirmImpact: true, reason: eudamedReason } })
    expect(refused.status()).toBe(409)
    expect((await refused.json()).code).toBe('eudamed_not_ready')
    expect((await integration(ra)).activeChannel).toEqual(activated)
    await reviewed.getByRole('button', { name: 'Actor-Audit', exact: true }).click()
    await expect(reviewer.locator('.eudamed-audit')).toContainText('EudamedActorSimulationResponded')
    expect(errors).toEqual([])
    expect(externalRequests).toEqual([])
    await reviewer.screenshot({ path: path.join(artifacts, 'cohors-eudamed-actor-configuration.png') })
    writeFileSync(path.join(artifacts, 'eudamed-configuration-proof.json'), JSON.stringify({
      success: true, actorId: id, actorVersion: application.version, actorStatuses: application.rounds.map(round => round.status),
      humanPmsAndRaAccountsSeparated: true, persistedReload: true, activeChannel: activated, draftMode: pending.draft!.profile.mode,
      blockedExternalActivation: true, browserExternalRequests: externalRequests, realGatewayOrAuthorityAcceptance: false,
    }, null, 2))
  } finally { await Promise.all([pmsContext?.close(), raContext?.close(), pms.dispose(), ra.dispose()]) }
})

test('E02: integration role and product checks survive an account switch with a delayed old workspace', async ({ playwright, browser, baseURL }) => {
  test.setTimeout(150_000)
  requireWorkspaceIsolation(baseURL)
  const ra = await playwright.request.newContext({ baseURL })
  const limited = await playwright.request.newContext({ baseURL })
  const medical = await playwright.request.newContext({ baseURL })
  let context: BrowserContext | undefined
  let release: (() => void) | undefined
  try {
    const csrf = await signIn(ra, 'ra.demo')
    const limitedCsrf = await signIn(limited, 'ra-cardio.demo')
    const medicalCsrf = await signIn(medical, 'medical.demo')
    const title = `DEMO Orthopaedic scope E02 ${Date.now()}`
    const input = { expectedVersion: 0, productId: 'ortho', reason: eudamedReason,
      details: { organization: title, role: 'MF', country: 'IE', address: 'SYNTHETISCH: separate product scope',
        contact: { firstName: 'Demo', familyName: 'Scoped', email: 'scoped@example.invalid', telephone: '000000' }, synthetic: true } }
    const actor = await command<ActorApplication>(ra, csrf, '/eudamed/actors', input)
    expect((await limited.get('/api/eudamed/ortho/workspace')).status()).toBe(403)
    expect((await limited.get(`/api/eudamed/audit/eudamedActor/${actor.id}`)).status()).toBe(403)
    expect((await integration(limited, 'cardio')).actors.some(value => value.id === actor.id)).toBe(false)
    expect((await limited.post(`/api/eudamed/actors/${actor.id}`, { headers: { 'X-CSRF-Token': limitedCsrf, 'Idempotency-Key': crypto.randomUUID() },
      data: { ...input, productId: 'cardio', expectedVersion: actor.version } })).status()).toBe(403)
    expect((await medical.post('/api/eudamed/actors', { headers: { 'X-CSRF-Token': medicalCsrf, 'Idempotency-Key': crypto.randomUUID() }, data: input })).status()).toBe(403)
    context = await browser.newContext({ storageState: await ra.storageState() })
    const page = await context.newPage()
    page.setDefaultTimeout(25_000)
    const errors: string[] = []
    page.on('pageerror', failure => errors.push(failure.message))
    await page.goto(baseURL!)
    await page.getByRole('navigation').getByRole('button', { name: 'EUDAMED Integration', exact: true }).click()
    await page.getByRole('combobox', { name: 'Integrations-Produktscope', exact: true }).selectOption('ortho')
    await expect(page.getByRole('heading', { name: title, exact: true })).toBeVisible()
    let markHeld: (() => void) | undefined
    let held = false
    const pending = new Promise<void>(resolve => { markHeld = resolve })
    const gate = new Promise<void>(resolve => { release = resolve })
    await page.route('**/api/eudamed/ortho/workspace', async route => {
      if (held) { await route.continue(); return }
      held = true
      const response = await route.fetch()
      markHeld!()
      await gate
      await route.fulfill({ response, headers: { ...response.headers(), 'x-cohors-delayed-eudamed': 'original' } })
    })
    await page.getByRole('button', { name: 'Stand neu lesen', exact: true }).click()
    await pending
    const logoutTitle = 'Abmelden und mit eigenem getrenntem Konto anmelden'
    async function loginUi(username: string) {
      await expect(page.getByRole('heading', { name: 'Willkommen bei Cohors', exact: true })).toBeVisible()
      await page.getByLabel('Benutzername', { exact: true }).fill(username)
      await page.getByLabel('Passwort', { exact: true }).fill(accounts[username])
      await page.getByRole('button', { name: 'Sicher anmelden', exact: true }).click()
      await expect(page.getByRole('navigation')).toBeVisible()
    }
    await page.getByTitle(logoutTitle, { exact: true }).click()
    await loginUi('ra-cardio.demo')
    await expect(page.getByRole('combobox', { name: 'Integrations-Produktscope', exact: true })).toHaveValue('cardio')
    await expect(page.locator('body')).not.toContainText(title)
    const delayed = page.waitForResponse(response => response.headers()['x-cohors-delayed-eudamed'] === 'original')
    release!()
    await (await delayed).finished()
    await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))))
    await expect(page.locator('body')).not.toContainText(title)
    await expect(page.getByRole('combobox', { name: 'Integrations-Produktscope', exact: true }).locator('option[value="ortho"]')).toHaveCount(0)
    await page.unroute('**/api/eudamed/ortho/workspace')
    await page.getByTitle(logoutTitle, { exact: true }).click()
    await loginUi('medical.demo')
    await expect(page.getByRole('button', { name: 'Neuer Actor-Antrag', exact: true })).toHaveCount(0)
    await expect(page.getByRole('button', { name: 'Domibus-Entwurf erfassen', exact: true })).toHaveCount(0)
    expect(errors).toEqual([])
    await page.screenshot({ path: path.join(artifacts, 'cohors-eudamed-role-scope.png') })
    writeFileSync(path.join(artifacts, 'eudamed-identity-proof.json'), JSON.stringify({
      success: true, roleAndScopeDeniedServerSide: true, realUiLogoutAndLogin: true,
      delayedOldWorkspaceIgnored: true, noForeignProductOption: true, medicalReadOnly: true,
    }, null, 2))
  } finally { release?.(); await context?.close(); await ra.dispose(); await limited.dispose(); await medical.dispose() }
})

const signalReason = 'SYNTHETISCH: Die Quellenrelevanz und fehlende Produktzuordnung werden begruendet menschlich geprueft. Keine Nichtmeldepflichtentscheidung, automatische Freigabe oder Fristaenderung.'
function signalInput(stamp: string, agent = false) {
  return { incidentKey: stamp, sourceId: crypto.randomUUID(), productId: 'ortho', title: `Synthetischer Signalreview ${stamp}`,
    description: 'SYNTHETISCH: Nicht bestaetigter Hinweis zur Produktfunktion. Direkte technische Befunde und eine verifizierte klinische Ursachenabgrenzung fehlen.',
    udi: null, batch: null, country: 'DE', jurisdiction: 'EU-DE', seriousness: 'OtherSerious', occurredAt: null,
    awarenessAt: new Date(Date.now() - 86_400_000).toISOString(), synthetic: true, scenario: agent ? 'signal-triage-replay-reject' : 'signal-review-ui' }
}
async function blockReviewPolling(context: BrowserContext) {
  await context.addInitScript(() => {
    const original = window.setInterval.bind(window)
    let blocked = 0
    Reflect.set(window, '__cohorsBlockedIntervals', 0)
    window.setInterval = (handler, delay, ...args) => {
      if (delay === 10_000) { Reflect.set(window, '__cohorsBlockedIntervals', ++blocked); return -blocked }
      return original(handler, delay, ...args)
    }
  })
}
async function reopenSignalUi(page: Page, signal: SignalView) {
  await page.getByRole('navigation').getByRole('button', { name: 'Abgelehnte Signale', exact: true }).click()
  const row = page.getByTestId(`signal-${signal.ownerId}-${signal.sourceId}`)
  await row.getByRole('button', { name: 'Wiederaufnehmen', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Fachliche Signalbegründung', { exact: true }).fill(signalReason)
  await submitDialog(dialog, 'Jetzt ins Review')
  await expect(page.getByRole('heading', { name: 'Review Queue', exact: true })).toBeVisible()
}
for (const variant of [
  { title: 'S01: Medical reopens an agent-rejected signal directly into Review Queue with polling disabled', username: 'medical.demo', limited: 'medical-cardio.demo', agent: true, image: 'medical' },
  { title: 'S02: RA reopens a reviewer-rejected signal directly into Review Queue with polling disabled', username: 'ra.demo', limited: 'ra-cardio.demo', agent: false, image: 'ra' },
]) {
  test(variant.title, async ({ playwright, browser, baseURL }) => {
    requireWorkspaceIsolation(baseURL)
    const pms = await playwright.request.newContext({ baseURL })
    const reviewer = await playwright.request.newContext({ baseURL })
    const limited = await playwright.request.newContext({ baseURL })
    let context: BrowserContext | undefined
    try {
      const pmsCsrf = await signIn(pms, 'pms.demo')
      await signIn(reviewer, variant.username)
      const limitedCsrf = await signIn(limited, variant.limited)
      let c = await command(pms, pmsCsrf, '/cases', signalInput(`${variant.image}-${Date.now()}`, variant.agent))
      if (variant.agent) {
        c = await command(pms, pmsCsrf, `/cases/${c.id}/assessment`, { expectedVersion: c.version, mode: 'Replay', reason: signalReason })
        await expect.poll(async () => { c = await (await pms.get(`/api/cases/${c.id}`)).json(); return c.currentAssessment?.version }).toBe(1)
      }
      context = await browser.newContext({ storageState: await reviewer.storageState() })
      await blockReviewPolling(context)
      const page = await context.newPage()
      const errors: string[] = []
      const posts: string[] = []
      let navigations = 0
      page.on('pageerror', e => errors.push(e.message))
      page.on('request', request => { if (request.method() === 'POST') posts.push(new URL(request.url()).pathname) })
      await page.goto(baseURL!)
      await expect(page.getByRole('heading', { name: 'Sicherheit im Blick.', exact: true })).toBeVisible()
      page.on('framenavigated', frame => { if (frame === page.mainFrame()) navigations++ })
      if (!variant.agent) {
        await page.getByRole('navigation').getByRole('button', { name: 'Signal Inbox', exact: true }).click()
        const row = page.getByTestId(`signal-${c.id}-${c.sources[0].id}`)
        await row.getByRole('button', { name: 'Signal ablehnen', exact: true }).click()
        const dialog = page.getByRole('dialog')
        await dialog.getByLabel('Fachliche Signalbegründung', { exact: true }).fill(signalReason)
        await submitDialog(dialog, 'Signal ablehnen')
        c = await (await pms.get(`/api/cases/${c.id}`)).json()
      }
      const rejected: SignalView[] = await (await reviewer.get('/api/signals')).json()
      const signal = rejected.find(s => s.ownerId === c.id)
      if (!signal) throw new Error('The actual persisted signal rejection is missing.')
      expect(signal.history.at(-1)?.origin).toBe(variant.agent ? 'Agent' : 'Reviewer')
      expect((await limited.get('/api/signals')).ok()).toBe(true)
      expect((await (await limited.get('/api/signals')).json() as SignalView[]).some(s => s.ownerId === c.id)).toBe(false)
      for (const [client, csrf] of [[pms, pmsCsrf], [limited, limitedCsrf]] as const) {
        const denied = await client.post(`/api/signals/case/${c.id}/reopen`, { headers: { 'X-CSRF-Token': csrf, 'Idempotency-Key': crypto.randomUUID() },
          data: { expectedVersion: signal.ownerVersion, sourceId: signal.sourceId, reason: signalReason } })
        expect(denied.status()).toBe(403)
      }
      await page.getByRole('navigation').getByRole('button', { name: 'Abgelehnte Signale', exact: true }).click()
      const rejectedRow = page.getByTestId(`signal-${signal.ownerId}-${signal.sourceId}`)
      await expect(rejectedRow).toContainText(variant.agent ? 'Agenten-Ablehnung' : 'Reviewer-Ablehnung')
      await expect(rejectedRow).toContainText(signal.history.at(-1)!.reason)
      if (variant.agent) await expect(rejectedRow).toContainText(signal.history.at(-1)!.agentRunId!)
      await page.screenshot({ path: path.join(artifacts, `cohors-signal-${variant.image}-rejected.png`) })
      const count = (await (await reviewer.get('/api/cases')).json() as CaseSummary[]).length
      const before = await (await reviewer.get(`/api/cases/${c.id}`)).json() as SafetyCase
      const start = Date.now()
      await reopenSignalUi(page, signal)
      const elapsedMs = Date.now() - start
      await expect(page.getByRole('button', { name: c.title, exact: true })).toBeVisible()
      await expect(page.getByRole('status').filter({ hasText: 'unmittelbar im menschlichen Review' })).toContainText(c.title)
      const current = await (await reviewer.get(`/api/cases/${c.id}`)).json() as SafetyCase
      expect(current.stage).toBe('AwaitingMedicalReview')
      expect(current.agentRuns).toEqual(before.agentRuns)
      expect(current.assessments).toEqual(before.assessments)
      expect(current.approvals).toEqual(before.approvals)
      expect(current.deadlines).toEqual(before.deadlines)
      expect(current.firstAwarenessAt).toBe(before.firstAwarenessAt)
      expect(current.sources).toEqual(before.sources)
      expect((await (await reviewer.get('/api/cases')).json() as CaseSummary[]).length).toBe(count)
      expect(current.signalReviews?.[0].history.map(d => d.action)).toEqual(['Reject', 'Reopen'])
      expect(posts.some(p => /\/cases\/[^/]+\/assessment$/.test(p))).toBe(false)
      expect(navigations).toBe(0)
      expect(await page.evaluate(() => Number(Reflect.get(window, '__cohorsBlockedIntervals')))).toBeGreaterThan(0)
      await page.screenshot({ path: path.join(artifacts, `cohors-signal-${variant.image}-review.png`) })
      await page.getByRole('button', { name: c.title, exact: true }).click()
      await expect(page.getByText(variant.agent ? 'Frühere Bewertung durch Wiederaufnahme überholt.' : 'Noch keine Sicherheitsbewertung', { exact: true })).toBeVisible()
      expect(errors).toEqual([])
      writeFileSync(path.join(artifacts, `signal-${variant.image}-proof.json`), JSON.stringify({
        title: variant.title, role: current.signalReviews?.[0].history.at(-1)?.role, signal: signal.sourceId,
        caseId: c.id, version: current.version, elapsedMs, tenSecondPollingDisabled: true, manualReloads: navigations,
        noNewAgentRun: true, awarenessAndDeadlinesPreserved: true, assessmentVersion: current.currentAssessment?.version ?? null,
      }, null, 2))
    } finally { await context?.close(); await pms.dispose(); await reviewer.dispose(); await limited.dispose() }
  })
}

test('S03: unlinked public-source reviews reopen immediately for Medical and RA without inventing cases or awareness', async ({ playwright, browser, baseURL }) => {
  requireWorkspaceIsolation(baseURL)
  for (const [username, sourceId] of [['medical.demo', 'synthetic-ui-medical'], ['ra.demo', 'synthetic-ui-regulatory']]) {
    const reviewer = await playwright.request.newContext({ baseURL })
    let context: BrowserContext | undefined
    try {
      const csrf = await signIn(reviewer, username)
      const all = await (await reviewer.get('/api/signals?status=All')).json() as SignalView[]
      const source = all.find(s => s.sourceId === sourceId && s.ownerKind === 'source')
      if (!source) throw new Error('Explicit isolated synthetic metadata fixture is missing; no live source is substituted.')
      const rejected = await command<SignalView>(reviewer, csrf, `/signals/source/${source.ownerId}/reject`,
        { expectedVersion: source.ownerVersion, sourceId, reason: signalReason })
      const casesBefore = await (await reviewer.get('/api/cases')).json() as CaseSummary[]
      context = await browser.newContext({ storageState: await reviewer.storageState() })
      await blockReviewPolling(context)
      const page = await context.newPage()
      const errors: string[] = []
      page.on('pageerror', e => errors.push(e.message))
      await page.goto(baseURL!)
      await page.getByRole('navigation').getByRole('button', { name: 'Abgelehnte Signale', exact: true }).click()
      const unknown = page.getByTestId(`signal-${source.ownerId}-synthetic-ui-unknown-history`)
      await expect(unknown).toContainText('Ablehnung · Herkunft unbekannt')
      await expect(unknown).not.toContainText('Agenten-Ablehnung')
      await reopenSignalUi(page, rejected)
      const pending = page.getByRole('region', { name: 'Quellenreviews ohne Fallakte', exact: true })
      await expect(pending.getByTestId(`signal-${source.ownerId}-${sourceId}`)).toContainText(rejected.reviewReference!)
      await expect(pending.getByTestId(`signal-${source.ownerId}-${sourceId}`)).toContainText('Zum Review wiederaufgenommen')
      const current = (await (await reviewer.get('/api/signals?status=ReviewRequired')).json() as SignalView[]).find(s => s.sourceId === sourceId)!
      expect(current.caseId).toBeNull()
      expect(current.awarenessAt).toBeNull()
      expect(current.reviewReference).toBe(rejected.reviewReference)
      expect(current.history.map(d => d.action)).toEqual(['Reject', 'Reopen'])
      expect((await (await reviewer.get('/api/cases')).json() as CaseSummary[]).map(c => c.id).sort()).toEqual(casesBefore.map(c => c.id).sort())
      expect(await page.evaluate(() => Number(Reflect.get(window, '__cohorsBlockedIntervals')))).toBeGreaterThan(0)
      expect(errors).toEqual([])
      await page.screenshot({ path: path.join(artifacts, `cohors-source-review-${username}.png`) })
    } finally { await context?.close(); await reviewer.dispose() }
  }
})

test('S04: account changes clear signal notices and ignore delayed prior-identity refresh and reopen responses', async ({ playwright, browser, baseURL }) => {
  requireWorkspaceIsolation(baseURL)
  const pms = await playwright.request.newContext({ baseURL })
  const medical = await playwright.request.newContext({ baseURL })
  let context: BrowserContext | undefined
  let release: (() => void) | undefined
  try {
    const pmsCsrf = await signIn(pms, 'pms.demo')
    const medicalCsrf = await signIn(medical, 'medical.demo')
    async function rejectedCase(suffix: string) {
      const c = await command(pms, pmsCsrf, '/cases', signalInput(`IDENTITY-${suffix}-${Date.now()}`))
      const signal = await command<SignalView>(medical, medicalCsrf, `/signals/case/${c.id}/reject`,
        { expectedVersion: c.version, sourceId: c.sources[0].id, reason: signalReason })
      return { c, signal }
    }
    const first = await rejectedCase('NOTICE')
    context = await browser.newContext({ storageState: await medical.storageState() })
    await blockReviewPolling(context)
    const page = await context.newPage()
    const errors: string[] = []
    page.on('pageerror', e => errors.push(e.message))
    await page.goto(baseURL!)
    await reopenSignalUi(page, first.signal)
    await expect(page.getByRole('status').filter({ hasText: first.c.title })).toBeVisible()
    async function loginUi(username: string) {
      await expect(page.getByRole('heading', { name: 'Willkommen bei Cohors', exact: true })).toBeVisible()
      await page.getByLabel('Benutzername', { exact: true }).fill(username)
      await page.getByLabel('Passwort', { exact: true }).fill(accounts[username])
      await page.getByRole('button', { name: 'Sicher anmelden', exact: true }).click()
      await expect(page.getByRole('navigation')).toBeVisible()
    }
    async function settleLateResponse() {
      const response = page.waitForResponse(r => r.headers()['x-cohors-delayed-identity'] === 'original')
      release!()
      await (await response).finished()
      await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))))
    }
    const logoutTitle = 'Abmelden und mit eigenem getrenntem Konto anmelden'
    let held = false
    let markHeld: (() => void) | undefined
    const pending = new Promise<void>(resolve => { markHeld = resolve })
    const delayed = new Promise<void>(resolve => { release = resolve })
    await page.route('**/api/signals?status=ReviewRequired', async route => {
      if (held) { await route.continue(); return }
      held = true
      const response = await route.fetch()
      markHeld!()
      await delayed
      await route.fulfill({ response, headers: { ...response.headers(), 'x-cohors-delayed-identity': 'original' } })
    })
    await page.getByRole('navigation').getByRole('button', { name: 'Signal Inbox', exact: true }).click()
    await page.getByRole('navigation').getByRole('button', { name: 'Review Queue', exact: true }).click()
    await pending
    await page.getByTitle(logoutTitle, { exact: true }).click()
    await loginUi('pms-cardio.demo')
    await expect(page.getByRole('heading', { name: 'Review Queue', exact: true })).toBeVisible()
    await expect(page.locator('body')).not.toContainText(first.c.title)
    await settleLateResponse()
    await expect(page.locator('body')).not.toContainText(first.c.title)
    await expect(page.getByRole('region', { name: 'Quellenreviews ohne Fallakte', exact: true })).not.toContainText('SYNTHETISCHER Quellenreview')
    await page.unroute('**/api/signals?status=ReviewRequired')

    await page.getByTitle(logoutTitle, { exact: true }).click()
    await loginUi('medical.demo')
    const second = await rejectedCase('LATE-POST')
    let postHeld: (() => void) | undefined
    const pendingPost = new Promise<void>(resolve => { postHeld = resolve })
    const delayedPost = new Promise<void>(resolve => { release = resolve })
    await page.route(`**/api/signals/case/${second.c.id}/reopen`, async route => {
      const response = await route.fetch()
      postHeld!()
      await delayedPost
      await route.fulfill({ response, headers: { ...response.headers(), 'x-cohors-delayed-identity': 'original' } })
    })
    await page.getByRole('navigation').getByRole('button', { name: 'Abgelehnte Signale', exact: true }).click()
    await page.getByTestId(`signal-${second.c.id}-${second.signal.sourceId}`).getByRole('button', { name: 'Wiederaufnehmen', exact: true }).click()
    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Fachliche Signalbegründung', { exact: true }).fill(signalReason)
    await dialog.getByRole('button', { name: 'Jetzt ins Review', exact: true }).click()
    await pendingPost
    // Controlled timing fault: invoke the real logout handler while its modal POST response is held.
    await page.getByTitle(logoutTitle, { exact: true }).evaluate(button => {
      if (!(button instanceof HTMLButtonElement)) throw new Error('The actual logout button is required.')
      button.click()
    })
    await loginUi('pms-cardio.demo')
    await settleLateResponse()
    await page.getByRole('navigation').getByRole('button', { name: 'Review Queue', exact: true }).click()
    await expect(page.locator('body')).not.toContainText(second.c.title)
    await expect(page.locator('body')).not.toContainText(first.c.title)
    await expect(page.locator('body')).not.toContainText('Wiederaufnahme gespeichert, aber')
    await expect(page.getByRole('region', { name: 'Quellenreviews ohne Fallakte', exact: true })).not.toContainText('SYNTHETISCHER Quellenreview')
    expect((await (await medical.get(`/api/cases/${second.c.id}`)).json() as SafetyCase).stage).toBe('AwaitingMedicalReview')
    expect((await page.request.get(`/api/cases/${second.c.id}`)).status()).toBe(403)
    expect(errors).toEqual([])
    await page.screenshot({ path: path.join(artifacts, 'cohors-signal-identity-scope.png') })
    writeFileSync(path.join(artifacts, 'signal-identity-proof.json'), JSON.stringify({
      success: true, realUiLogoutAndLogin: true, oldNoticeCleared: true, delayedRefreshIgnored: true,
      delayedReopenIgnored: true, delayedPostUsedExplicitTimingFault: true, authorizedOriginalWritePreserved: true,
      newAccountProducts: ['cardio'], noForeignSourceReviews: true,
    }, null, 2))
  } finally { release?.(); await context?.close(); await pms.dispose(); await medical.dispose() }
})

test('D01: authorized cockpit counts, actual process drilldown, unavailable risk groups and read-only navigation', async ({ playwright, browser, baseURL }) => {
  requireWorkspaceIsolation(baseURL)
  const pms = await playwright.request.newContext({ baseURL })
  const limited = await playwright.request.newContext({ baseURL })
  let context: BrowserContext | undefined
  const errors: string[] = []
  try {
    const csrf = await signIn(pms, 'pms.demo')
    await signIn(limited, 'pms-ortho.demo')
    const stamp = `COCKPIT-${Date.now()}`
    const input = { incidentKey: stamp, sourceId: crypto.randomUUID(), productId: 'ortho', title: `Synthetische Cockpit-Auswertung ${stamp}`,
      description: 'SYNTHETISCH: Berichteter Funktionsverlust. Die Patientenfolge ist nicht bestaetigt; Fristenklasse, Wahrscheinlichkeit und KI-Hypothesen sind kein Todesfallnachweis.',
      udi: null, batch: null, country: 'DE', jurisdiction: 'EU-DE', seriousness: 'DeathOrSeriousDeterioration',
      occurredAt: null, awarenessAt: new Date(Date.now() - 86_400_000).toISOString(), synthetic: true, scenario: 'cockpit-ui' }
    let c = await command(pms, csrf, '/cases', input)
    const unassessed = await command(pms, csrf, '/cases', { ...input, incidentKey: `${stamp}-OPEN`, sourceId: crypto.randomUUID(), title: `Synthetisch unbewertet ${stamp}` })
    const foreign = await command(pms, csrf, '/cases', { ...input, incidentKey: `${stamp}-FOREIGN`, sourceId: crypto.randomUUID(), productId: 'cardio' })
    c = await command(pms, csrf, `/cases/${c.id}/assessment`, { expectedVersion: c.version, mode: 'Replay',
      reason: 'Synthetischen begruendeten Replay-Entwurf fuer echte Prozesszahlen erzeugen; keine automatische Risikogruppe.' })
    await expect.poll(async () => { c = await (await pms.get(`/api/cases/${c.id}`)).json(); return c.currentAssessment?.version }).toBe(1)
    const source = JSON.stringify(c)
    const day = c.systemReceivedAt.slice(0, 10)
    const query = `?productId=ortho&from=${day}&through=${day}&timeZone=UTC`
    const snapshot: CockpitSnapshot = await (await limited.get(`/api/cockpit${query}`)).json()
    const cases: CaseSummary[] = await (await limited.get('/api/cases')).json()
    const cohort = cases.filter(row => row.productId === 'ortho' && row.systemReceivedAt.slice(0, 10) === day)
    expect(snapshot.totalCases).toBe(cohort.length)
    expect(snapshot.kpis.openCases).toBe(cohort.filter(row => row.stage !== 'Closed').length)
    expect(snapshot.processSteps.reduce((sum, row) => sum + row.count, 0)).toBe(cohort.length)
    expect(snapshot.intake.reduce((sum, row) => sum + row.count, 0)).toBe(cohort.length)
    expect(snapshot.riskGroupsAvailable).toBe(false)
    expect(snapshot.cases.some(row => row.id === foreign.id)).toBe(false)
    expect((await limited.get(`/api/cockpit?productId=cardio&from=${day}&through=${day}`)).status()).toBe(403)
    const process: CaseProcessView = await (await limited.get(`/api/cases/${c.id}/process`)).json()
    expect(snapshot.cases.find(row => row.id === c.id)?.currentStep).toBe(process.currentStep)
    expect(snapshot.cases.find(row => row.id === c.id)?.outcome.code).toBe('Unknown')

    context = await browser.newContext({ storageState: await limited.storageState(), timezoneId: 'America/Los_Angeles' })
    const page = await context.newPage()
    const requests: { path: string; method: string }[] = []
    page.on('pageerror', e => errors.push(e.message))
    page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/')) requests.push({ path: new URL(request.url()).pathname, method: request.method() }) })
    await page.goto(baseURL!)
    await expect(page.getByRole('heading', { name: 'Fallzugänge', exact: true })).toBeVisible()
    await page.getByRole('combobox', { name: 'Anzeigezeitzone', exact: true }).selectOption('UTC')
    await page.getByRole('combobox', { name: 'Produktfamilie', exact: true }).selectOption('ortho')
    await page.getByLabel('Fallzugänge ab', { exact: true }).fill(day)
    await page.getByLabel('Bis einschließlich', { exact: true }).fill(day)
    const applied = page.waitForResponse(response => response.url().includes('/api/cockpit?') && response.url().includes(`from=${day}`) && response.url().includes(`through=${day}`))
    await page.getByRole('button', { name: 'Zeitraum anwenden', exact: true }).click()
    expect((await applied).ok()).toBe(true)
    await expect(page.getByTestId('kpi-open').locator('strong')).toHaveText(snapshot.kpis.openCases.toLocaleString('de-DE'))
    await expect(page.getByText('Fachliche Gruppierung nicht verfügbar', { exact: true })).toBeVisible()
    await expect(page.getByRole('group', { name: 'Prüfstände', exact: true })).toContainText('KI-Entwurf')
    await page.screenshot({ path: path.join(artifacts, 'cohors-cockpit-kpis.png') })
    await page.getByRole('heading', { name: 'Risikogruppen', exact: true }).scrollIntoViewIfNeeded()
    await page.screenshot({ path: path.join(artifacts, 'cohors-cockpit-risk-process.png') })
    await page.getByRole('group', { name: 'Prozesszahlen', exact: true }).getByRole('button', { name: /^Analyse:/ }).click()
    await expect(page.getByRole('region', { name: 'Zugehörige Fallakten' })).toContainText(unassessed.title)
    await expect(page.getByRole('region', { name: 'Zugehörige Fallakten' })).not.toContainText(c.title)
    await page.getByRole('button', { name: new RegExp(`Alle ${snapshot.totalCases} Fallakten der Auswahl`) }).click()
    await expect(page.getByRole('region', { name: 'Zugehörige Fallakten' })).toContainText(c.title)
    expect(requests.some(request => request.path === '/api/cases')).toBe(false)
    expect(requests.some(request => /\/cases\/[^/]+\/process$/.test(request.path))).toBe(false)
    await page.getByRole('region', { name: 'Zugehörige Fallakten' }).getByRole('button', { name: c.title, exact: true }).click()
    await expect(page.getByRole('region', { name: 'Fallprozess', exact: true })).toBeVisible()
    await page.screenshot({ path: path.join(artifacts, 'cohors-cockpit-case-drilldown.png') })
    expect(requests.every(request => request.method === 'GET')).toBe(true)
    expect(JSON.stringify(await (await pms.get(`/api/cases/${c.id}`)).json())).toBe(source)
    expect(errors).toEqual([])
  } finally { await context?.close(); await pms.dispose(); await limited.dispose() }
})

test('D02: stored PSUR date-only and BS UTC month-boundary calendar, exact source navigation and whole-group authorization', async ({ playwright, browser, baseURL }) => {
  requireWorkspaceIsolation(baseURL)
  test.setTimeout(210_000)
  const pms = await playwright.request.newContext({ baseURL })
  const medical = await playwright.request.newContext({ baseURL })
  const ra = await playwright.request.newContext({ baseURL })
  const limited = await playwright.request.newContext({ baseURL })
  let context: BrowserContext | undefined
  const errors: string[] = []
  try {
    const pmsCsrf = await signIn(pms, 'pms.demo')
    const medicalCsrf = await signIn(medical, 'medical.demo')
    const raCsrf = await signIn(ra, 'ra.demo')
    await signIn(limited, 'pms-cardio.demo')
    const profiles: ProductGovernance[] = await (await pms.get('/api/products/profiles')).json()
    const profile = profiles.find(p => p.productId === 'cardio')
    if (!profile) throw new Error('Explicit seeded Cardio profile is required.')
    const rationale = 'SYNTHETISCH: Gespeicherte Quelltermine und getrennte Basic-UDI-Berechtigungen pruefen; keine klinische Risikoakzeptanz, reale BS-Kommunikation oder Terminverschiebung.'
    const group = await command<PsurGroupDefinition>(pms, pmsCsrf, '/psur-groups', { expectedVersion: 0,
      reference: `DEMO-PSUR-GROUP-CALENDAR-${Date.now()}`, leadProductId: 'cardio', memberProductIds: ['cardio', 'ortho'],
      firstPeriodStart: profile.firstPeriodStart, rationale, scheduleBasis: 'Expliziter synthetischer Kalendernachweis aus dem bestehenden Leitprodukt-Jahresplan.' })
    const [year, month, day] = group.firstPeriodStart.split('-').map(Number)
    let report = await command<Psur>(pms, pmsCsrf, '/psurs', { productId: 'cardio', leadProductId: 'cardio',
      start: group.firstPeriodStart, end: new Date(Date.UTC(year + 1, month - 1, day - 1)).toISOString().slice(0, 10),
      region: 'Worldwide', groupId: group.id, groupVersion: group.version, mode: 'Replay' })
    report = await command<Psur>(medical, medicalCsrf, `/psurs/${report.id}/approve`, { expectedVersion: report.version, reason: rationale })
    report = await command<Psur>(ra, raCsrf, `/psurs/${report.id}/approve`, { expectedVersion: report.version, reason: rationale })
    report = await command<Psur>(ra, raCsrf, `/psurs/${report.id}/simulate`, { expectedVersion: report.version, reason: rationale })
    const series = report.snapshot.series!
    const [dueYear, dueMonth] = series.submissionDate.split('-').map(Number)
    const lastDay = new Date(Date.UTC(dueYear, dueMonth, 0)).toISOString().slice(0, 10)
    const nextMonth = new Date(Date.UTC(dueYear, dueMonth, 1)).toISOString().slice(0, 10)
    const review = await command<PsurReview>(ra, raCsrf, '/psur-reviews', { psurId: report.id, psurVersion: report.version,
      reference: `DEMO-BS-CALENDAR-${Date.now()}`, question: rationale, dueAt: `${lastDay}T23:30:00Z`,
      actionRequired: false, scheduleBasis: 'Dieser isolierte synthetische Test vereinbart ausdruecklich 23:30 UTC am Monatsende; keine allgemeine Rechtsfrist.',
      language: 'de', reason: rationale })
    const reportBytes = JSON.stringify(report)
    const reviewBytes = JSON.stringify(review)
    const full: CalendarSnapshot = await (await pms.get(`/api/deadline-calendar?month=${nextMonth}&timeZone=Europe%2FBerlin&kind=NotifiedBodyReply`)).json()
    expect(full.events.find(e => e.entityId === review.id)?.displayDate).toBe(nextMonth)
    expect(full.events.find(e => e.entityId === review.id)?.originalTimeZone).toBe('UTC')
    const restricted: CalendarSnapshot = await (await limited.get(`/api/deadline-calendar?month=${nextMonth}&timeZone=Europe%2FBerlin&kind=NotifiedBodyReply`)).json()
    expect(restricted.events.some(e => e.entityId === review.id)).toBe(false)
    expect(restricted.totalEvents).toBe(0)
    expect(restricted.openObligations).toBe(0)

    context = await browser.newContext({ storageState: await pms.storageState(), timezoneId: 'America/Los_Angeles' })
    const page = await context.newPage()
    const mutations: string[] = []
    page.on('pageerror', e => errors.push(e.message))
    page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/') && request.method() !== 'GET') mutations.push(request.url()) })
    await page.goto(baseURL!)
    await page.getByRole('navigation').getByRole('button', { name: 'Fristen', exact: true }).click()
    await page.getByLabel('Kalendermonat', { exact: true }).fill(series.submissionDate.slice(0, 7))
    await page.getByRole('combobox', { name: 'Fristtyp', exact: true }).selectOption('PsurSubmission')
    let event = page.getByTestId('calendar-event').filter({ hasText: series.reference })
    await expect(event).toContainText('ohne Uhrzeit')
    await expect(event).toContainText('keine Zeitzone gespeichert')
    await page.getByRole('combobox', { name: 'Anzeigezeitzone', exact: true }).selectOption('UTC')
    await expect(event.locator('time')).toHaveAttribute('datetime', series.submissionDate)
    await page.screenshot({ path: path.join(artifacts, 'cohors-calendar-month.png') })
    await event.scrollIntoViewIfNeeded()
    await page.screenshot({ path: path.join(artifacts, 'cohors-calendar-psur-agenda.png') })
    await event.getByRole('button').click()
    await expect(page.getByLabel('Unveränderlicher Snapshot / aktuelle Bearbeitungsfassung')).toHaveValue(report.id)
    await expect(page.getByLabel('PSUR-Serie / versionierte Gruppierung')).toHaveValue(group.id)
    await page.screenshot({ path: path.join(artifacts, 'cohors-calendar-psur-source.png') })

    await page.getByRole('navigation').getByRole('button', { name: 'Fristen', exact: true }).click()
    await page.getByLabel('Kalendermonat', { exact: true }).fill(nextMonth.slice(0, 7))
    await page.getByRole('combobox', { name: 'Fristtyp', exact: true }).selectOption('NotifiedBodyReply')
    event = page.getByTestId('calendar-event').filter({ hasText: review.reference })
    await expect(event.locator('time')).toContainText(new Intl.DateTimeFormat('de-DE',
      { timeZone: 'Europe/Berlin', hour: '2-digit', minute: '2-digit' }).format(new Date(`${lastDay}T23:30:00Z`)))
    await expect(event).toContainText('Originalzeitzone: UTC')
    await page.getByRole('button', { name: new RegExp(`^${nextMonth}:`) }).click()
    await expect(page.getByRole('region', { name: 'Fristenagenda' })).toContainText(review.reference)
    await page.screenshot({ path: path.join(artifacts, 'cohors-calendar-bs-boundary.png') })
    await page.getByRole('combobox', { name: 'Anzeigezeitzone', exact: true }).selectOption('UTC')
    await expect(page.getByText('Keine gespeicherten Termine im gewählten Filter', { exact: true })).toBeVisible()
    await page.getByLabel('Kalendermonat', { exact: true }).fill(lastDay.slice(0, 7))
    await expect(event.locator('time')).toContainText('23:30')
    await event.getByRole('button').click()
    await expect(page.getByLabel('BS-Vorgang dieser Serie')).toHaveValue(review.id)
    await expect(page.locator('.calendar-source-focus')).toContainText('Frage 1')
    await page.locator('.calendar-source-focus').scrollIntoViewIfNeeded()
    await page.screenshot({ path: path.join(artifacts, 'cohors-calendar-bs-source.png') })
    await page.getByRole('navigation').getByRole('button', { name: 'Fristen', exact: true }).click()
    await page.getByRole('combobox', { name: 'Anzeigezeitzone', exact: true }).selectOption('UTC')
    await page.getByLabel('Kalendermonat', { exact: true }).fill('0001-01')
    await expect(page.getByRole('button', { name: /^0001-01-\d{2}:/ })).toHaveCount(31)
    expect(mutations).toEqual([])
    expect(JSON.stringify(await (await pms.get(`/api/psurs/${report.id}`)).json())).toBe(reportBytes)
    expect(JSON.stringify(await (await pms.get(`/api/psur-reviews/${review.id}`)).json())).toBe(reviewBytes)
    expect(errors).toEqual([])
  } finally { await context?.close(); await pms.dispose(); await medical.dispose(); await ra.dispose(); await limited.dispose() }
})

test('R01: named recipient, independent simulated dispatch and original-bound response review', async ({ playwright, browser, baseURL }) => {
  test.setTimeout(210_000)
  requireWorkspaceIsolation(baseURL)
  const pms = await playwright.request.newContext({ baseURL })
  const medical = await playwright.request.newContext({ baseURL })
  const ra = await playwright.request.newContext({ baseURL })
  const limited = await playwright.request.newContext({ baseURL })
  const contexts: BrowserContext[] = []
  const pageErrors: string[] = []
  const posts: string[] = []
  const reason = 'SYNTHETISCH: Kontakt, konkreter Minimaldatensatz und exakte Originalreferenzen wurden getrennt geprueft. Keine reale Kommunikation und keine abschliessende Kausalitaetsbewertung.'
  const trace: { version: number; status: string }[] = []
  try {
    const pmsCsrf = await signIn(pms, 'pms.demo')
    await signIn(medical, 'medical.demo')
    const raCsrf = await signIn(ra, 'ra.demo')
    const limitedCsrf = await signIn(limited, 'rfi-cardio.demo')
    const stamp = `RFI-${Date.now()}`
    const input = signalInput(stamp)
    let c = await command(pms, pmsCsrf, '/cases', input)
    const deadlines = JSON.stringify(c.deadlines)
    const awareness = c.firstAwarenessAt
    const originalEvidence = c.evidence.length
    async function read() {
      c = await (await pms.get(`/api/cases/${c.id}`)).json()
      return c
    }
    async function pageFor(client: APIRequestContext) {
      const context = await browser.newContext({ storageState: await client.storageState(), timezoneId: 'UTC' })
      contexts.push(context)
      const page = await context.newPage()
      page.on('pageerror', e => pageErrors.push(e.message))
      page.on('request', request => { if (request.method() === 'POST') posts.push(new URL(request.url()).pathname) })
      await page.goto(baseURL!)
      await showCase(page, stamp, input.title)
      await page.getByRole('tab', { name: /^Informationsbedarf/ }).click()
      return page
    }
    const pmsPage = await pageFor(pms)
    await pmsPage.getByRole('button', { name: 'Anfrage entwerfen', exact: true }).click()
    let dialog = pmsPage.getByRole('dialog')
    await dialog.getByLabel('Synthetische Kontaktreferenz', { exact: true }).fill(`CONTACT-${stamp}`)
    await dialog.getByLabel('Organisation des Kontakts', { exact: true }).fill('Synthetische Demo-Klinik Nord')
    await dialog.getByLabel('Benannter Kontakt oder eindeutige Empfangsstelle', { exact: true }).fill('Synthetisches Befundteam Nord')
    await dialog.getByLabel('Rolle des Kontakts', { exact: true }).fill('Synthetische klinische Sicherheitsstelle')
    await dialog.getByRole('combobox', { name: 'Vorgesehener Kontaktkanal (nur simuliert)', exact: true }).selectOption('Email')
    await dialog.getByLabel('Synthetische Zielreferenz - keine echten Kontaktdaten', { exact: true }).fill('nord@example.invalid')
    await dialog.getByRole('combobox', { name: 'Pilotgrenze bestaetigen', exact: true }).selectOption('true')
    await submitDialog(dialog, 'Anfrageentwurf speichern')
    await read()
    let request = c.informationRequests[0]
    expect(request.lifecycle?.recipient.contact).toBe('Synthetisches Befundteam Nord')
    expect(request.lifecycle?.recipient.destination).toBe('nord@example.invalid')
    expect(request.status).toBe('Draft')
    trace.push({ version: c.version, status: request.status })
    const requestCard = (page: Page) => page.getByRole('article', { name: `Anfrage ${request.id}`, exact: true })
    await expect(requestCard(pmsPage)).toContainText('Synthetische Demo-Klinik Nord')
    await expect(requestCard(pmsPage)).toContainText('nord@example.invalid')
    await expect(requestCard(pmsPage).getByRole('button', { name: /^Als .* genehmigen$/ })).toHaveCount(0)
    const medicalPage = await pageFor(medical)
    await requestCard(medicalPage).getByRole('button', { name: 'Als Medical Safety genehmigen', exact: true }).click()
    dialog = medicalPage.getByRole('dialog')
    await dialog.getByLabel('Fachliche Begruendung', { exact: true }).fill(reason)
    await submitDialog(dialog)
    const raPage = await pageFor(ra)
    await requestCard(raPage).getByRole('button', { name: 'Als RA genehmigen', exact: true }).click()
    dialog = raPage.getByRole('dialog')
    await dialog.getByLabel('Fachliche Begruendung', { exact: true }).fill(reason)
    await submitDialog(dialog)
    await read()
    request = c.informationRequests[0]
    expect(request.status).toBe('Approved')
    expect(request.lifecycle?.dispatches).toHaveLength(0)
    expect(new Set(request.approvals.map(a => a.actorId)).size).toBe(2)
    trace.push({ version: c.version, status: request.status })
    await expect(requestCard(raPage)).toContainText('Freigegeben - noch nicht versendet')
    const denied = await limited.post(`/api/cases/${c.id}/information/${request.id}/dispatch`, {
      headers: { 'X-CSRF-Token': limitedCsrf, 'Idempotency-Key': crypto.randomUUID() },
      data: { expectedVersion: c.version, scenario: 'Accept', reason },
    })
    expect(denied.status()).toBe(403)

    async function dispatch(scenario: string, expected: string) {
      await requestCard(raPage).getByRole('button', { name: 'Simulierten Versand beauftragen', exact: true }).click()
      const prompt = raPage.getByRole('dialog')
      await prompt.getByRole('combobox', { name: 'Explizites Simulator-Szenario', exact: true }).selectOption(scenario)
      await prompt.getByLabel('Fachliche Begruendung', { exact: true }).fill(reason)
      const pendingResponse = raPage.waitForResponse(response => response.request().method() === 'POST' &&
        new URL(response.url()).pathname === `/api/cases/${c.id}/information/${request.id}/dispatch`)
      await submitDialog(prompt, 'Nur internen Simulator beauftragen')
      const pending: SafetyCase = await (await pendingResponse).json()
      expect(pending.informationRequests[0].status).toBe('SendPending')
      expect(pending.informationRequests[0].lifecycle?.dispatches.at(-1)?.receiptReference).toBeNull()
      trace.push({ version: pending.version, status: 'SendPending' })
      await expect.poll(async () => { await read(); return c.informationRequests[0].status }).toBe(expected)
      request = c.informationRequests[0]
      trace.push({ version: c.version, status: request.status })
    }
    await dispatch('Fail', 'SendFailed')
    await expect(requestCard(raPage).getByText('Versand fehlgeschlagen', { exact: true })).toBeVisible()
    expect(request.lifecycle?.dispatches[0].receiptReference).toBeNull()
    await dispatch('Unknown', 'TransmissionUnknown')
    await expect(requestCard(raPage).getByText('Versand unklar - Abgleich erforderlich', { exact: true })).toBeVisible()
    await expect(requestCard(raPage).getByRole('button', { name: 'Simulierten Versand beauftragen', exact: true })).toHaveCount(0)
    expect(request.lifecycle?.dispatches).toHaveLength(2)
    await requestCard(raPage).getByRole('button', { name: 'Empfang gezielt abgleichen', exact: true }).click()
    dialog = raPage.getByRole('dialog')
    await dialog.getByLabel('Fachliche Begruendung', { exact: true }).fill(reason)
    await submitDialog(dialog)
    await read()
    request = c.informationRequests[0]
    expect(request.status).toBe('SimulatedSent')
    expect(request.lifecycle?.dispatches).toHaveLength(2)
    expect(request.lifecycle?.dispatches[1].receiptReference).toMatch(/^DEMO-RFI-/)
    trace.push({ version: c.version, status: request.status })
    await raPage.screenshot({ path: path.join(artifacts, 'cohors-rfi-recipient-and-delivery.png') })
    await expect(requestCard(pmsPage).getByText('Im Simulator zugestellt', { exact: true })).toBeVisible()
    await requestCard(pmsPage).getByRole('button', { name: 'Antwort simulieren', exact: true }).click()
    dialog = pmsPage.getByRole('dialog')
    const externalId = `ORIGINAL-${stamp}`
    await dialog.getByLabel('Originalreferenz des Ruecklaufs', { exact: true }).fill(externalId)
    await expect(dialog.getByLabel('Anfrage-ID laut Original (leer, wenn nicht vorhanden)', { exact: true })).toHaveValue(request.id)
    await expect(dialog.getByLabel('Korrelation laut Original (leer, wenn nicht vorhanden)', { exact: true })).toHaveValue(request.lifecycle!.correlationId)
    await dialog.getByLabel('Anfrage-ID laut Original (leer, wenn nicht vorhanden)', { exact: true }).fill('')
    await dialog.getByLabel('Korrelation laut Original (leer, wenn nicht vorhanden)', { exact: true }).fill('')
    await dialog.getByRole('combobox', { name: 'Pilotgrenze bestaetigen', exact: true }).selectOption('true')
    const original = 'SYNTHETISCHER ORIGINALBEFUND:\nFunktionsverlust beschrieben; Ursache und klinische Befundqualitaet bleiben ungeklaert.'
    await dialog.getByRole('textbox', { name: 'Synthetischer Originalbefund', exact: true }).fill(original)
    await dialog.getByLabel('Fachliche Begruendung', { exact: true }).fill(reason)
    await submitDialog(dialog, 'Originalruecklauf speichern')
    await read()
    let response = c.informationResponses![0]
    expect(response.status).toBe('Held')
    expect(response.mapping).toBeNull()
    expect(c.evidence).toHaveLength(originalEvidence)
    expect(response.content).toBe(original)
    expect(response.contentHash).toBe(createHash('sha256').update(original).digest('hex'))
    trace.push({ version: c.version, status: response.status })
    const responseCard = (page: Page) => page.getByRole('article', { name: `Ruecklauf ${externalId}`, exact: true })
    await expect(responseCard(pmsPage)).toContainText('Zurueckgehalten - Zuordnung unklar')
    await responseCard(pmsPage).getByRole('button', { name: 'Ruecklauf begruendet zuordnen', exact: true }).click()
    dialog = pmsPage.getByRole('dialog')
    const select = dialog.getByRole('combobox', { name: 'Exakte Anfrage mit bestaetigtem simuliertem Empfang', exact: true })
    await expect(select).toHaveValue('')
    await select.selectOption(request.id)
    await dialog.getByLabel('Fachliche Begruendung', { exact: true }).fill(reason)
    await dialog.getByRole('button', { name: 'Als unbestaetigte Evidenz zuordnen', exact: true }).click()
    await expect(dialog.getByRole('alert')).toContainText('Fehlende Originalreferenzen')
    await dialog.getByRole('combobox', { name: 'Fehlende Originalreferenzen', exact: true }).selectOption('true')
    await submitDialog(dialog, 'Als unbestaetigte Evidenz zuordnen')
    await read()
    response = c.informationResponses![0]
    expect(response.status).toBe('Mapped')
    expect(response.mapping?.requestId).toBe(request.id)
    expect(response.mapping?.missingReferencesConfirmed).toBe(true)
    expect(response.declaredRequestId).toBe('')
    expect(response.envelope.correlationId).toBeNull()
    expect(c.evidence.find(e => e.id === response.mapping?.evidenceId)?.approved).toBe(false)
    expect(c.stage).toBe('AwaitingMedicalReview')
    expect(c.agentRuns).toHaveLength(0)
    trace.push({ version: c.version, status: 'MappedUnconfirmed' })
    await expect(responseCard(pmsPage)).toContainText('Zugeordnet - Befund unbestaetigt')
    const deniedReview = await ra.post(`/api/cases/${c.id}/information/responses/${response.id}/review`, {
      headers: { 'X-CSRF-Token': raCsrf, 'Idempotency-Key': crypto.randomUUID() }, data: { expectedVersion: c.version, reason },
    })
    expect(deniedReview.status()).toBe(403)
    await expect(responseCard(medicalPage)).toContainText('Zugeordnet - Befund unbestaetigt')
    await medicalPage.setViewportSize({ width: 980, height: 850 })
    await responseCard(medicalPage).scrollIntoViewIfNeeded()
    await medicalPage.screenshot({ path: path.join(artifacts, 'cohors-rfi-unconfirmed-response.png') })
    await responseCard(medicalPage).getByRole('button', { name: 'Herkunft und Befundquelle pruefen', exact: true }).click()
    dialog = medicalPage.getByRole('dialog')
    await dialog.getByLabel('Fachliche Begruendung', { exact: true }).fill(reason)
    await submitDialog(dialog, 'Nur Quelle fuer Bewertung freigeben')
    await read()
    response = c.informationResponses![0]
    expect(response.review?.actorId).toBe(request.approvals.find(a => a.role === 'MedicalSafetyReviewer')?.actorId)
    expect(c.evidence.find(e => e.id === response.mapping?.evidenceId)?.approved).toBe(true)
    expect(c.evidence.find(e => e.id === response.mapping?.evidenceId)?.content).toBe(original)
    expect(JSON.stringify(c.deadlines)).toBe(deadlines)
    expect(c.firstAwarenessAt).toBe(awareness)
    expect(c.agentRuns).toHaveLength(0)
    expect(c.submissions).toHaveLength(0)
    expect(c.approvals).toHaveLength(0)
    expect(posts.some(p => /\/assessment$|\/report$|\/submit$/.test(p))).toBe(false)
    expect(pageErrors).toEqual([])
    trace.push({ version: c.version, status: 'SourceReviewedNotCaseApproved' })
    writeFileSync(path.join(artifacts, 'information-lifecycle-workflow.json'), JSON.stringify({ caseId: c.id, version: c.version,
      requestId: request.id, correlationId: request.lifecycle?.correlationId, recipient: request.lifecycle?.recipient,
      trace, responseId: response.id, originalHash: response.originalHash, contentHash: response.contentHash,
      preservedMissingReferences: response.declaredRequestId === '' && response.envelope.correlationId === null,
      separateMedicalSourceReview: true, deadlinesPreserved: true, originalAwarenessPreserved: true,
      automaticAnalyses: 0, realTransmissions: 0, currentCaseApprovals: 0, pageErrors }, null, 2))
  } finally {
    for (const context of contexts) await context.close()
    await pms.dispose(); await medical.dispose(); await ra.dispose(); await limited.dispose()
  }
})

test('V01: agent-prefilled report, grounded fields, missing-data return and separate Medical/RA review', async ({ playwright, browser, baseURL }) => {
  test.setTimeout(210_000)
  const pms = await playwright.request.newContext({ baseURL })
  const medical = await playwright.request.newContext({ baseURL })
  const ra = await playwright.request.newContext({ baseURL })
  const contexts: BrowserContext[] = []
  const pageErrors: string[] = []
  try {
    const pmsCsrf = await signIn(pms, 'pms.demo')
    await signIn(medical, 'medical.demo')
    await signIn(ra, 'ra.demo')
    const incidentKey = `PREFILL-${Date.now()}`
    const input = { incidentKey, sourceId: crypto.randomUUID(), title: 'Synthetischer Prüffall: agentengestützter Meldungsentwurf',
      description: 'SYNTHETISCH: Funktionsverlust und Revisionsbedarf wurden berichtet. Die genaue gesundheitliche Folge und Kausalität sind noch nicht bestätigt; keine Schlussfolgerung allein aus der Dringlichkeitsklasse.',
      productId: 'ortho', udi: 'DEMO-PREFILL-UDI', batch: 'DEMO-PREFILL', country: 'IE', jurisdiction: 'EU-IE',
      seriousness: 'DeathOrSeriousDeterioration', occurredAt: new Date(Date.now() - 2 * 86_400_000).toISOString(),
      awarenessAt: new Date(Date.now() - 86_400_000).toISOString(), scenario: 'report-prefill-ui', synthetic: true }
    let c = await command(pms, pmsCsrf, '/cases', input)
    c = await command(pms, pmsCsrf, `/cases/${c.id}/assessment`, { expectedVersion: c.version, mode: 'Replay',
      reason: 'Begründete synthetische Analyse als tatsächliche Grundlage des getrennten Vigilance-Autorenlaufs erstellen.' })
    await expect.poll(async () => {
      c = await (await pms.get(`/api/cases/${c.id}`)).json()
      return c.currentAssessment?.version
    }).toBe(1)
    const deadlines = JSON.stringify(c.deadlines)
    async function pageFor(client: APIRequestContext) {
      const context = await browser.newContext({ storageState: await client.storageState(), timezoneId: 'UTC' })
      contexts.push(context)
      const page = await context.newPage()
      page.on('pageerror', e => pageErrors.push(e.message))
      await page.goto(baseURL!)
      await showCase(page, incidentKey, input.title)
      return page
    }
    const pmsPage = await pageFor(pms)
    await pmsPage.getByRole('region', { name: 'Fallprozess', exact: true }).getByRole('button', { name: 'Entwurf vorbereiten' }).click()
    let dialog = pmsPage.getByRole('dialog')
    await dialog.locator('[name=reason]').fill('Konkreten quellengebundenen Meldungsentwurf erzeugen, aber keine fehlenden Fakten oder menschlichen Termine erfinden.')
    await submitDialog(dialog, 'Versionierten Entwurf erstellen')
    await expect(pmsPage.getByRole('heading', { name: 'Prüfansicht des vorausgefüllten Meldungsentwurfs' })).toBeVisible()
    await expect(pmsPage.locator('.report-target')).toContainText('Health Products Regulatory Authority (HPRA)')
    await expect(pmsPage.locator('.report-target')).toContainText('HPRA-SIMULATOR')
    await expect(pmsPage.getByTestId('report-gaps')).toContainText('Nicht automatisch befüllbar – Angaben oder Belege erforderlich')
    await expect(pmsPage.getByTestId('report-gaps')).toContainText('Belegter Hersteller-/Melderkontakt · Vorname')
    await expect(pmsPage.getByTestId('report-gaps')).not.toContainText('ContactFirstName')
    await expect(pmsPage.locator('[data-issue-field="contactFirstName"]')).toContainText('Fehlende oder nicht verwendbare Quelle')
    await expect(pmsPage.locator('[data-issue-field="nextReportDate"]')).toContainText('Menschliche Entscheidung erforderlich')
    await pmsPage.locator('[data-issue-field="contactFirstName"]').getByRole('button', { name: 'Angabe belegen / Widerspruch klären' }).click()
    dialog = pmsPage.getByRole('dialog')
    await expect(dialog.locator('[name=contactFirstName]')).toHaveValue('')
    await pmsPage.keyboard.press('Escape')
    c = await (await pms.get(`/api/cases/${c.id}`)).json()
    const draft = c.currentReport!
    expect(draft.preparation?.mode).toBe('Replay')
    expect(draft.mir?.facts.eventDateFrom).toBe(input.occurredAt.slice(0, 10))
    expect(draft.mir?.facts.eventClassification).toBeNull()
    expect(draft.mir?.facts.nextReportDate).toBeNull()
    expect(draft.mir?.facts.initialCorrectiveAction).toBeNull()
    expect(c.approvals).toHaveLength(0)
    expect(c.submissions).toHaveLength(0)
    expect(JSON.stringify(c.deadlines)).toBe(deadlines)
    await pmsPage.screenshot({ path: path.join(artifacts, 'cohors-prefill-agent-draft.png') })
    const proposed = pmsPage.locator('[data-field="preliminaryAnalysis"]')
    await expect(proposed).toContainText('Agentenvorschlag · unbestätigt')
    await proposed.scrollIntoViewIfNeeded()
    await pmsPage.screenshot({ path: path.join(artifacts, 'cohors-prefill-field-origin.png') })
    await proposed.getByRole('button', { name: /Originalbeleg/ }).first().click()
    await expect(pmsPage.getByRole('dialog').getByText('Ausgewählte Textfassung · Original-Hash verifiziert', { exact: true })).toBeVisible()
    await pmsPage.screenshot({ path: path.join(artifacts, 'cohors-prefill-source-document.png') })
    await pmsPage.keyboard.press('Escape')

    const raPage = await pageFor(ra)
    await raPage.getByRole('tab', { name: 'Human Review & Meldung' }).click()
    await expect(raPage.getByRole('button', { name: 'MIR-Pflichtdaten bearbeiten' })).toHaveCount(0)
    await expect(raPage.getByRole('button', { name: 'Regulatorisch freigeben', exact: true })).toHaveCount(0)
    await raPage.getByRole('button', { name: 'Zur Korrektur zurückgeben', exact: true }).click()
    dialog = raPage.getByRole('dialog')
    const returnReason = 'PMS: Belegten Herstellerkontakt, Geräteverbleib, genaue Ereignisfolge und ausdrücklich menschlich zugesagten Folgetermin ergänzen. Keine Daten erfinden.'
    await dialog.locator('[name=reason]').fill(returnReason)
    await submitDialog(dialog, 'Zur Korrektur zurückgeben')
    c = await (await pms.get(`/api/cases/${c.id}`)).json()
    expect(c.currentReport?.reviewReturn?.role).toBe('RegulatoryAffairs')
    expect(c.currentReport?.scopeHash).not.toBe(draft.scopeHash)
    expect(c.currentReport?.reviewReturn?.reason).toBe(returnReason)
    expect(JSON.stringify(c.deadlines)).toBe(deadlines)

    await pmsPage.reload()
    await showCase(pmsPage, incidentKey, input.title)
    await pmsPage.getByRole('tab', { name: 'Human Review & Meldung' }).click()
    await pmsPage.getByRole('button', { name: 'MIR-Pflichtdaten bearbeiten' }).click()
    dialog = pmsPage.getByRole('dialog')
    await expect(dialog.locator('[name=preliminaryAnalysis]')).toHaveValue(draft.mir!.facts.preliminaryAnalysis!)
    await expect(dialog.locator('[name=nextReportDate]')).toHaveValue('')
    await dialog.locator('[name=eventClassification]').selectOption('Unanticipated serious deterioration in state of health')
    await dialog.locator('[name=deviceProblemCode]').fill('ASYN')
    await dialog.locator('[name=deviceLocation]').selectOption('Unknown')
    const nextDate = new Date(Date.now() + 7 * 86_400_000).toISOString().slice(0, 10)
    await dialog.locator('[name=nextReportDate]').fill(nextDate)
    for (const [key, value] of Object.entries({ contactFirstName: 'Synthetic', contactLastName: 'Testcontact', contactStreet: 'Demo street',
      contactCity: 'Demo city', contactPostcode: 'DEMO', contactPhone: '000000', contactEmail: 'synthetic@example.invalid' })) {
      await dialog.locator(`[name=${key}]`).fill(value)
    }
    await dialog.locator('[name=submitterIsManufacturerContact]').selectOption('true')
    await dialog.locator('[name=rationale]').fill('SYNTHETISCHE TESTANGABEN: ASYN und der Herstellerkontakt sind explizite synthetische Prüfwerte, kein realer Kontakt oder fachlich validierter Code. Der Folgetermin wird hier bewusst menschlich zugesagt; die tatsächliche Kausalität bleibt offen.')
    await dialog.locator('[name=reason]').fill('PMS beantwortet die konkrete Rückgabe mit expliziten synthetischen Belegen; vorhandene Agententexte bleiben unverändert.')
    await submitDialog(dialog, 'Versioniert speichern und gegen Original-XSD prüfen')
    await expect.poll(async () => {
      c = await (await pms.get(`/api/cases/${c.id}`)).json()
      return c.currentReport?.mir?.valid
    }).toBe(true)
    expect(c.currentReport?.reviewReturn).toBeUndefined()
    expect(c.currentReport?.mir?.facts.preliminaryAnalysis).toBe(draft.mir?.facts.preliminaryAnalysis)
    expect(c.currentReport?.preparation?.fields.find(f => f.field === 'preliminaryAnalysis')?.origin).toBe('AgentDraft')
    expect(c.currentReport?.preparation?.fields.find(f => f.field === 'nextReportDate')?.origin).toBe('HumanEntry')
    expect(c.currentReport?.mir?.followUpSchedule?.reportDate).toBe(nextDate)
    expect(c.currentReport?.mir?.facts.initialCorrectiveAction).toBeNull()
    expect(c.currentReport?.mir?.facts.udiDi).toBe(input.udi)
    await pmsPage.screenshot({ path: path.join(artifacts, 'cohors-prefill-corrected.png') })

    const medicalPage = await pageFor(medical)
    await medicalPage.getByRole('tab', { name: 'Human Review & Meldung' }).click()
    await medicalPage.getByRole('button', { name: 'Medizinisch freigeben', exact: true }).click()
    dialog = medicalPage.getByRole('dialog')
    await dialog.locator('[name=reason]').fill('Quellen, synthetische Pflichtangaben, Unsicherheit und konkretes Berichtspaket medizinisch geprüft.')
    await submitDialog(dialog, 'Medizinisch freigeben')
    await raPage.reload()
    await showCase(raPage, incidentKey, input.title)
    await raPage.getByRole('tab', { name: 'Human Review & Meldung' }).click()
    await expect(raPage.getByRole('button', { name: 'MIR-Pflichtdaten bearbeiten' })).toHaveCount(0)
    await raPage.getByRole('button', { name: 'Regulatorisch freigeben', exact: true }).click()
    dialog = raPage.getByRole('dialog')
    await dialog.locator('[name=reason]').fill('RA prüft und bestätigt das bereits ausgefüllte konkrete Paket, Zielprofil und die Grenzen des ausschließlich simulierten Versands.')
    await submitDialog(dialog, 'Regulatorisch freigeben')
    c = await (await ra.get(`/api/cases/${c.id}`)).json()
    expect(c.currentReport?.state).toBe('Frozen')
    expect(c.submissions).toHaveLength(0)
    expect(JSON.stringify(c.deadlines)).toBe(deadlines)
    const approvals = c.approvals.filter(a => a.scopeHash === c.currentReport?.scopeHash)
    expect(approvals.map(a => a.role).sort()).toEqual(['MedicalSafetyReviewer', 'RegulatoryAffairs'])
    expect(new Set(approvals.map(a => a.actorId)).size).toBe(2)
    expect(c.currentReport?.preparation?.target.nationalSubmissionReady).toBe(false)
    await raPage.screenshot({ path: path.join(artifacts, 'cohors-prefill-ra-approved.png') })
    await raPage.setViewportSize({ width: 390, height: 844 })
    const target = raPage.locator('.report-target').first()
    await target.scrollIntoViewIfNeeded()
    const processHeight = (await raPage.getByRole('region', { name: 'Fallprozess', exact: true }).boundingBox())!.height
    await target.evaluate((element, offset) => { element.scrollIntoView({ block: 'start' }); window.scrollBy(0, -offset) }, processHeight + 16)
    await expect(target).toBeInViewport()
    expect(await raPage.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    await raPage.screenshot({ path: path.join(artifacts, 'cohors-prefill-ra-narrow.png') })
    const frozen = c.currentReport!
    const originalApprovals = JSON.stringify(c.approvals)
    const originalFacts = JSON.stringify(frozen.mir!.facts)
    const previousCaseVersion = c.version
    await medicalPage.reload()
    await showCase(medicalPage, incidentKey, input.title)
    await medicalPage.getByRole('tab', { name: 'Human Review & Meldung' }).click()
    await medicalPage.getByRole('button', { name: 'Pflichtfelder erneut durch Agenten vervollständigen', exact: true }).click()
    dialog = medicalPage.getByRole('dialog')
    await expect(dialog.locator('[name=contactFirstName]')).toHaveCount(0)
    await expect(dialog.locator('[name=nextReportDate]')).toHaveCount(0)
    await submitDialog(dialog, 'Erneut vervollständigen')
    c = await (await medical.get(`/api/cases/${c.id}`)).json()
    expect(c.currentReport?.version).toBe(frozen.version + 1)
    expect(c.currentReport?.state).toBe('Draft')
    expect(c.currentReport?.scopeHash).not.toBe(frozen.scopeHash)
    expect(c.currentReport?.preparation?.caseSource.version).toBe(previousCaseVersion)
    expect(c.currentReport?.mir?.valid).toBe(true)
    expect(JSON.stringify(c.currentReport?.mir?.facts)).toBe(originalFacts)
    expect(JSON.stringify(c.approvals)).toBe(originalApprovals)
    expect(c.approvals.filter(a => a.scopeHash === c.currentReport?.scopeHash)).toHaveLength(0)
    expect(c.currentReport?.preparation?.fields.find(f => f.field === 'nextReportDate')?.origin).toBe('HumanEntry')
    expect(c.currentReport?.preparation?.fields.find(f => f.field === 'contactFirstName')?.origin).toBe('HumanEntry')
    expect(JSON.stringify(c.deadlines)).toBe(deadlines)
    expect(c.submissions).toHaveLength(0)
    await expect(medicalPage.getByRole('button', { name: 'Medizinisch freigeben', exact: true })).toBeEnabled()
    await medicalPage.screenshot({ path: path.join(artifacts, 'cohors-prefill-medical-completion.png') })
    expect(pageErrors).toEqual([])
    writeFileSync(path.join(artifacts, 'prefill-workflow.json'), JSON.stringify({ caseId: c.id, version: c.version,
      reportVersion: c.currentReport?.version, scopeHash: c.currentReport?.scopeHash, xmlHash: c.currentReport?.mir?.xmlHash,
      target: c.currentReport?.preparation?.target, roles: approvals.map(a => a.role), differentIdentities: true,
      initialGapsPreserved: true, agentTextPreserved: true, deadlinePreserved: true, hiddenUdiPreserved: true,
      medicalCompletionWithoutRetyping: true, historicalApprovalsPreserved: true, currentApprovals: 0,
      automaticSubmissions: 0, pageErrors }, null, 2))
  } finally {
    for (const context of contexts) await context.close()
    await Promise.all([pms.dispose(), medical.dispose(), ra.dispose()])
  }
})

test('UI01: persistent case process, explicit next action, failed reanalysis and verified claim document', async ({ playwright, browser, baseURL }) => {
  test.setTimeout(150_000)
  const pms = await playwright.request.newContext({ baseURL })
  let context: BrowserContext | undefined
  try {
    const csrf = await signIn(pms, 'pms.demo')
    const incidentKey = `UI-PROCESS-${Date.now()}`
    const input = { incidentKey, sourceId: crypto.randomUUID(), title: 'Synthetischer UI-Prüffall: Prozess und Fundstellen',
      description: 'SYNTHETISCH: Vorzeitiger Funktionsverlust wurde berichtet. Technischer Befund und Ursachenabgrenzung stehen aus. Dieser Fall dient nur der isolierten Bedienprüfung.',
      productId: 'ortho', udi: null, batch: 'DEMO-UI-PROCESS', country: 'IE', jurisdiction: 'EU-IE',
      seriousness: 'DeathOrSeriousDeterioration', occurredAt: null, awarenessAt: new Date(Date.now() - 86_400_000).toISOString(),
      scenario: 'case-documents-ui', synthetic: true }
    let c = await command(pms, csrf, '/cases', input)
    c = await command(pms, csrf, `/cases/${c.id}/assessment`, { expectedVersion: c.version, mode: 'Replay',
      reason: 'Isolierte Bedienprüfung mit begründeter synthetischer Analyse und echten gespeicherten Fundstellen.' })
    await expect.poll(async () => {
      c = await (await pms.get(`/api/cases/${c.id}`)).json()
      return c.currentAssessment?.version
    }).toBe(1)
    const due = c.deadlines[0].legalDue
    context = await browser.newContext({ storageState: await pms.storageState() })
    const page = await context.newPage()
    await page.goto(baseURL!)
    await expect(page.getByRole('heading', { name: 'Sicherheit im Blick.' })).toBeVisible()
    await expect(page.getByRole('region', { name: 'Fallprozess', exact: true })).toHaveCount(0)
    await page.getByRole('navigation').getByRole('button', { name: 'Fallakten', exact: true }).click()
    await page.getByRole('textbox', { name: 'Fälle durchsuchen' }).fill(incidentKey)
    const row = page.getByRole('row').filter({ has: page.getByRole('button', { name: input.title, exact: true }) })
    await expect(row).toBeVisible()
    const listText = await row.textContent()
    await page.screenshot({ path: path.join(artifacts, 'cohors-case-list.png') })
    await row.getByRole('button', { name: input.title, exact: true }).click()
    await expect(page.getByRole('heading', { name: 'Assessment v1', exact: true })).toBeVisible()
    await page.screenshot({ path: path.join(artifacts, 'cohors-case-overview.png') })
    const progress = page.getByRole('region', { name: 'Fallprozess', exact: true })
    await expect(progress.locator('[data-step=analysis]')).toHaveAttribute('data-state', 'completed')
    expect(listText).toContain('Fristenklasse:')
    expect(listText).toContain('Patientenausgang unklar / nicht bestätigt')
    await expect(progress.getByText('Fristenklasse:', { exact: false })).toBeVisible()
    await expect(progress.getByText('Patientenausgang unklar / nicht bestätigt', { exact: true })).toBeVisible()
    await expect(progress.locator('[data-step=medical]')).not.toHaveAttribute('data-state', 'completed')
    await progress.getByRole('button', { name: 'Entwurf vorbereiten', exact: true }).click()
    await expect(page.getByRole('tab', { name: 'Human Review & Meldung' })).toHaveAttribute('aria-selected', 'true')
    await expect(page.getByRole('dialog')).toBeVisible()
    expect((await (await pms.get(`/api/cases/${c.id}`)).json() as SafetyCase).reports).toHaveLength(0)
    await page.getByRole('dialog').getByRole('button', { name: 'Abbrechen', exact: true }).click()
    await page.getByRole('tab', { name: 'Analyse & Evidenz' }).click()
    const source = c.evidence[0]
    const opener = page.locator('.evidence-item').filter({ hasText: source.title })
    await expect(opener.getByLabel('Dokumentenkategorie: Beschwerde', { exact: true })).toBeVisible()
    await opener.click()
    const document = page.getByRole('dialog', { name: source.title, exact: true })
    await expect(document.getByText('Ausgewählte Textfassung · Original-Hash verifiziert', { exact: true })).toBeVisible()
    await expect(document.getByLabel('Dokumentenkategorie: Beschwerde', { exact: true })).toBeVisible()
    expect(await document.getByRole('region', { name: 'Verifizierter Dokumenttext' }).textContent()).toBe(source.content)
    await expect(document.getByText(`Referenzhash: ${source.hash}`, { exact: true })).toBeVisible()
    await page.screenshot({ path: path.join(artifacts, 'cohors-case-document.png') })
    for (let i = 0; i < 8; i++) await page.keyboard.press('Tab')
    expect(await document.evaluate(element => element.contains(window.document.activeElement))).toBe(true)
    await page.keyboard.press('Escape')
    await expect(document).not.toBeVisible()
    await expect(opener).toBeFocused()
    const claim = c.currentAssessment!.content.claims[0]
    const citation = page.locator('button.citation').filter({ hasText: claim.id }).first()
    await citation.click()
    await expect(page.getByRole('dialog').getByText(claim.text, { exact: true })).toBeVisible()
    const quotation = page.getByRole('dialog').locator('blockquote').filter({ hasText: claim.citations[0].quote })
    await expect(quotation).toBeVisible()
    await expect(quotation.getByText(claim.citations[0].locator, { exact: true })).toBeVisible()
    await page.keyboard.press('Escape')
    await expect(citation).toBeFocused()
    await page.evaluate(() => window.scrollTo(0, 900))
    await expect.poll(async () => Math.round((await progress.boundingBox())!.y)).toBe(0)
    await page.screenshot({ path: path.join(artifacts, 'cohors-case-process-sticky.png') })
    c = await (await pms.get(`/api/cases/${c.id}`)).json()
    c = await command(pms, csrf, `/cases/${c.id}/assessment`, { expectedVersion: c.version, mode: 'Failure',
      reason: 'Expliziter synthetischer Ausfalltest: alter Entwurf darf nicht als erfolgreicher neuer Lauf erscheinen.' })
    await expect.poll(async () => {
      c = await (await pms.get(`/api/cases/${c.id}`)).json()
      return c.agentRuns.filter(run => run.role === 'CausalityRisk').at(-1)?.status
    }).toBe('Failed')
    await expect(progress.locator('[data-step=analysis]')).toHaveAttribute('data-state', 'blocked')
    await expect(progress.locator('[data-step=regulatory]')).not.toHaveAttribute('data-state', 'completed')
    await expect(progress.getByRole('button', { name: 'Entwurf vorbereiten', exact: true })).toHaveCount(0)
    expect(c.deadlines[0].legalDue).toBe(due)
    expect(c.currentAssessment?.version).toBe(1)
    writeFileSync(path.join(artifacts, 'cohors-case-process.json'), JSON.stringify({
      caseId: c.id, caseVersion: c.version, originalLegalDue: due, finalLegalDue: c.deadlines[0].legalDue,
      projection: await (await pms.get(`/api/cases/${c.id}/process`)).json(),
      failedRun: c.agentRuns.filter(run => run.role === 'CausalityRisk').at(-1),
    }, null, 2))
  } finally { await context?.close(); await pms.dispose() }
})

test('UI02: historical library document, long narrow content, access failure and late-response isolation', async ({ playwright, browser, baseURL }) => {
  test.setTimeout(150_000)
  const pms = await playwright.request.newContext({ baseURL })
  const quality = await playwright.request.newContext({ baseURL })
  let context: BrowserContext | undefined
  let release: (() => void) | undefined
  try {
    const csrf = await signIn(pms, 'pms.demo')
    const qualityCsrf = await signIn(quality, 'quality.demo')
    const c = await command(pms, csrf, '/cases', { incidentKey: `UI-DOCUMENT-${Date.now()}`, sourceId: crypto.randomUUID(),
      title: 'Synthetischer UI-Prüffall: kontrollierte Dokumentrevision',
      description: 'SYNTHETISCH: Die isolierte Dokumentansicht wird an einer nachvollziehbaren Revision geprüft. Keine reale Änderung eines Medizinproduktes.',
      productId: 'ortho', udi: null, batch: 'DEMO-UI-DOCUMENT', country: 'IE', jurisdiction: 'EU-IE',
      seriousness: 'OtherSerious', occurredAt: null, awarenessAt: new Date(Date.now() - 86_400_000).toISOString(),
      scenario: 'case-documents-ui', synthetic: true })
    const original = ((await (await pms.get('/api/evidence')).json()) as Evidence[]).find(e => e.id === 'ortho-spec-v3')!
    expect(original).toBeTruthy()
    let change = await command<ChangeRequest>(pms, csrf, '/changes', { caseId: c.id, productId: 'ortho',
      documentId: original.id, documentVersion: original.version,
      proposedChange: 'Synthetischen kontrollierten Lesetext ergänzen, um historische Revision und lange Dokumentansicht zu prüfen.',
      rationale: 'Isolierte Nachverfolgbarkeit der unveränderten Vorversion; keine klinische oder regulatorische Freigabebehauptung.' })
    change = await command<ChangeRequest>(quality, qualityCsrf, `/changes/${change.id}/approve`, { expectedVersion: change.version,
      reason: 'Getrennte QM-Identität genehmigt ausschließlich die nachvollziehbare synthetische Testrevision.' })
    const revised = await command<Evidence>(quality, qualityCsrf, `/evidence/${original.id}/revise`, {
      expectedVersion: original.version, changeId: change.id, title: original.title + ' / synthetische UI-Langfassung',
      content: original.content + '\n\n' + 'SYNTHETISCHE LESERPRÜFUNG: Randbedingungen bleiben anhand tatsächlicher Befunde getrennt zu beurteilen.\n'.repeat(150) +
        '\n' + 'LANGE_UNGEBROCHENE_TESTREFERENZ_'.repeat(20) + '\nENDE DER SYNTHETISCHEN LANGFASSUNG.',
      validFrom: new Date(Date.now() + 86_400_000).toISOString(),
      reason: 'Genehmigte synthetische Revision nur im isolierten Prüfbestand; Originalfassung bleibt unverändert erhalten.',
    })
    context = await browser.newContext({ storageState: await pms.storageState() })
    const page = await context.newPage()
    await page.goto(baseURL!)
    await page.getByRole('navigation').getByRole('button', { name: 'Evidenzbibliothek', exact: true }).click()
    const card = page.getByRole('article').filter({ has: page.getByRole('heading', { name: revised.title, exact: true }) })
    await expect(card).toBeVisible()
    await page.screenshot({ path: path.join(artifacts, 'cohors-library-overview.png') })
    const currentOpener = card.getByRole('button', { name: `Dokument öffnen · v${revised.version}`, exact: true })
    await currentOpener.click()
    const document = page.getByRole('dialog')
    await expect(document.getByText('Ausgewählte Textfassung · Original-Hash verifiziert', { exact: true })).toBeVisible()
    await expect(document.getByLabel('Dokumentenkategorie: Technische Spezifikation', { exact: true })).toBeVisible()
    expect(await document.getByRole('region', { name: 'Verifizierter Dokumenttext' }).textContent()).toBe(revised.content)
    await page.screenshot({ path: path.join(artifacts, 'cohors-library-current-document.png') })
    await page.setViewportSize({ width: 390, height: 844 })
    const box = (await document.boundingBox())!
    expect(box.x).toBeGreaterThanOrEqual(0)
    expect(box.x + box.width).toBeLessThanOrEqual(390)
    expect(await document.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
    expect(await document.evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true)
    await document.evaluate(element => { element.scrollTop = element.scrollHeight })
    await page.screenshot({ path: path.join(artifacts, 'cohors-library-document-narrow.png') })
    await page.keyboard.press('Escape')
    await expect(currentOpener).toBeFocused()
    await page.setViewportSize({ width: 1512, height: 982 })
    await card.getByRole('button', { name: 'Versionshistorie laden', exact: true }).click()
    await card.getByRole('combobox', { name: 'Unveränderliche Revision' }).selectOption(String(original.version))
    const historicalCard = page.getByRole('article').filter({ has: page.getByRole('heading', { name: original.title, exact: true }) })
    const historicalOpener = historicalCard.getByRole('button', { name: `Dokument öffnen · v${original.version}`, exact: true })
    await historicalOpener.click()
    await expect(document.getByRole('heading', { name: original.title, exact: true })).toBeVisible()
    await expect(document.getByLabel('Dokumentenkategorie: Technische Spezifikation', { exact: true })).toBeVisible()
    await expect(document.getByText('Ausgewählte Textfassung · Original-Hash verifiziert', { exact: true })).toBeVisible()
    expect(await document.getByRole('region', { name: 'Verifizierter Dokumenttext' }).textContent()).toBe(original.content)
    await expect(document.getByText(`Referenzhash: ${original.hash}`, { exact: true })).toBeVisible()
    await page.screenshot({ path: path.join(artifacts, 'cohors-library-historical-document.png') })
    await page.keyboard.press('Escape')
    await expect(historicalOpener).toBeFocused()
    await signIn(context.request, 'pms-cardio.demo')
    const denied = page.waitForResponse(response => response.url().endsWith(`/evidence/${original.id}/versions/${original.version}/content`))
    await historicalOpener.click()
    expect((await denied).status()).toBe(403)
    await expect(document.getByRole('alert')).toBeVisible()
    await expect(document.getByText('Nicht verifiziert', { exact: true })).toBeVisible()
    await expect(document.getByRole('region', { name: 'Verifizierter Dokumenttext' })).toHaveCount(0)
    await page.keyboard.press('Escape')
    await signIn(context.request, 'pms.demo')
    const held = new Promise<void>(resolve => { release = resolve })
    let delayed = false
    let complete!: () => void
    const completed = new Promise<void>(resolve => { complete = resolve })
    await page.route(`**/api/evidence/${original.id}/versions/${original.version}/content`, async route => {
      const response = await route.fetch()
      delayed = true
      await held
      await route.fulfill({ response })
      complete()
    })
    await historicalOpener.click()
    await expect.poll(() => delayed).toBe(true)
    await expect(document.getByText('Originalprüfung läuft', { exact: true })).toBeVisible()
    await document.getByRole('button', { name: 'Dokument schließen', exact: true }).first().click()
    await historicalCard.getByRole('combobox', { name: 'Unveränderliche Revision' }).selectOption(String(revised.version))
    await currentOpener.click()
    await expect(document.getByText('Ausgewählte Textfassung · Original-Hash verifiziert', { exact: true })).toBeVisible()
    expect(await document.getByRole('region', { name: 'Verifizierter Dokumenttext' }).textContent()).toBe(revised.content)
    release!()
    await completed
    await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))))
    await expect(document.getByRole('heading', { name: revised.title, exact: true })).toBeVisible()
    expect(await document.getByRole('region', { name: 'Verifizierter Dokumenttext' }).textContent()).toBe(revised.content)
    writeFileSync(path.join(artifacts, 'cohors-document-versions.json'), JSON.stringify({
      id: original.id, original: { version: original.version, hash: original.hash },
      revised: { version: revised.version, hash: revised.hash, length: revised.content.length },
      actualOutOfScopeStatus: 403, delayedHistoricalResponseDidNotReplaceCurrent: true,
    }, null, 2))
  } finally { release?.(); await context?.close(); await pms.dispose(); await quality.dispose() }
})

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
  await dialog.locator('[name=eventDateRationale]').fill('SYNTHETISCHE BROWSERTESTANGABE: explizite zeitliche Testspanne aus dem gewählten Eingang, kein systemseitig erfundenes Ereignisdatum.')
  for (const [key, value] of Object.entries({ contactFirstName: 'Synthetic', contactLastName: 'Testcontact', contactStreet: 'Demo street',
    contactCity: 'Demo city', contactPostcode: 'DEMO', contactPhone: '000000', contactEmail: 'synthetic@example.invalid' })) {
    await dialog.locator(`[name=${key}]`).fill(value)
  }
  await dialog.locator('[name=submitterIsManufacturerContact]').selectOption('true')
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
  await raPage.getByLabel('Kalendermonat', { exact: true }).fill(nextDate.slice(0, 7))
  await raPage.getByRole('combobox', { name: 'Fristtyp', exact: true }).selectOption('FollowUp')
  const calendarEvent = raPage.getByTestId('calendar-event').filter({ hasText: incidentKey })
  await expect(calendarEvent).toContainText('Bestätigter Folgetermin')
  await expect(calendarEvent).toContainText('Offen')
  await expect(calendarEvent).toContainText('Europe/Dublin')
  await raPage.getByLabel('Kalendermonat', { exact: true }).fill(due.slice(0, 7))
  await raPage.getByRole('combobox', { name: 'Fristtyp', exact: true }).selectOption('LegalDue')
  await expect(calendarEvent).toContainText('Simuliert erledigt')
  await expect(calendarEvent).toContainText('Keine offene Terminpflicht')
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
  await page.getByLabel('Kalendermonat', { exact: true }).fill('2026-11')
  await page.getByRole('combobox', { name: 'Fristtyp', exact: true }).selectOption('LegalDue')
  const calendarEvent = page.getByTestId('calendar-event').filter({ hasText: incidentKey })
  await expect(calendarEvent.locator('time')).toContainText('18. Nov. 2026, 00:59')
  await expect(calendarEvent).toContainText('Originalzeitzone: Europe/Dublin · 17. Nov. 2026, 23:59')
  await page.getByRole('combobox', { name: 'Anzeigezeitzone', exact: true }).selectOption('Europe/Dublin')
  await expect(calendarEvent.locator('time')).toContainText('17. Nov. 2026, 23:59')
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
      ...c.currentReport!.mir!.facts,
      eventDateFrom: eventDay, eventDateTo: eventDay, nextReportDate: day(7), eventClassification: 'All other reportable incidents',
      eventDateRationale: 'SYNTHETISCHE TESTANGABE: menschlich festgelegte Testspanne, keine aus Systemeingang abgeleitete Herstellerinformation.',
      contactFirstName: 'Synthetic', contactLastName: 'Testcontact', contactStreet: 'Demo street', contactCity: 'Demo city',
      contactPostcode: 'DEMO', contactPhone: '000000', contactEmail: 'synthetic@example.invalid', submitterIsManufacturerContact: true,
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
