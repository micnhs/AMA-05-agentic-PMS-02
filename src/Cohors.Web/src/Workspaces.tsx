import { useEffect, useState } from 'react'
import { Activity, ArrowUpRight, CheckCheck, Download, FileCheck2, FileText, Fingerprint, Link2, Plus, RefreshCw, ShieldCheck } from 'lucide-react'
import { api } from './api'
import type { Actor, AuditEvent, Capa, CaseSummary, ChangeRequest, Evidence, Psur, SourceRun, Trend } from './types'
import { Badge, date, Empty, ErrorBanner, hash, label, Loading, SectionHead, type Dialog, type Field } from './ui'

function useData<T>(path: string, revision: number) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState('')
  useEffect(() => { let active = true; setError(''); api<T>(path).then(data => { if (active) setData(data) }).catch(e => { if (active) setError(e.message) }); return () => { active = false } }, [path, revision])
  return { data, error }
}
type WorkspaceProps = { actor: Actor; revision: number; mutate: (path: string, body: unknown) => Promise<void>; openDialog: (d: Dialog) => void }
const reason: Field = { name: 'reason', label: 'Begründung / fachliche Entscheidung', type: 'textarea', minLength: 20 }

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
  const [selected, setSelected] = useState('')
  const psur = data?.find(p => p.id === selected) ?? data?.[0]
  return <div className="stack"><section className="panel"><SectionHead eyebrow="EINGEFRORENE DATEN. NACHVOLLZIEHBARE SCHLUSSFOLGERUNGEN." title="Periodischer Sicherheitsbericht">{actor.roles.some(r => ['PmsSpecialist', 'MedicalSafetyReviewer'].includes(r)) && <button className="button primary" onClick={() => openDialog({ title: 'PSUR-Snapshot einfrieren', fields: [{ name: 'productId', label: 'Produktfamilie / Leitprodukt', type: 'select', options: [{ value: 'ortho', label: 'OrthoLink Modular Hip' }, { value: 'cardio', label: 'CardioFlow Valve' }] }], description: 'Periode 15.11.2025–14.11.2026. Offene Fälle bleiben enthalten. Worldwide umfasst die Teilregion; keine Addition beider Nenner.', submitLabel: 'Snapshot & Replay-Entwurf erstellen', onSubmit: v => mutate('/psurs', { ...v, leadProductId: v.productId, start: '2025-11-15', end: '2026-11-14', region: 'Worldwide' }) })}><Plus size={17} />PSUR-Zyklus anlegen</button>}</SectionHead>{error && <ErrorBanner message={error} />}
      {!!data?.length && <label className="field compact">Snapshot<select value={psur?.id} onChange={e => setSelected(e.target.value)}>{data.map(p => <option key={p.id} value={p.id}>{p.productId} · {date(p.snapshot.end)} · {label(p.status)}</option>)}</select></label>}
      {!data?.length && <Empty title="Noch kein eingefrorener PSUR-Datenstand">Zähler, passende Nenner, Fallversionen, Dokumente, Trends und Maßnahmen werden gemeinsam gebunden.</Empty>}
    </section>{psur && <div className="review-grid"><article className="panel psur-document"><div className="row-between"><Badge tone="amber">REPLAY-ENTWURF</Badge><Badge>{label(psur.status)}</Badge></div><p className="mono">Snapshot {hash(psur.snapshotHash)}</p>{psur.draft.split('\n').map((line, i) => line.startsWith('# ') ? <h1 key={i}>{line.slice(2)}</h1> : line.startsWith('## ') ? <h2 key={i}>{line.slice(3)}</h2> : <p key={i}>{line}</p>)}</article><aside className="panel"><h2>Snapshot & Human Review</h2><p>{date(psur.snapshot.start)} – {date(psur.snapshot.end)}</p><Badge>{psur.snapshot.region}</Badge><div className="snapshot-count"><strong>{psur.snapshot.cases.length}</strong><span>Fallversionen gebunden</span></div><div className="callout">Offene Fragen sind enthalten, nicht als bestätigt dargestellt und nicht aus dem PSUR ausgeblendet.</div><p className="mono">Freigabe-Hash<br />{hash(psur.scopeHash)}</p>{psur.approvals.map(a => <div className="history-entry" key={a.id}><b>{a.actorName}</b><p>{a.rationale}</p><small>{date(a.at, true)}</small></div>)}{actor.roles.some(r => ['MedicalSafetyReviewer', 'RegulatoryAffairs'].includes(r)) && psur.status !== 'Approved' && <button className="button primary full" onClick={() => openDialog({ title: 'PSUR-Inhalt und Snapshot prüfen', fields: [reason], onSubmit: v => mutate(`/psurs/${psur.id}/approve`, { ...v, expectedVersion: psur.version }) })}><ShieldCheck size={17} />Mit eigener Rolle freigeben</button>}{actor.roles.includes('RegulatoryAffairs') && psur.status === 'Approved' && !psur.notifiedBodyReceipt && <button className="button primary full" onClick={() => openDialog({ title: 'Antwort der Benannten Stelle simulieren', fields: [reason], onSubmit: v => mutate(`/psurs/${psur.id}/simulate`, { ...v, expectedVersion: psur.version }) })}>BS-Antwort simulieren</button>}{psur.notifiedBodyReceipt && <div className="receipt"><Badge tone="blue">SIMULIERTE BS-ANTWORT</Badge><b>{psur.notifiedBodyReceipt}</b><p>Keine reale Behörden- oder BS-Akzeptanz.</p></div>}</aside></div>}</div>
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
