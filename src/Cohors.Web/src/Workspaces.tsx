import { useEffect, useState } from 'react'
import { Activity, ArrowUpRight, CheckCheck, Download, FileCheck2, FileText, Fingerprint, Link2, Plus, RefreshCw, ShieldCheck } from 'lucide-react'
import { api, downloadApi } from './api'
import type { Actor, AuditEvent, Capa, CaseSummary, ChangeRequest, Evidence, ProductGovernance, ProductLifecycle, Psur, PsurGroupDefinition, RetentionRecord, RetentionView, SourceRun, Trend, VersionReference } from './types'
import { Badge, date, Empty, ErrorBanner, hash, label, Loading, SectionHead, type Dialog, type Field } from './ui'

function useData<T>(path: string, revision: number) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState('')
  useEffect(() => { let active = true; setData(null); setError(''); api<T>(path).then(data => { if (active) setData(data) }).catch(e => { if (active) setError(e.message) }); return () => { active = false } }, [path, revision])
  return { data, error }
}
type WorkspaceProps = { actor: Actor; revision: number; mutate: (path: string, body: unknown) => Promise<void>; openDialog: (d: Dialog) => void }
const reason: Field = { name: 'reason', label: 'Begründung / fachliche Entscheidung', type: 'textarea', minLength: 20 }

const retentionNames: Record<string, string> = {
  Registered: 'Registriert', Held: 'Hold aktiv', Authorized: 'Löschung autorisiert', Deleting: 'Vorgang begonnen',
  DeletionFailed: 'Teilvorgang offen', Deleted: 'Lokal gelöscht',
  lifecycle_missing_or_unconfirmed: 'Letztes Inverkehrbringen fehlt oder ist unbestätigt.',
  minimum_interval_not_elapsed: 'Mindestintervall ist nicht nachweislich abgelaufen.',
  regulatory_deletion_not_authorized_in_pilot: 'Regulatorische Evidenz wird im Piloten nicht physisch gelöscht.',
  physical_profile_not_enabled: 'Das isolierte lokale Löschprofil ist nicht aktiviert.',
  legal_hold: 'Ein aktiver Hold sperrt die Löschung.',
  retained_current_or_historical_reference: 'Aktuelle, historische oder eingefrorene Bindungen bleiben erhalten.',
  already_deleted: 'Ein abgeschlossener lokaler Löschbeleg liegt vor.',
  explicit_scoped_authorization_required: 'Explizite menschliche Freigabe einschließlich Kopienprüfung fehlt.',
}

export function RetentionWorkspace({ actor, revision, mutate, openDialog }: WorkspaceProps) {
  const [localRevision, setLocalRevision] = useState(0)
  const [product, setProduct] = useState(actor.products[0])
  const [selected, setSelected] = useState('')
  const currentRevision = revision + localRevision
  const records = useData<RetentionRecord[]>('/retention', currentRevision)
  const lifecycles = useData<ProductLifecycle[]>('/product-lifecycles', currentRevision)
  const evidence = useData<Evidence[]>('/evidence', currentRevision)
  const policy = useData<{ policyVersion: string; localPhysicalProfile: boolean }>('/retention/policy', currentRevision)
  const submit = async (path: string, body: unknown) => { try { await mutate(path, body) } finally { setLocalRevision(r => r + 1) } }
  const error = records.error || lifecycles.error || evidence.error || policy.error
  if (error) return <ErrorBanner message={error} />
  if (!records.data || !lifecycles.data || !evidence.data || !policy.data) return <Loading />
  const lifecycle = lifecycles.data.find(p => p.productId === product)
  const documents = evidence.data.filter(e => e.productId === product && e.synthetic && e.approved &&
    !records.data!.some(r => r.reference === `${e.id}@${e.version}`))
  const quality = actor.roles.includes('QualityManager')
  const changeLifecycle = () => openDialog({ title: 'Synthetischen Lifecycle dokumentieren', description: 'Nur bestätigtes letztes Inverkehrbringen bestimmt die Mindestfrist. Fehlende Daten bleiben unbekannt. Dies beendet keine PMS-/PSUR-Pflichten.',
    fields: [{ name: 'lastPlacingOnMarketLocalDate', label: 'Letztes Inverkehrbringen (Kalendertag, falls bekannt)', type: 'date', required: false, value: lifecycle?.lastPlacingOnMarketLocalDate ?? '' },
      { name: 'confirmed', label: 'Durch synthetischen Beleg bestätigt', type: 'select', value: String(lifecycle?.confirmed ?? false), options: [{ value: 'false', label: 'Nicht bestätigt / unbekannt' }, { value: 'true', label: 'Ausdrücklich bestätigt' }] },
      { name: 'sourceReference', label: 'Synthetischer Herkunftsbeleg (DEMO-...)', value: lifecycle?.sourceReference ?? 'DEMO-LAST-PLACING-', minLength: 10 }, { ...reason, minLength: 40 }],
    onSubmit: v => submit('/product-lifecycles', { ...v, productId: product, expectedVersion: lifecycle?.version ?? 0,
      lastPlacingOnMarketLocalDate: v.lastPlacingOnMarketLocalDate || null, confirmed: v.confirmed === 'true' }) })
  const createRaw = () => openDialog({ title: 'Isoliertes Rohdaten-Testobjekt anlegen', description: 'Der Server erzeugt ausschließlich synthetische Testbytes in der Rohquarantäne. Sieben volle Tage sind ein explizites Testprofil, keine allgemeine Aufbewahrungsregel.',
    fields: [{ name: 'reference', label: 'Testreferenz (DEMO-RAW-...)', value: `DEMO-RAW-${Date.now()}` },
      { name: 'syntheticReceivedAt', label: 'Expliziter simulierter Empfang (ISO-Zeit mit Zeitzone)', value: new Date().toISOString() }, { ...reason, minLength: 40 }],
    onSubmit: v => submit('/retention/raw', { ...v, productId: product }) })
  const track = () => openDialog({ title: 'Demo-Evidenz kontrolliert klassifizieren', description: 'Synthetisches Art.-10(8)-Mindestfristbeispiel für Implantatdokumentation. Originalbytes bleiben erhalten. Kein produktiver WORM-Lock und keine Löschfreigabe.',
    fields: [{ name: 'source', label: 'Konkrete genehmigte Dokumentversion', type: 'select', options: documents.map(e => ({ value: `${e.id}@${e.version}`, label: `${e.title} · v${e.version}` })) }, { ...reason, minLength: 40 }],
    onSubmit: v => { const document = documents.find(e => `${e.id}@${e.version}` === v.source); if (!document) throw new Error('Die ausgewählte Evidenzversion fehlt.'); return submit('/retention/evidence', { evidenceId: document.id, evidenceVersion: document.version, reason: v.reason }) } })
  return <div className="stack"><section className="panel"><SectionHead eyebrow="DATENKLASSE VOR FRIST" title="Aufbewahren ist nicht freigeben." /><p>Regulatorische Mindestfrist, Legal Hold, aktuelle und historische Bindungen sowie tatsächliche Speicherbereinigung werden getrennt geprüft. Ein Fristablauf schließt keinen PSUR und löscht keine regulatorische Evidenz.</p>
    <Badge tone={policy.data.localPhysicalProfile ? 'amber' : 'neutral'}>{policy.data.localPhysicalProfile ? 'Explizites synthetisches Lokalprofil' : 'Physische Löschung deaktiviert'}</Badge><p className="muted">{policy.data.policyVersion} · Keine produktive WORM-Dauer aktiviert.</p>
    <label className="field compact">Produktfamilie<select value={product} onChange={e => { setProduct(e.target.value); setSelected('') }}>{actor.products.map(p => <option key={p} value={p}>{p}</option>)}</select></label>
    <h3>Bestätigter Lifecycle</h3><p>{lifecycle?.confirmed ? `Letztes Inverkehrbringen: ${date(lifecycle.lastPlacingOnMarketLocalDate)} · v${lifecycle.version} · ${lifecycle.timeZone}` : 'Unbekannt oder unbestätigt. Upload und Systemeingang sind kein Ersatz.'}</p>
    {lifecycle && <><p>{lifecycle.sourceReference} · {lifecycle.rationale}</p><small>Erfasst {date(lifecycle.recordedAt, true)} · {lifecycle.ruleVersion}</small></>}
    {quality && <div className="row"><button className="button secondary" onClick={changeLifecycle}>Lifecycle versionieren</button><button className="button secondary" disabled={!documents.length} onClick={track}>Dokument klassifizieren</button><button className="button secondary" disabled={!policy.data.localPhysicalProfile} onClick={createRaw}>Testrohobjekt anlegen</button></div>}
  </section><section className="panel"><SectionHead title="Objektbindungen und Löschbelege"><button className="text-button" onClick={() => setLocalRevision(r => r + 1)}><RefreshCw size={16} />Neu abgleichen</button></SectionHead>
    {!records.data.some(r => r.productId === product) ? <Empty title="Keine klassifizierten Objekte">Bestehende Dokumente werden nicht automatisch zu löschbaren Rohdaten umklassifiziert.</Empty> : records.data.filter(r => r.productId === product).map(r =>
      <button className="priority-row" key={r.id} onClick={() => setSelected(r.id)}><div><b>{r.reference}</b><small>{r.object.dataClass} · v{r.version}</small></div><Badge tone={r.status === 'DeletionFailed' ? 'danger' : r.legalHold ? 'amber' : 'neutral'}>{retentionNames[r.status] ?? r.status}</Badge><ArrowUpRight size={16} /></button>)}
  </section>{selected && <RetentionDetail key={selected} id={selected} actor={actor} revision={currentRevision} mutate={submit} openDialog={openDialog} />}</div>
}

function RetentionDetail({ id, actor, revision, mutate, openDialog }: WorkspaceProps & { id: string }) {
  const { data, error } = useData<RetentionView>(`/retention/${encodeURIComponent(id)}`, revision)
  if (error) return <ErrorBanner message={error} />
  if (!data) return <Loading />
  const { record: r, evaluation: e } = data
  const quality = actor.roles.includes('QualityManager')
  const hold = () => openDialog({ title: r.legalHold ? 'Hold ausdrücklich aufheben' : 'Hold setzen', description: 'Jede Hold-Änderung verwirft eine vorhandene Löschfreigabe. Begonnene physische Schritte werden nicht durch einen wirkungslosen nachträglichen Hold verdeckt.',
    fields: [{ ...reason, minLength: 40 }], onSubmit: v => mutate(`/retention/${id}/hold`, { ...v, expectedVersion: r.version, legalHold: !r.legalHold }) })
  const authorize = () => openDialog({ title: 'Synthetische Löschung autorisieren', description: 'Nur nach nachvollziehbarer Prüfung von Backups, Exporten, Caches und Agentenspeichern. Die Freigabe bindet exakt Datenklasse, Objekt, Policy und Zeitbasis. SQL prüft vor der Ausführung alle Bindungen erneut.',
    fields: [{ name: 'externalCopiesReviewed', label: 'Kopien geprüft; keine ungeklärten oder unberücksichtigten Kopien', type: 'select', value: 'false', options: [{ value: 'false', label: 'Nicht bestätigt / noch ungeklärt' }, { value: 'true', label: 'Ja, im Löschplan vollständig berücksichtigt' }] },
      { name: 'externalCopiesReview', label: 'Prüfumfang und Ergebnis einschließlich unbekannter externer Kopien', type: 'textarea', minLength: 60 }, { ...reason, minLength: 40 }],
    onSubmit: v => mutate(`/retention/${id}/authorize-deletion`, { ...v, expectedVersion: r.version, externalCopiesReviewed: v.externalCopiesReviewed === 'true' }) })
  const remove = () => openDialog({ title: r.status === 'DeletionFailed' || r.status === 'Deleting' ? 'Teilvorgang kontrolliert abgleichen' : 'Testrohbytes physisch löschen', description: `Nur ${r.reference}; kein Löschen regulatorischer Evidenz. Ein Speicherfehler bleibt ein offener Teilvorgang und wird nicht als Erfolg ausgegeben.`,
    fields: [{ name: 'confirmReference', label: `Exakte Testreferenz zur Bestätigung: ${r.reference}` }, { ...reason, minLength: 40 }],
    onSubmit: v => { if (v.confirmReference !== r.reference) throw new Error('Die Bestätigung stimmt nicht mit der ausgewählten Testreferenz überein.'); return mutate(`/retention/${id}/delete`, { expectedVersion: r.version, reason: v.reason }) } })
  return <section className="panel"><SectionHead eyebrow="VERSIONIERTER OBJEKTNACHWEIS" title={r.reference} /><Badge tone={r.errorCode ? 'danger' : 'neutral'}>{retentionNames[r.status] ?? r.status}</Badge>
    {r.errorCode && <ErrorBanner message={`Offener Teilvorgang: ${r.errorCode}. Kein vollständiger Löschabschluss bestätigt.`} />}
    <p className="mono">{r.object.objectKey}</p><p>{r.object.dataClass} · {r.object.contract} · v{r.version}</p>
    <div className="deadline-dates"><span><small>EINGANG / SIMULIERTER EMPFANG</small><b>{date(r.receivedAt, true, 'UTC')} UTC</b></span><span><small>MINDESTENS AUFBEWAHREN BIS</small><b>{e.minimumRetainThroughLocalDate ? `${date(e.minimumRetainThroughLocalDate)} einschließlich` : r.object.dataClass === 'UnreferencedSyntheticRawQuarantine' ? `${date(e.expiresAt, true, 'UTC')} UTC` : 'Nicht bestimmbar'}</b></span></div>
    <p>Mindestintervall: {e.minimumIntervalElapsed === null ? 'unbekannt' : e.minimumIntervalElapsed ? 'abgelaufen, keine automatische Freigabe' : 'nicht abgelaufen'} · {e.retainedReferences} fortbestehende Bindungen · {r.attempts} Ausführungsversuche</p>
    {e.blockers.map(b => <p key={b}><Badge tone="amber">{retentionNames[b] ?? b}</Badge></p>)}
    {quality && <div className="row"><button className="button secondary" disabled={['Deleting', 'DeletionFailed', 'Deleted'].includes(r.status)} onClick={hold}>{r.legalHold ? 'Hold aufheben' : 'Hold setzen'}</button><button className="button secondary" disabled={!e.eligibleForAuthorization || ['Deleting', 'DeletionFailed', 'Deleted'].includes(r.status)} onClick={authorize}>Löschung autorisieren</button><button className="button secondary" disabled={!e.canDeleteNow} onClick={remove}>{['Deleting', 'DeletionFailed'].includes(r.status) ? 'Teilvorgang abgleichen' : 'Testbytes physisch löschen'}</button></div>}
    <details><summary>Regel, Berechnung und gebundene Herkunft</summary>{e.calculation.map((line, index) => <p key={index}>{line}</p>)}<p className="mono">Policy {e.policyVersion} · Scope {e.scopeHash}</p>{e.lifecycleSource && <p className="mono">{e.lifecycleSource.id}@{e.lifecycleSource.version} · {e.lifecycleSource.hash}</p>}</details>
    {!!data.references.length && <details><summary>Berechtigte aktuelle und historische Referenzen</summary>{data.references.map(ref => <p key={`${ref.kind}-${ref.id}-${ref.version}`}><b>{ref.kind} · {ref.id}@{ref.version}</b><small className="mono">{ref.versionHash}</small></p>)}</details>}
    {r.authorization && <details><summary>Menschliche Löschfreigabe</summary><p>{r.authorization.actorId} · {date(r.authorization.at, true)}</p><p>{r.authorization.rationale}</p><p>{r.authorization.externalCopiesReview}</p><p className="mono">{r.authorization.scopeHash}</p></details>}
    {r.deletion && <details open><summary>Abgeschlossener lokaler Löschbeleg</summary><p>Aktive Objektdatei entfernt und SQL-Suchprojektion bereinigt; kein Nachweis physischer Datenträgerüberschreibung oder produktiver Backup-Löschung.</p><p>{r.deletion.contract} · {r.deletion.state} · {date(r.deletion.at, true)}</p><p className="mono">Vorgang {r.deletion.operationId} · Objekt {r.deletion.hash}</p></details>}
  </section>
}

export function SourcesWorkspace({ actor, revision, mutate, openDialog }: WorkspaceProps) {
  const { data, error } = useData<SourceRun[]>('/sources', revision)
  const [localError, setLocalError] = useState('')
  const [busy, setBusy] = useState('')
  const [productId, setProductId] = useState(actor.products[0])
  async function sync(source: string) {
    setBusy(source); setLocalError('')
    try { await mutate(`/sources/${source}/sync`, { productId }) } catch (e) { setLocalError(e instanceof Error ? e.message : 'Quelle nicht aktualisiert.') } finally { setBusy('') }
  }
  return <section className="panel sources-panel"><SectionHead eyebrow="ÖFFENTLICHE VERGLEICHSQUELLEN" title="Reale Quellen. Nur lesend." /><p className="muted">Ein externer Datensatz ist kein Vorfall des fiktiven Herstellers. MAUDE-Zählungen sind keine Inzidenz. Nur Metadaten, keine Patientenberichte oder ungeprüften Volltexte.</p>{(error || localError) && <ErrorBanner message={error || localError} />}
    <label className="field compact">Suchprofil / Vergleichsprodukt<select value={productId} onChange={e => setProductId(e.target.value)}>{actor.products.map(p => <option key={p} value={p}>{p === 'ortho' ? 'OrthoLink' : 'CardioFlow'}</option>)}</select></label>
    <div className="source-connectors">{[{ id: 'fda', title: 'FDA / MAUDE', note: 'openFDA Device Event · limitierte Produktmetadaten', url: 'https://open.fda.gov/apis/device/event/' }, { id: 'pubmed', title: 'PubMed', note: 'NCBI E-utilities · Literaturmetadaten, keine Volltextlizenz', url: 'https://www.ncbi.nlm.nih.gov/books/NBK25497/' }].map(s => <div key={s.id}><span className="source-symbol"><Link2 /></span><h3>{s.title}</h3><p>{s.note}</p><a className="text-button" href={s.url} target="_blank" rel="noreferrer">Quellenvertrag <ArrowUpRight size={15} /></a>{actor.roles.includes('PmsSpecialist') && <button className="button secondary" disabled={!!busy} onClick={() => void sync(s.id)}><RefreshCw size={16} className={busy === s.id ? 'spin' : ''} />{busy === s.id ? 'Leseabruf läuft…' : 'Öffentlich lesen'}</button>}</div>)}</div>
    {data?.map(run => <article className="source-run" key={run.id}><div className="row-between"><h3>{run.source} · {run.productId} · {date(run.at, true)}</h3><Badge tone={run.status === 'Failed' ? 'danger' : 'blue'}>{run.status} · {run.items.length} Metadaten</Badge></div>{run.error && <ErrorBanner message={run.error} />}{run.items.map(item => <div className="history-entry" key={item.externalId}><a className="public-source" href={item.url} target="_blank" rel="noreferrer"><span><b>{item.title}</b><small>{item.externalId} · öffentliche Vergleichsquelle</small></span><ArrowUpRight size={16} /></a><p>{item.summary}</p><p className="mono">{hash(item.provenanceHash)}</p>{actor.roles.includes('MedicalSafetyReviewer') && <button className="button secondary" onClick={() => openDialog({ title: 'Öffentliche Vergleichsquelle beurteilen', description: 'Nur Metadaten: klinische Übertragbarkeit, Produktabgrenzung und Quellenrechte begründen. Keine Volltext- oder Kausalitätsfreigabe.', fields: [{ ...reason, minLength: 40 }], onSubmit: values => mutate(`/sources/${run.id}/approve`, { ...values, expectedVersion: run.version, externalId: item.externalId }) })}>Als Vergleichsevidenz einordnen</button>}</div>)}</article>)}</section>
}

export function EvidenceWorkspace({ revision, actor, openDialog, mutate }: WorkspaceProps) {
  const { data, error } = useData<Evidence[]>('/evidence', revision)
  if (error) return <ErrorBanner message={error} />
  if (!data) return <Loading />
  return <div className="evidence-library">{data.map(e => <EvidenceCard key={`${e.id}-${e.version}`} current={e} actor={actor} openDialog={openDialog} mutate={mutate} />)}</div>
}
function EvidenceCard({ current, actor, openDialog, mutate }: Omit<WorkspaceProps, 'revision'> & { current: Evidence }) {
  const [versions, setVersions] = useState<Evidence[]>([current])
  const [version, setVersion] = useState(current.version)
  const [error, setError] = useState('')
  const e = versions.find(item => item.version === version) ?? current
  async function revise() {
    try {
      const changes = (await api<ChangeRequest[]>('/changes')).filter(c => c.documentId === current.id && c.documentVersion === current.version && c.status === 'Approved')
      if (!changes.length) throw new Error('Zuerst einen versionsgebundenen Änderungsauftrag unter Trends & CAPA genehmigen.')
      openDialog({ title: 'Kontrollierte Dokumentversion veröffentlichen', fields: [
        { name: 'changeId', label: 'Genehmigter Änderungsauftrag', type: 'select', options: changes.map(c => ({ value: c.id, label: c.proposedChange })) },
        { name: 'title', label: 'Revisionstitel', value: current.title, minLength: 10 },
        { name: 'content', label: 'Vollständiger synthetischer Folgeinhalt', type: 'textarea', value: current.content, minLength: 50 },
        { name: 'validFrom', label: 'Fachlich gültig ab (lokale Eingabezeit)', type: 'datetime-local', value: new Date(Date.now() - new Date().getTimezoneOffset() * 60000).toISOString().slice(0, 16) }, { ...reason, minLength: 30 }
      ], onSubmit: v => mutate(`/evidence/${current.id}/revise`, { ...v, expectedVersion: current.version, validFrom: new Date(v.validFrom).toISOString() }) })
    } catch (error) { setError(error instanceof Error ? error.message : 'Änderungsauftrag konnte nicht geprüft werden.') }
  }
  return <article className="panel"><div className="row-between"><span className="document-icon"><FileCheck2 size={24} /></span><Badge>{e.kind}</Badge></div><h3>{e.title}</h3><p className="muted">{e.productId} · {e.locator}</p><blockquote>{e.content}</blockquote><div className="row-between"><Badge tone="blue">{e.synthetic ? 'Synthetisches Dokument' : 'Öffentliche Evidenz'}</Badge><span>v{e.version}</span></div><p className="mono">{hash(e.hash)}</p><small className="muted">Fachliche Gültigkeit ab {date(e.validFrom)} · Erfasst {date(e.recordedAt)}</small>{error && <ErrorBanner message={error} />}
    <div className="row"><button className="button secondary" onClick={() => api<Evidence[]>(`/evidence/${e.id}/versions`).then(setVersions).catch(e => setError(e.message))}>Versionshistorie laden</button>{current.synthetic && actor.roles.includes('QualityManager') && <button className="button secondary" onClick={() => void revise()}>Genehmigte Folgeversion</button>}</div>{versions.length > 1 && <label className="field">Unveränderliche Revision<select value={version} onChange={event => setVersion(Number(event.target.value))}>{versions.map(v => <option key={v.version} value={v.version}>v{v.version} · gültig ab {date(v.validFrom)}</option>)}</select></label>}
  </article>
}

export function FeedbackWorkspace({ actor, cases, revision, openDialog, mutate }: WorkspaceProps & { cases: CaseSummary[] }) {
  const trends = useData<Trend[]>('/trends', revision)
  const capas = useData<Capa[]>('/capas', revision)
  const changes = useData<ChangeRequest[]>('/changes', revision)
  const canWrite = actor.roles.some(r => ['PmsSpecialist', 'MedicalSafetyReviewer', 'QualityManager'].includes(r))
  return <div className="stack">
    {(trends.error || capas.error || changes.error) && <ErrorBanner message={trends.error || capas.error || changes.error} />}
    <section className="panel"><SectionHead eyebrow="NENNER VOR NARRATIV" title="Deterministische Trends">{canWrite && <button className="button primary" onClick={() => openDialog({ title: 'Trend mit passendem Nenner berechnen', fields: [{ name: 'productId', label: 'Produktfamilie', type: 'select', options: [{ value: 'ortho', label: 'OrthoLink Modular Hip' }, { value: 'cardio', label: 'CardioFlow Valve' }] }], submitLabel: 'Statistik berechnen', description: 'Festes synthetisches PMS-Plan-Profil: 15.11.2025–14.11.2026, Worldwide, implantierte Einheiten. Keine öffentliche MAUDE-Inzidenz.', onSubmit: v => mutate('/trends', { productId: v.productId, region: 'Worldwide', start: '2025-11-15', end: '2026-11-14', exposureId: `exposure-${v.productId}-2026-Worldwide` }) })}><Activity size={17} />Trend berechnen</button>}</SectionHead>
      {!trends.data?.length && <Empty title="Noch keine Trendberechnung">Ereignisse werden dedupliziert. Produkt, Region, Einheit und Zeitraum des Nenners müssen übereinstimmen.</Empty>}
      {trends.data?.map(t => <article className="trend-card" key={t.id}><div className="row-between"><h3>{t.productId === 'ortho' ? 'OrthoLink' : 'CardioFlow'} · {t.input.region}</h3><Badge tone={t.result.signalCandidate ? 'amber' : 'blue'}>{t.result.signalCandidate ? 'Signalkandidat – kein bestätigter Trend' : label(t.status)}</Badge></div><div className="trend-values"><div><small>EINDEUTIGE EREIGNISSE</small><strong>{t.result.events}</strong></div><div><small>PASSENDER NENNER</small><strong>{t.result.exposure.toLocaleString('de-DE')}</strong></div><div><small>RATE / 1.000 IMPLANTATE</small><strong>{t.result.ratePerThousand.toLocaleString('de-DE', { maximumFractionDigits: 3 })}</strong></div><div><small>POISSON · EINSEITIG</small><strong>p = {t.result.upperTailProbability.toFixed(4)}</strong></div></div><p>{t.interpretation}</p><details><summary>Methode, Annahmen und Grenzen</summary>{t.result.limitations.map(l => <p key={l}>{l}</p>)}<p>Periode {date(t.input.start)} bis {date(t.input.end)}. Ein LLM berechnet keine dieser Kennzahlen.</p></details>{canWrite && t.status === 'Candidate' && <button className="button secondary" onClick={() => openDialog({ title: 'Trend fachlich beurteilen', fields: [reason], onSubmit: values => mutate(`/trends/${t.id}/review`, { ...values, expectedVersion: t.version }) })}>Begründeten Human Review speichern</button>}</article>)}
    </section>
    <section className="panel"><SectionHead eyebrow="MASSNAHMEN MIT WIRKSAMKEITSNACHWEIS" title="CAPA & Untersuchungen">{canWrite && <button className="button secondary" onClick={() => openDialog({ title: 'CAPA mit Prüfkriterium eröffnen', fields: [
      { name: 'caseId', label: 'Ursprungsfall', type: 'select', options: cases.map(c => ({ value: c.id, label: `${c.reference} / ${c.title}` })) },
      { name: 'title', label: 'Maßnahme', value: 'Beweiserhaltende Untersuchung und Prozessprüfung' },
      { name: 'action', label: 'Konkrete Handlung', type: 'textarea', minLength: 20, value: 'Dokumentierte nichtdestruktive Untersuchung des synthetischen Explantats und Abgleich der Montage- und Prüfrandbedingungen.' },
      { name: 'rationale', label: 'Medizinisch-technische Begründung', type: 'textarea', minLength: 30 },
      { name: 'effectivenessCriterion', label: 'Messbares Wirksamkeitskriterium', type: 'textarea', minLength: 20, value: 'Unabhängiger QM-Review bestätigt vollständige Befunddokumentation und nachweislich behobene Prozessabweichung anhand von drei Folgeprüfungen.' },
    ], onSubmit: v => mutate('/capas', { ...v, productId: cases.find(c => c.id === v.caseId)?.productId, ownerId: actor.id, dueAt: new Date(Date.now() + 14 * 86400000).toISOString() }) })}><Plus size={17} />CAPA eröffnen</button>}</SectionHead>
      {!capas.data?.length && <Empty title="Noch keine Maßnahmen">Ein angelegtes CAPA ist kein abgeschlossener Wirksamkeitsnachweis.</Empty>}
      {capas.data?.map(c => <article className="capa-card" key={c.id}><div className="row-between"><h3>{c.title}</h3><Badge tone={c.status === 'Verified' ? 'blue' : 'amber'}>{label(c.status)}</Badge></div><p>{c.action}</p><p><b>Begründung:</b> {c.rationale}</p><div className="callout"><b>Wirksamkeitskriterium</b><p>{c.effectivenessCriterion}</p></div><p className="muted">Interne Frist {date(c.dueAt)} · Version {c.version}</p>{actor.roles.includes('QualityManager') && c.status !== 'Verified' && <button className="button secondary" onClick={() => openDialog({ title: 'CAPA-Wirksamkeit unabhängig prüfen', fields: [{ name: 'evidence', label: 'Konkreter Ergebnis-/Wirksamkeitsnachweis', type: 'textarea', minLength: 40 }, { name: 'effective', label: 'Ergebnis', type: 'select', options: [{ value: 'true', label: 'Wirksamkeit nachgewiesen' }, { value: 'false', label: 'Nicht wirksam – weitere Bearbeitung' }] }, reason], onSubmit: v => mutate(`/capas/${c.id}/verify`, { ...v, effective: v.effective === 'true', expectedVersion: c.version }) })}><CheckCheck size={17} />Wirksamkeit bewerten</button>}{c.evidence && <p><b>Prüfnachweis:</b> {c.evidence}</p>}</article>)}
    </section>
    <section className="panel"><SectionHead eyebrow="KONTROLLIERTE DOKUMENTE BLEIBEN UNVERÄNDERT" title="Änderungsaufträge an RM / PMCF / SSCP">{canWrite && <button className="button secondary" onClick={() => openDialog({ title: 'Kontrollierten Änderungsauftrag erstellen', fields: [
      { name: 'caseId', label: 'Ursprungsfall', type: 'select', options: cases.map(c => ({ value: c.id, label: c.reference })) },
      { name: 'kind', label: 'Zieldokument', type: 'select', options: [{ value: 'risk-v4', label: 'Risikodatei' }, { value: 'pmcf-v1', label: 'PMCF-Plan' }, { value: 'sscp-v1', label: 'SSCP' }] },
      { name: 'proposedChange', label: 'Konkreter Änderungsvorschlag', type: 'textarea', minLength: 30 }, { name: 'rationale', label: 'Evidenzbasierte Begründung', type: 'textarea', minLength: 30 }
    ], onSubmit: v => { const productId = cases.find(c => c.id === v.caseId)?.productId; const suffix = productId === 'cardio' && v.kind === 'risk-v4' ? 'risk-v3' : v.kind; return mutate('/changes', { ...v, productId, documentId: `${productId}-${suffix}`, documentVersion: 1 }) } })}><FileText size={17} />Änderung anfordern</button>}</SectionHead>
      {!changes.data?.length && <Empty title="Keine offenen Änderungsaufträge">Agenten und Maßnahmen überschreiben keine freigegebenen Produktunterlagen.</Empty>}
      {changes.data?.map(c => <article className="history-entry" key={c.id}><div className="row-between"><h3>{c.documentId} · v{c.documentVersion}</h3><Badge>{label(c.status)}</Badge></div><p>{c.proposedChange}</p><p className="muted">{c.rationale}</p>{actor.roles.includes('QualityManager') && c.status === 'Proposed' && <button className="button secondary" onClick={() => openDialog({ title: 'Dokumentänderung kontrolliert genehmigen', fields: [reason], onSubmit: v => mutate(`/changes/${c.id}/approve`, { ...v, expectedVersion: c.version }) })}>Änderungsauftrag genehmigen</button>}</article>)}
    </section>
  </div>
}

export function PsurWorkspace({ actor, revision, openDialog, mutate }: WorkspaceProps) {
  const { data, error } = useData<Psur[]>('/psurs', revision)
  const profiles = useData<ProductGovernance[]>('/products/profiles', revision)
  const groups = useData<PsurGroupDefinition[]>('/psur-groups', revision)
  const [selected, setSelected] = useState('')
  const [productId, setProductId] = useState(actor.products[0] ?? '')
  const [groupId, setGroupId] = useState('')
  const [memberProductId, setMemberProductId] = useState('')
  const [localError, setLocalError] = useState('')
  const [exporting, setExporting] = useState('')
  const productReports = (data ?? []).filter(p => p.productId === productId)
  const availableGroups = [...(groups.data ?? []).filter(g => g.productId === productId),
    ...productReports.flatMap(p => p.snapshot.group ? [p.snapshot.group.definition] : [])]
    .filter((g, i, all) => all.findIndex(other => other.id === g.id) === i)
  const group = availableGroups.find(g => g.id === groupId)
  const currentGroup = groups.data?.find(g => g.id === groupId)
  const reports = productReports.filter(p => groupId ? p.snapshot.group?.definition.id === groupId : !p.snapshot.group)
    .sort((a, b) => (b.snapshot.series?.reportVersion ?? 0) - (a.snapshot.series?.reportVersion ?? 0))
  const psur = reports.find(p => p.id === selected) ?? reports[0]
  const latest = reports[0]
  const profile = profiles.data?.find(p => p.productId === productId)
  const legacySeries = productReports.some(p => !p.snapshot.series)
  const canWrite = actor.roles.some(r => ['PmsSpecialist', 'MedicalSafetyReviewer'].includes(r))
  const families = psur?.snapshot.group?.members.map(m => m.snapshot) ?? (psur ? [psur.snapshot] : [])
  const member = psur?.snapshot.group?.members.find(m => m.product.productId === memberProductId) ??
    psur?.snapshot.group?.members.find(m => m.product.productId === psur.productId)
  const family = member?.snapshot ?? psur?.snapshot
  const frozenProfile = member?.governance ?? psur?.snapshot.series?.governance
  const canFreeze = !!data && !!profiles.data && !!groups.data && !!profile && !legacySeries && (!groupId || !!currentGroup)
  const blocked = psur && (!psur.snapshot.series || families.some(s => !s.sourceManifest || !s.actions || !s.directory) || psur.findings.some(f => ['Critical', 'Major'].includes(f.severity)))
  const canApprove = psur && !blocked && !psur.approvals.some(a => a.actorId === actor.id) &&
    (psur.status === 'Draft' && actor.roles.includes('MedicalSafetyReviewer') || psur.status === 'MedicalApproved' && actor.roles.includes('RegulatoryAffairs'))
  function recordGroup(existing?: PsurGroupDefinition) {
    if (!profile || !profiles.data) return
    const eligible = profiles.data.filter(p => actor.products.includes(p.productId)).sort((a, b) => a.productId.localeCompare(b.productId))
    const membershipOptions = [{ value: productId, label: `Nur Leitprodukt ${productId} / ${profile.device.basicUdi}` },
      ...(eligible.length > 1 ? [{ value: eligible.map(p => p.productId).join(','), label: eligible.map(p => `${p.productId} / ${p.device.basicUdi}`).join(' + ') }] : [])]
    openDialog({ title: existing ? 'Mitgliedschaft kontrolliert neu versionieren' : 'Synthetische PSUR-Gruppe begründen',
      description: `Festes Leitprodukt ${productId}. Gleiche verantwortliche Benannte Stelle, höchste Risikoklasse und Leitprodukt-Zeitplan werden serverseitig geprüft. Keine Eignungsaussage zu realen Produktkombinationen. Alte Berichte und Pflichten bleiben bestehen.`,
      fields: [
        existing ? { name: 'reference', label: 'Unveränderliche Serienreferenz', type: 'select', options: [{ value: existing.reference, label: existing.reference }] } :
          { name: 'reference', label: 'Neue eindeutige DEMO-PSUR-GROUP-Referenz', type: 'text', value: `DEMO-PSUR-GROUP-${productId.toUpperCase()}-${Date.now()}`, minLength: 17 },
        { name: 'memberProductIds', label: 'Explizite Mitgliedschaft (inklusive Leitprodukt)', type: 'select', options: membershipOptions, value: existing?.members.map(m => m.productId).sort().join(',') ?? eligible.map(p => p.productId).join(',') },
        { name: 'firstPeriodStart', label: 'Fester erster Periodenbeginn im Leitprodukt-Zeitplan', type: 'date', value: existing?.firstPeriodStart ?? profile.firstPeriodStart },
        { name: 'rationale', label: 'Fachliche Begründung der synthetischen Gruppierung / Änderung', type: 'textarea', minLength: 80, value: existing?.rationale ?? '' },
        { name: 'scheduleBasis', label: 'Leitprodukt-Zeitplan und fortbestehende Altserienpflichten', type: 'textarea', minLength: 40, value: existing?.scheduleBasis ?? '' }
      ], submitLabel: 'Definition mit aktuellen Profilversionen binden',
      onSubmit: v => mutate('/psur-groups', { ...v, expectedVersion: existing?.version ?? 0, leadProductId: productId, memberProductIds: v.memberProductIds.split(',') }) })
  }
  function freeze(refreeze: boolean) {
    if (!profile || !canFreeze) { setLocalError('Aktueller berechtigter Produkt-/Gruppenstand fehlt; keine automatische Rücknummerierung.'); return }
    const start = latest ? refreeze ? latest.snapshot.start : new Date(Date.parse(`${latest.snapshot.end}T00:00:00Z`) + 86400000).toISOString().slice(0, 10) : currentGroup?.firstPeriodStart ?? profile.firstPeriodStart
    const [year, month, day] = start.split('-').map(Number)
    const end = latest && refreeze ? latest.snapshot.end : new Date(Date.UTC(year + 1, month - 1, Math.min(day, new Date(Date.UTC(year + 1, month, 0)).getUTCDate()) - 1)).toISOString().slice(0, 10)
    const fields: Field[] = [
      ...(latest ? [{ name: 'previousReportId', label: 'Ausdrücklich gebundene aktuelle Vorfassung', type: 'select' as const, options: [{ value: latest.id, label: `${latest.snapshot.series!.reference} / Nr. ${latest.snapshot.series!.reportVersion} / SQL v${latest.version} / ${label(latest.status)}` }] }] : []),
      { name: 'start', label: 'Berichtsperiode ab', type: 'date', value: start }, { name: 'end', label: 'Berichtsperiode bis einschließlich', type: 'date', value: end },
      { name: 'region', label: 'Narrativer Fokus (Worldwide enthält die Teilregion)', type: 'select', options: [{ value: 'Worldwide', label: 'Worldwide' }, { value: 'EEA-Turkey-NI', label: 'EWR / Türkei / Nordirland' }] },
      { name: 'submissionDate', label: 'Gesondert vereinbarter Einreichungstag', type: 'date', value: latest ? refreeze ? latest.snapshot.series!.submissionDate : '' : start === profile.firstPeriodStart ? profile.firstSubmissionDate : '' },
      { name: 'scheduleAgreement', label: 'Nachweis der Terminvereinbarung (keine automatische Tagesfrist)', type: 'textarea', minLength: 12, value: latest ? refreeze ? latest.snapshot.series!.scheduleAgreement : '' : start === profile.firstPeriodStart ? profile.scheduleAgreement : '' },
      { name: 'mode', label: 'Expliziter Agentenmodus', type: 'select', options: [{ value: 'Replay', label: 'Reproduzierbarer Demo-/Replay-Entwurf' }, { value: 'Live', label: 'Live Foundry – nur bei freigegebener Konfiguration' }, { value: 'Failure', label: 'Ausfallszenario – kein Ersatzentwurf' }] },
      ...(latest ? [{ name: 'revisionReason', label: 'Begründung der neuen eigenständigen Fassung', type: 'textarea' as const, minLength: 40 }] : [])
    ]
    openDialog({ title: latest ? refreeze ? 'Neue Fassung derselben PSUR-Periode' : 'Lückenloser PSUR-Folgezyklus' : 'Ersten PSUR-Snapshot einfrieren',
      description: `${currentGroup ? `${currentGroup.reference} / Gruppenversion ${currentGroup.version}; Mitglieder ${currentGroup.members.map(m => m.basicUdi).join(', ')}` : `${profile.psurReference}; einzelne Basic-UDI ${profile.device.basicUdi}`}. Festes Leitprodukt ${productId}, Benannte Stelle ${profile.device.notifiedBodyNumber}. Alle Mitglieder werden separat ausgewertet und gemeinsam gebunden. Alte Snapshots bleiben unverändert.`, fields, submitLabel: 'Neue Fassung und expliziten Modus bestätigen',
      onSubmit: v => mutate('/psurs', { ...v, productId, leadProductId: productId, ...(currentGroup ? { groupId: currentGroup.id, groupVersion: currentGroup.version } : {}), ...(latest ? { previousReportId: v.previousReportId, previousReportVersion: latest.version } : {}) }) })
  }
  async function download(format: 'pdf' | 'json' | 'zip') {
    if (!psur) return
    setExporting(format); setLocalError('')
    try { await downloadApi(`/psurs/${psur.id}/export/${format}?version=${psur.version}`, `${psur.snapshot.series?.reference ?? psur.id}-v${psur.version}.${format}`) }
    catch (e) { setLocalError(e instanceof Error ? e.message : 'Export nicht bestätigt.') }
    finally { setExporting('') }
  }
  return <div className="stack">
    <section className="panel"><SectionHead eyebrow="EINGEFRORENE DATEN. NACHVOLLZIEHBARE SCHLUSSFOLGERUNGEN." title="Periodischer Sicherheitsbericht">
      {canWrite && <div className="row"><button className="button primary" disabled={!canFreeze} onClick={() => freeze(false)}><Plus size={17} />{latest ? 'Nächste PSUR-Periode' : 'PSUR-Zyklus anlegen'}</button>{latest && <button className="button secondary" disabled={!canFreeze} onClick={() => freeze(true)}>Neue Fassung derselben Periode</button>}</div>}
    </SectionHead>
      {(error || profiles.error || groups.error || localError) && <ErrorBanner message={error || profiles.error || groups.error || localError} />}
      <label className="field compact">Produktfamilie / festes Leitprodukt<select value={productId} onChange={e => { setProductId(e.target.value); setSelected(''); setGroupId(''); setMemberProductId(''); setLocalError('') }}>{actor.products.map(id => <option key={id} value={id}>{id === 'ortho' ? 'OrthoLink Modular Hip' : id === 'cardio' ? 'CardioFlow Valve' : id}</option>)}</select></label>
      <label className="field">PSUR-Serie / versionierte Gruppierung<select value={groupId} onChange={e => { setGroupId(e.target.value); setSelected(''); setMemberProductId(''); setLocalError('') }}><option value="">Einzelfamilien-Serie · {profile?.psurReference ?? productId}</option>{availableGroups.map(g => <option key={g.id} value={g.id}>{g.reference} · Definition v{g.version} · {g.members.length} Basic-UDIs</option>)}</select></label>
      {group && <div className="callout"><b>{group.reference} · Definition v{group.version}</b><p>{group.rationale}</p><p>{group.scheduleBasis}</p>{!currentGroup && <p>Nur ein berechtigter historischer Stand ist verfügbar. Keine Erstellung mit einer unbekannten aktuellen Mitgliedschaft.</p>}</div>}
      {canWrite && <div className="row"><button className="button secondary" disabled={!profile || !groups.data} onClick={() => recordGroup()}>Neue synthetische Gruppe</button>{currentGroup && <button className="button secondary" onClick={() => recordGroup(currentGroup)}>Mitgliedschaft neu versionieren</button>}</div>}
      {legacySeries && <ErrorBanner message="Altbericht ohne kontrollierte Serienbindung: lesbar, aber keine stille Rücknummerierung, Reparatur oder Freigabe." />}
      {!data && !error && <Loading />}
      {!!reports.length && <label className="field">Unveränderlicher Snapshot / aktuelle Bearbeitungsfassung<select value={psur?.id} onChange={e => setSelected(e.target.value)}>{reports.map(p => <option key={p.id} value={p.id}>{p.snapshot.series?.reference ?? p.id} / Nr. {p.snapshot.series?.reportVersion ?? '?'} · {date(p.snapshot.end)} · SQL v{p.version} · {label(p.status)}</option>)}</select></label>}
      {data && !reports.length && <Empty title="Noch kein eingefrorener PSUR-Datenstand">Fallversionen aller ausgewiesenen Perioden und Regionen, passende Nenner, Dokumente, Trends und Maßnahmen werden gebunden.</Empty>}
    </section>
    {psur && family && <>
      <div className="review-grid"><article className="panel psur-document"><div className="row-between"><Badge tone={psur.error ? 'danger' : 'amber'}>{psur.mode} · SYNTHETISCH</Badge><Badge>{label(psur.status)}</Badge></div>
        <p className="mono">Snapshot {hash(psur.snapshotHash)}</p>{psur.error && <ErrorBanner message={`Autorenlauf: ${psur.error}. Kein verdeckter Replay-Ersatz.`} />}
        {!psur.draft && <Empty title="Kein Berichtstext vorhanden">Ein eingefrorener Datensatz ist noch kein erfolgreicher Autorenlauf.</Empty>}
        {psur.draft.split('\n').map((line, i) => line.startsWith('# ') ? <h1 key={i}>{line.slice(2)}</h1> : line.startsWith('## ') ? <h2 key={i}>{line.slice(3)}</h2> : <p key={i}>{line}</p>)}
      </article><aside className="panel"><h2>Snapshot & Human Review</h2><p>{date(psur.snapshot.start)} – {date(psur.snapshot.end)}</p><Badge>{psur.snapshot.region}</Badge>
        <p><b>{psur.snapshot.series?.reference ?? 'Serienbindung fehlt'}</b> / Berichtsnummer {psur.snapshot.series?.reportVersion ?? 'unbekannt'}</p>
        <p>Freeze {date(psur.snapshot.frozenAt, true)}. Vereinbarter Termin {date(psur.snapshot.series?.submissionDate)}.</p><p className="muted">{psur.snapshot.series?.scheduleAgreement}</p>
        <div className="snapshot-count"><strong>{families.every(s => s.sourceManifest) ? new Set(families.flatMap(s => s.sourceManifest!.cases.map(c => `${c.reference.id}@${c.reference.version}`))).size : '?'}</strong><span>eindeutige Quellfallversionen aller Mitglieder, Perioden und Regionen</span></div><p>{families.reduce((sum, s) => sum + s.cases.length, 0)} Fälle in den N-Narrativ-/Carry-over-Listen; kein gepoolter Inzidenzzähler.</p>
        <div className="callout">Offene Fragen bleiben sichtbar. Freeze-Zeit ist kein historischer Bearbeitungsnachweis. Die serverseitige Freigabe prüft Originalversionen, Befunde und getrennte Konten erneut.</div>
        <p className="mono">Freigabe-Hash<br />{hash(psur.scopeHash)}</p>{psur.approvals.map(a => <div className="history-entry" key={a.id}><b>{a.actorName} · {a.role}</b><p>{a.rationale}</p><small>{date(a.at, true)}</small></div>)}
        {blocked && <ErrorBanner message="Wesentlicher Befund oder fehlender Snapshot-Nachweis: keine Freigabe. Altstände bleiben unverändert." />}
        {canWrite && psur.status !== 'Authoring' && !psur.notifiedBodyReceipt && <button className="button secondary full" onClick={() => openDialog({ title: 'Konkreten PSUR-Text nachbearbeiten', description: 'Änderungen invalidieren alle Freigaben. Der Daten-Snapshot wird nicht geändert; dafür eine neue Fassung einfrieren.', fields: [{ name: 'draft', label: 'Vollständiger ausführlicher Bericht mit Belegmarkern', type: 'textarea', value: psur.draft, minLength: 300 }, { ...reason, minLength: 30 }], onSubmit: v => mutate(`/psurs/${psur.id}/revise`, { ...v, expectedVersion: psur.version }) })}>Text kontrolliert nachbearbeiten</button>}
        {canApprove && <button className="button primary full" onClick={() => openDialog({ title: 'PSUR-Inhalt und Snapshot prüfen', fields: [{ ...reason, minLength: 40 }], onSubmit: v => mutate(`/psurs/${psur.id}/approve`, { ...v, expectedVersion: psur.version }) })}><ShieldCheck size={17} />Mit eigener Rolle freigeben</button>}
        {actor.roles.includes('RegulatoryAffairs') && psur.status === 'Approved' && !psur.notifiedBodyReceipt && <button className="button primary full" onClick={() => openDialog({ title: 'Antwort der Benannten Stelle simulieren', fields: [{ ...reason, minLength: 40 }], onSubmit: v => mutate(`/psurs/${psur.id}/simulate`, { ...v, expectedVersion: psur.version }) })}>BS-Antwort simulieren</button>}
        {psur.notifiedBodyReceipt && <div className="receipt"><Badge tone="blue">SIMULIERTE BS-ANTWORT</Badge><b>{psur.notifiedBodyReceipt}</b><p>Keine reale Behörden- oder BS-Akzeptanz.</p></div>}
        <h3>Authentifizierter Leseexport</h3><p className="muted">PDF ist eine abgeleitete Lesekopie; ZIP enthält Original-JSON, Snapshot, Audit, Metadaten und Prüfsummen. Keine neue Signatur, Formatannahme oder Übermittlung.</p>
        <div className="row">{(['pdf', 'json', 'zip'] as const).map(format => <button className="button secondary" key={format} disabled={!!exporting} onClick={() => void download(format)}><Download size={15} />{exporting === format ? 'Export…' : format.toUpperCase()}</button>)}</div>
      </aside></div>
      <section className="panel"><SectionHead eyebrow="GEMEINSAM GEBUNDEN · EINZELN AUSWERTBAR" title="Eingefrorene Basic-UDI-Mitgliedschaft" />
        {psur.snapshot.group ? <><p>Definition {psur.snapshot.group.definition.reference}@{psur.snapshot.group.definition.version}; festes Leitprodukt {psur.snapshot.leadProductId}.</p><p>{psur.snapshot.group.definition.rationale}</p><p>{psur.snapshot.group.definition.scheduleBasis}</p><PsurReferences values={[psur.snapshot.group.definitionSource]} />
          {psur.snapshot.group.members.map(m => <div className="history-entry" key={m.product.productId}><b>{m.product.productId === psur.snapshot.leadProductId ? 'Leitprodukt' : 'Mitglied'}: {m.product.productId} / {m.product.basicUdi}</b><p>Klasse {m.product.riskClass} · BS {m.product.notifiedBodyNumber}</p><PsurReferences values={[m.product.governanceSource]} /></div>)}
          <label className="field">Basic-UDI-Einzelauswertung<select value={family.productId} onChange={e => setMemberProductId(e.target.value)}>{psur.snapshot.group.members.map(m => <option key={m.product.productId} value={m.product.productId}>{m.product.productId} / {m.product.basicUdi}</option>)}</select></label>
          <p className="callout">Die folgenden Tabellen und Dokumente zeigen ausschließlich {family.productId}. Berichtstext, Human Reviews und Export binden weiterhin die gesamte eingefrorene Gruppe. Keine Addition zu einer gemeinsamen Inzidenz.</p>
        </> : <p>Einzelfamilie {family.productId} / {frozenProfile?.device.basicUdi ?? 'Profilbindung fehlt'}. Keine nachträgliche Umformung in eine Gruppe.</p>}
      </section>
      <section className="panel"><SectionHead eyebrow={`${family.productId} · N, N2, N3, N4 · KEINE Ratenaddition`.toUpperCase()} title="Zähler, Nenner und Quellfallversionen" /><p className="muted">{family.dateBasis}</p>
        <div className="table-scroll"><table className="case-table"><thead><tr><th>Periode / Region</th><th>Datenstand</th><th>Ereignisse</th><th>Nenner / Einheit</th><th>Rate / 1.000</th><th>Gebundene Belege</th></tr></thead><tbody>{family.periods.flatMap(period => period.regions.map(region => <tr key={`${period.label}-${region.region}`}>
          <td>{period.label} · {region.region}<small className="subline">{date(period.start)} – {date(period.end)}</small></td><td>{region.dataStatus}</td><td>{region.events ?? 'Unbekannt'}</td><td>{region.exposure ?? 'Unbekannt'} / {region.unit ?? 'unbekannt'}<small className="subline">{region.exposureId ?? 'kein Nenner'}@{region.exposureVersion ?? '?'}</small></td><td>{region.ratePerThousand?.toLocaleString('de-DE', { maximumFractionDigits: 4 }) ?? 'Unbekannt'}</td><td><details><summary>{region.numeratorSources?.length ?? '?'} Quellfälle</summary><PsurReferences values={region.numeratorSources} /></details></td>
        </tr>))}</tbody></table></div>
        <details><summary>Kenntnisstichtag, historische Bearbeitung und Nachträge</summary><p>Exklusive Kenntnisgrenze N: {date(family.sourceManifest?.knowledgeCutoffExclusiveUtc, true, 'UTC')} UTC.</p><p>{family.sourceManifest?.periodKnowledgeRule}</p><p>{family.sourceManifest?.stateEvidence}</p>
          {family.sourceManifest?.cases.map(c => <div className="history-entry" key={c.reference.id}><b>{c.reference.id}@{c.reference.version}</b><p>Belegter Stichtagsstatus: {c.statusAtCutoff}; damalige Version {c.cutoffVersion?.version ?? 'nicht bekannt'}.</p></div>)}
          {family.sourceManifest?.postCutoffCases.map(c => <div className="history-entry" key={`${c.period}-${c.source.id}`}><Badge tone="amber">NACHTRAG · {c.period}</Badge><PsurReferences values={[c.source]} /><p>Spätere Kenntnis {date(c.awarenessAt, true)}. {c.reason}</p></div>)}
        </details>
      </section>
      <section className="panel"><SectionHead eyebrow="HEUTIGE RETROSPEKTIVE KODIERUNG ≠ DAMALIGE RELEASE-GÜLTIGKEIT" title="A/C/D/F und ausdrücklich kodierte Todesfälle" />
        <p>Fehlende Kodierung ist kein Null-Todesfallnachweis. Historische Originalangaben bleiben separat und ungegroupiert; C1901/C1902 bleiben bei ungeklärter Hierarchie ohne automatische Aggregation.</p>
        {family.codingTables?.map(table => <details key={`${table.period}-${table.region}`}><summary>{table.period} / {table.region}: {table.explicitDeathCases.length} explizite aktuelle F02-Belege; {table.unverifiedHistoricalDeathCases?.length ?? 'unbekannt'} ungeprüfte historische F02-Angaben; {table.missingAnnexCodes.length} unvollständig kodierte Fälle</summary><p>{table.completeness}</p>
          <h3>F02 unabhängig von Rang/Häufigkeit</h3><PsurReferences values={table.explicitDeathCases} />
          {!!table.unverifiedHistoricalDeathCases?.length && <><h3>Historische F02-Originalangaben, Release-Gültigkeit nicht bestätigt</h3><PsurReferences values={table.unverifiedHistoricalDeathCases} /></>}
          {table.rows.map((row, i) => <div className="history-entry" key={i}><b>{row.annex}: {row.code} / {row.term}</b><p>{row.status} · {row.historical ? 'Historische Originalangabe' : 'Aktuelle retrospektive Zuordnung'} · Originalkodes {row.originalCodes.join(', ')}</p><PsurReferences values={row.cases} /></div>)}
        </details>)}
      </section>
      <section className="panel"><SectionHead eyebrow="STICHTAG ≠ AKTUELLER FREEZE-STAND" title="CAPA und kontrollierte Änderungen" />
        {!family.actions && <ErrorBanner message="Historisches Maßnahmenmanifest fehlt. Heutiger Status wird nicht rückdatiert." />}
        {[...(family.actions?.capas ?? []), ...(family.actions?.changes ?? [])].map(action => <div className="history-entry" key={action.currentSource.id}><b>{action.currentSource.id} · {action.selection}</b><p>Am Stichtag: {action.statusAtCutoff} / v{action.cutoffSource?.version ?? '?'} / SQL erfasst {date(action.recordedAtCutoff, true)}. Beim Freeze: {action.currentAtFreeze.status} / v{action.currentSource.version}.</p><p>{action.reason}</p><PsurReferences values={[action.currentSource, ...(action.cutoffSource ? [action.cutoffSource] : [])]} /></div>)}
      </section>
      <section className="panel"><SectionHead eyebrow="QUELLEN, AUSSCHLÜSSE UND GENAU EINE WIRKSAME DOKUMENTREVISION" title="Evidenz- und Auswahlverzeichnis" /><p>Dokumentstichtag: {date(family.directory?.documentsEffectiveAt, true)}. {family.directory?.coverage}</p>
        {family.documents.map(document => <details key={`${document.id}-${document.version}`}><summary>{document.title} · {document.id}@{document.version}</summary><p>{document.locator} · {document.origin}</p><p className="mono">{document.hash}</p><blockquote>{document.content}</blockquote></details>)}
        <details><summary>Alle gebundenen Datenquellen und ausgeschlossenen Stände ({family.directory?.entries.length ?? 'nicht belegt'})</summary>{family.directory?.entries.map((entry, i) => <div className="history-entry" key={i}><b>{entry.kind} · {entry.name}</b><p>{entry.sourceType} / {entry.selection} / {entry.origin}</p><p>{entry.reason}</p><PsurReferences values={[entry.source]} /></div>)}</details>
        {frozenProfile && <details><summary>Gebundenes Produktprofil, PRRC und Art. 18</summary><p>{psur.snapshot.series?.groupRationale}</p><p>{frozenProfile.manufacturer.name} / {frozenProfile.manufacturer.srn}; {frozenProfile.device.basicUdi}; Zertifikat {frozenProfile.device.certificateId}; BS {frozenProfile.device.notifiedBodyName} / {frozenProfile.device.notifiedBodyNumber}.</p>
          <p>PRRC: {frozenProfile.prrc.primaryRole}; Vertretung: {frozenProfile.prrc.deputyRole}. {frozenProfile.prrc.qualificationBoundary}</p><p>Organisationszuständigkeit, keine zusätzliche technische Freigaberolle.</p><p>Art. 18 anwendbar: {frozenProfile.device.article18Applicable ? 'Ja' : 'Nein'}. {frozenProfile.device.article18Rationale}</p><p>{frozenProfile.lifecyclePolicy}</p>
        </details>}
      </section>
      <section className="panel"><SectionHead title="Befunde des gebundenen Entwurfs" />{psur.findings.map((finding, i) => <div className="history-entry" key={i}><Badge tone={['Critical', 'Major'].includes(finding.severity) ? 'danger' : 'amber'}>{finding.severity} · {finding.code}</Badge><p>{finding.description}</p><p>{finding.remedy}</p></div>)}<p className="muted">Keine automatische Risikoakzeptanz. Export und Freigabe prüfen den gespeicherten Stand erneut; neue Quellen erfordern eine neue kontrollierte Fassung.</p></section>
    </>}
  </div>
}

function PsurReferences({ values }: { values?: VersionReference[] | null }) {
  if (!values) return <p>Kein hinreichendes Quellenmanifest vorhanden.</p>
  if (!values.length) return <p>Keine ausgewiesenen Quellfälle in dieser Liste; Datenstatus und Nenner gesondert beachten.</p>
  return <div>{values.map((value, i) => <p key={`${value.id}-${value.version}-${i}`}><b>{value.id}@{value.version}</b><br /><small className="mono">{value.hash}</small></p>)}</div>
}

export function AuditWorkspace({ cases, revision }: { cases: CaseSummary[]; revision: number }) {
  const [selected, setSelected] = useState('')
  const id = selected || cases[0]?.id
  return <section className="panel"><SectionHead eyebrow="WER. WAS. WARUM. AUF WELCHER EVIDENZ." title="Unveränderliche Ereigniskette"><Fingerprint size={25} /></SectionHead><p className="muted">Domänenänderung, neue Version, Audit und Outbox werden atomar in SQL geschrieben. Ledger und Hashkette ersetzen nicht die noch offene externe Digest-/Wiederherstellungsabnahme.</p><label className="field compact">Fallakte<select value={id} onChange={e => setSelected(e.target.value)}>{cases.map(c => <option key={c.id} value={c.id}>{c.reference} · {c.title}</option>)}</select></label>{id && <AuditEntries id={id} revision={revision} />}</section>
}
function AuditEntries({ id, revision }: { id: string; revision: number }) {
  const { data, error } = useData<AuditEvent[]>(`/cases/${id}/audit`, revision)
  if (error) return <ErrorBanner message={error} />
  if (!data) return <Loading />
  return <><div className="row audit-export"><Badge>{data.length} persistierte Ereignisse</Badge><a className="button secondary" href={`/api/cases/${id}/manifest`} target="_blank" rel="noreferrer"><Download size={16} />Manifest & Hashprüfung</a></div><div className="audit-timeline">{data.map(e => <article key={e.sequence}><span className="audit-sequence">{e.version}</span><div><div className="row-between"><h3>{e.action}</h3><time>{date(e.at, true)}</time></div><p>{e.reason}</p><div className="audit-meta"><b>{e.actorName}</b><span>Fallversion {e.version}</span><span className="mono">{hash(e.hash)}</span></div></div></article>)}</div></>
}
