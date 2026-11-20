import { useCallback, useEffect, useRef, useState } from 'react'
import { FileUp, RefreshCw, Scale } from 'lucide-react'
import { api } from './api'
import type { Actor, DenominatorPreview, Device, DistributionAffected, DistributionImport, DistributionRow, Exposure, IntakeImport, IntakeImportChannel, IntakeImportRow } from './types'
import { ActionDialog, Badge, date, ErrorBanner, hash, Loading, SectionHead, type Dialog, type Field } from './ui'

const channels: { value: IntakeImportChannel; label: string }[] = [
  { value: 'SyntheticDistributorFeedback', label: 'Synthetische Händlerrückmeldung' },
  { value: 'SyntheticClinicFeedback', label: 'Synthetische Klinikrückmeldung' },
  { value: 'SyntheticCallCenterLog', label: 'Synthetisches Callcenter-Protokoll' },
]
const outcomes: Record<IntakeImportRow['outcome'], { label: string; tone: string }> = {
  Registered: { label: 'Eingangssignal registriert', tone: 'blue' },
  Duplicate: { label: 'Als Dublette verknüpft', tone: 'neutral' },
  Rejected: { label: 'Mit Grund abgewiesen', tone: 'danger' },
  Pending: { label: 'Übernahme offen', tone: 'amber' },
  CorrectionRequired: { label: 'Zur menschlichen Korrektur zurückgestellt', tone: 'amber' },
}
const regions = [{ value: 'Worldwide', label: 'Worldwide' }, { value: 'EEA-Turkey-NI', label: 'EEA-Turkey-NI (EU/EWR, Türkei, Nordirland)' }]
const maximumBytes = 256 * 1024
const reasonField = (minLength: number): Field => ({ name: 'reason', label: `Fachliche Begründung (mindestens ${minLength} Zeichen)`, type: 'textarea', minLength })
const failed = (failure: unknown, fallback: string) => failure instanceof Error ? failure.message : fallback
const channelName = (value: string) => channels.find(c => c.value === value)?.label ?? value
const isoDay = (value: Date) => value.toISOString().slice(0, 10)
const period = (start: string, end: string) => `${date(start)} – ${date(end)}`

type Original = { name: string; bytes: number; content: string; hash: string }
type Shared = { actor: Actor; products: Device[]; live: { current: boolean }; reload: () => void; setDialog: (dialog: Dialog) => void; setError: (message: string) => void }

async function readOriginal(file: File): Promise<Original> {
  if (!/^[A-Za-z0-9._-]{1,116}\.csv$/.test(file.name)) throw new Error('Dateiname: höchstens 120 Zeichen aus A–Z, a–z, 0–9, Punkt, Unterstrich und Bindestrich mit Endung .csv.')
  if (file.size === 0 || file.size > maximumBytes) throw new Error('Nur eine synthetische CSV-Datei mit höchstens 256 KiB auswählen.')
  const buffer = await file.arrayBuffer()
  let content: string
  try { content = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(buffer) }
  catch { throw new Error('Datei ist kein gültiges UTF-8; nichts importiert.') }
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', buffer))
  return { name: file.name, bytes: buffer.byteLength, content, hash: Array.from(digest, b => b.toString(16).padStart(2, '0')).join('') }
}
function quarterOf(rows: DistributionRow[]) {
  const days = rows.map(r => r.deliveredOn).sort()
  if (!days.length) return { start: '', end: '' }
  const first = new Date(`${days[0]}T00:00:00Z`), last = new Date(`${days[days.length - 1]}T00:00:00Z`)
  return { start: isoDay(new Date(Date.UTC(first.getUTCFullYear(), Math.floor(first.getUTCMonth() / 3) * 3, 1))),
    end: isoDay(new Date(Date.UTC(last.getUTCFullYear(), Math.floor(last.getUTCMonth() / 3) * 3 + 3, 0))) }
}

export default function ImportWorkspace({ actor, products, revision, open }: { actor: Actor; products: Device[]; revision: number; open: (id: string) => void }) {
  const [intake, setIntake] = useState<IntakeImport[] | null>(null)
  const [distribution, setDistribution] = useState<DistributionImport[] | null>(null)
  const [exposures, setExposures] = useState<Exposure[] | null>(null)
  const [intakeId, setIntakeId] = useState('')
  const [distributionId, setDistributionId] = useState('')
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const [error, setError] = useState('')
  const live = useRef(true)
  const generation = useRef(0)
  const load = useCallback(async () => {
    const request = ++generation.current
    const [nextIntake, nextDistribution, nextExposures] = await Promise.all([api<IntakeImport[]>('/imports/intake'),
      api<DistributionImport[]>('/imports/distribution'), api<Exposure[]>('/exposures')])
    if (!live.current || request !== generation.current) return
    setIntake(nextIntake); setDistribution(nextDistribution); setExposures(nextExposures)
  }, [])
  const reload = useCallback(() => { void load().catch(failure => { if (live.current) setError(failed(failure, 'Importe nicht geladen.')) }) }, [load])
  useEffect(() => { live.current = true; reload(); return () => { live.current = false; generation.current++ } }, [reload, revision])
  const shared: Shared = { actor, products, live, reload, setDialog, setError }
  const selectedIntake = intake?.find(i => i.id === intakeId)
  const selectedDistribution = distribution?.find(d => d.id === distributionId)
  return <>
    <div className="callout" role="note" aria-label="Grenzen der Datenimporte"><b>SYNTHETISCH · Simulation.</b> Kein ERP, kein Patientenregister, kein Echtkanal, keine KI-Extraktion.
      <p>Importierte Kanalzeilen sind Eingangssignale, keine bestätigten Sicherheitsfälle; Fristen entstehen nur für registrierte Zeilen über den bestehenden Eingangspfad.
        Ein Nenner wird erst durch menschliche Quality-Freigabe wirksam; gebundene Trend-/PSUR-Ergebnisse behalten ihre Nennerversion.</p></div>
    {error && <ErrorBanner message={error} dismiss={() => setError('')} />}
    <IntakeSection shared={shared} imports={intake} select={setIntakeId} />
    {selectedIntake && <IntakeDetail value={selectedIntake} open={open} close={() => setIntakeId('')} />}
    <DistributionSection shared={shared} imports={distribution} select={setDistributionId} />
    {selectedDistribution && <DistributionDetail key={`${selectedDistribution.id}:${selectedDistribution.version}`} shared={shared} value={selectedDistribution} close={() => setDistributionId('')} />}
    <ExposureSection exposures={exposures} />
    {dialog && <ActionDialog key={dialog.title} dialog={dialog} close={() => { if (live.current) setDialog(null) }} />}
  </>
}

function IntakeSection({ shared, imports, select }: { shared: Shared; imports: IntakeImport[] | null; select: (id: string) => void }) {
  const { actor, products, live, reload, setDialog, setError } = shared
  const [productId, setProductId] = useState('')
  const [channel, setChannel] = useState<IntakeImportChannel>('SyntheticDistributorFeedback')
  const [file, setFile] = useState<File | null>(null)
  const input = useRef<HTMLInputElement>(null)
  const product = products.find(p => p.id === productId) ?? products[0]
  const author = actor.isHuman && actor.roles.includes('PmsSpecialist')
  async function start() {
    if (!file || !product) return
    setError('')
    try {
      const original = await readOriginal(file)
      if (!live.current) return
      setDialog({ title: 'Synthetische Kanaldatei übernehmen',
        description: `${original.name} · ${original.bytes} Bytes · SHA-256 ${hash(original.hash)} · ${channelName(channel)} · ${product.name}. Jede Zeile wird als Eingangssignal registriert, als Dublette verknüpft, mit Grund abgewiesen oder zur menschlichen Korrektur zurückgestellt; nichts wird still verworfen. Keine KI-Extraktion, keine Bewertung, keine Meldung.`,
        submitLabel: 'Zeilen prüfen und übernehmen', fields: [reasonField(30)], onSubmit: async values => {
          const result = await api<IntakeImport>('/imports/intake', { productId: product.id, channel, fileName: original.name,
            content: original.content, reason: values.reason, synthetic: true })
          if (!live.current) return
          setFile(null); if (input.current) input.current.value = ''
          select(result.id)
          if (result.original.hash !== original.hash) setError('Gespeicherter Originalhash weicht vom lokal berechneten Dateihash ab. Import nicht verwenden, sondern prüfen.')
          reload()
        } })
    } catch (failure) { if (live.current) setError(failed(failure, 'Kanaldatei nicht gelesen.')) }
  }
  return <section className="panel" aria-label="Synthetischer Kanal-Batchimport">
    <SectionHead eyebrow="EINGANGSKANÄLE · ORIGINALHASH UND ZEILENBEZUG" title="Synthetischer Kanal-Batchimport">
      <button className="button secondary" onClick={reload}><RefreshCw size={16} />Neu lesen</button>
    </SectionHead>
    <p>Strukturierte synthetische Kanaldatei. Validierte Zeilen laufen über denselben Eingangspfad wie „Beschwerde aufnehmen“; gesetzliche Fristen entstehen dort unverändert aus der ersten Organisationskenntnis der Zeile.</p>
    {author ? <div className="eudamed-actions">
      <label className="field">Produktfamilie (Kanalimport)<select value={product?.id ?? ''} onChange={e => setProductId(e.target.value)}>{products.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
      <label className="field">Eingangskanal<select value={channel} onChange={e => { const next = channels.find(c => c.value === e.target.value); if (next) setChannel(next.value) }}>{channels.map(c => <option key={c.value} value={c.value}>{c.label}</option>)}</select></label>
      <label className="field">Synthetische Kanaldatei (CSV, max. 256 KiB)<input ref={input} type="file" accept=".csv,text/csv" onChange={e => setFile(e.target.files?.[0] ?? null)} /></label>
      <button className="button primary" disabled={!file || !product} onClick={() => void start()}><FileUp size={17} />Kanaldatei prüfen</button>
    </div> : <p className="muted">Kanalimporte startet ausschließlich eine menschliche PMS-Rolle; hier nur Lesestand.</p>}
    {!imports ? <Loading /> : !imports.length ? <p>Noch kein Kanalimport in diesem Produktscope.</p> :
      <div className="workspace-table"><table className="case-table" aria-label="Kanalimporte"><thead><tr><th>Datei / Kanal</th><th>Eingang</th><th>Zeilen</th><th>Status</th><th><span className="sr-only">Anzeigen</span></th></tr></thead>
        <tbody>{imports.map(i => <tr key={i.id} data-import-id={i.id}>
          <td><button className="case-title" onClick={() => select(i.id)}>{i.original.fileName}</button><span className="subline">{channelName(i.channel)} <span>·</span> {i.productId} <span>·</span> v{i.version}</span></td>
          <td>{date(i.receivedAt, true)}<small className="subline">{i.importedBy}</small></td>
          <td>{i.counts.rows} Zeilen<small className="subline">{i.counts.registered} registriert · {i.counts.duplicates} Dubletten · {i.counts.rejected} abgewiesen{i.counts.correctionRequired ? ` · ${i.counts.correctionRequired} zur Korrektur zurückgestellt` : ''}{i.counts.pending ? ` · ${i.counts.pending} offen` : ''}</small></td>
          <td>{i.status === 'Processed' ? <Badge tone="blue">Verarbeitet</Badge> : <Badge tone="amber">Unterbrochen · Übernahme offen</Badge>}</td>
          <td><button className="button secondary" onClick={() => select(i.id)}>Zeilen anzeigen</button></td>
        </tr>)}</tbody></table></div>}
  </section>
}

function IntakeDetail({ value, open, close }: { value: IntakeImport; open: (id: string) => void; close: () => void }) {
  return <section className="panel" aria-label="Kanalimport-Zeilen">
    <SectionHead eyebrow={`KANALIMPORT v${value.version}`} title={value.original.fileName}><button className="button secondary" onClick={close}>Schließen</button></SectionHead>
    <p>{channelName(value.channel)} · {value.productId} · Eingang {date(value.receivedAt, true)} durch {value.importedBy}{value.processedAt ? ` · verarbeitet ${date(value.processedAt, true)}` : ''}</p>
    <p>Originalhash <span className="mono">{value.original.hash}</span> · {value.original.bytes} Bytes · <span className="mono">{value.original.uri}</span> · Vertrag {value.contract}</p>
    <p>Begründung: {value.reason}</p>
    {value.status !== 'Processed' && <div className="callout" role="status">Übernahme unterbrochen. Dieselbe Person setzt sie fort, indem sie dieselbe Datei für Produktfamilie und Kanal erneut importiert; bereits übernommene Zeilen werden nicht doppelt registriert.</div>}
    <div className="callout">{value.boundary}</div>
    <div className="workspace-table"><table className="case-table" aria-label="Importzeilen"><thead><tr><th>Zeile</th><th>Kanalreferenz / Vorfall</th><th>Ergebnis</th><th>Begründung</th><th>Fall</th></tr></thead>
      <tbody>{value.rows.map(row => <tr key={row.line} data-line={row.line} data-outcome={row.outcome} data-code={row.code}>
        <td>{row.line}<small className="subline mono">{hash(row.rowHash)}</small></td>
        <td>{row.externalRef ?? 'Nicht lesbar'}<small className="subline">{row.incidentKey ?? 'Keine gültige Vorfallreferenz'}</small></td>
        <td><Badge tone={outcomes[row.outcome].tone}>{outcomes[row.outcome].label}</Badge><small className="subline mono">{row.code}</small></td>
        <td>{row.reason}{row.duplicateOf !== null && <small className="subline">Kopie von Zeile {row.duplicateOf}</small>}</td>
        <td>{row.case ? <button className="text-button" onClick={() => open(row.case!.id)}>Fall v{row.case.version} öffnen</button> : <span className="muted">Kein Fall</span>}</td>
      </tr>)}</tbody></table></div>
  </section>
}

function DistributionSection({ shared, imports, select }: { shared: Shared; imports: DistributionImport[] | null; select: (id: string) => void }) {
  const { actor, products, live, reload, setDialog, setError } = shared
  const [productId, setProductId] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const input = useRef<HTMLInputElement>(null)
  const product = products.find(p => p.id === productId) ?? products[0]
  const author = actor.isHuman && actor.roles.some(role => role === 'PmsSpecialist' || role === 'QualityManager')
  async function start() {
    if (!file || !product) return
    setError('')
    try {
      const original = await readOriginal(file)
      if (!live.current) return
      setDialog({ title: 'Synthetische Lieferdatei prüfen',
        description: `${original.name} · ${original.bytes} Bytes · SHA-256 ${hash(original.hash)} · ${product.name}. Aggregierte synthetische Liefer- und Implantationszahlen ohne Personenbezug. Jeder Befund weist die gesamte Datei ab; ein Nenner entsteht erst durch eine getrennte Quality-Freigabe.`,
        submitLabel: 'Lieferdatei prüfen und speichern', fields: [reasonField(30)], onSubmit: async values => {
          const result = await api<DistributionImport>('/imports/distribution', { productId: product.id, fileName: original.name,
            content: original.content, reason: values.reason })
          if (!live.current) return
          setFile(null); if (input.current) input.current.value = ''
          select(result.id)
          if (result.original.hash !== original.hash) setError('Gespeicherter Originalhash weicht vom lokal berechneten Dateihash ab. Import nicht verwenden, sondern prüfen.')
          reload()
        } })
    } catch (failure) { if (live.current) setError(failed(failure, 'Lieferdatei nicht gelesen.')) }
  }
  return <section className="panel" aria-label="Liefer- und Expositionsdaten">
    <SectionHead eyebrow="EXPOSITION · CHARGE, LAND, EINHEIT" title="Liefer- und Expositionsdaten" />
    <p>Synthetische aggregierte Liefer-/Implantationszeilen mit Herkunft. Absatz, implantierte Einheiten und Empfänger bleiben getrennt; Mehrfachimplantate werden ausgewiesen, nicht verrechnet.</p>
    {author ? <div className="eudamed-actions">
      <label className="field">Produktfamilie (Lieferimport)<select value={product?.id ?? ''} onChange={e => setProductId(e.target.value)}>{products.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
      <label className="field">Synthetische Lieferdatei (CSV, max. 256 KiB)<input ref={input} type="file" accept=".csv,text/csv" onChange={e => setFile(e.target.files?.[0] ?? null)} /></label>
      <button className="button primary" disabled={!file || !product} onClick={() => void start()}><FileUp size={17} />Lieferdatei prüfen</button>
    </div> : <p className="muted">Lieferimporte starten ausschließlich menschliche PMS- oder Quality-Rollen; hier nur Lesestand.</p>}
    {!imports ? <Loading /> : !imports.length ? <p>Noch kein Lieferimport in diesem Produktscope.</p> :
      <div className="workspace-table"><table className="case-table" aria-label="Lieferimporte"><thead><tr><th>Datei</th><th>Eingang</th><th>Prüfung</th><th>Geliefert / implantiert / Empfänger</th><th><span className="sr-only">Anzeigen</span></th></tr></thead>
        <tbody>{imports.map(i => <tr key={i.id} data-import-id={i.id}>
          <td><button className="case-title" onClick={() => select(i.id)}>{i.original.fileName}</button><span className="subline">{i.productId} <span>·</span> v{i.version}</span></td>
          <td>{date(i.receivedAt, true)}<small className="subline">{i.importedBy}</small></td>
          <td>{i.status === 'Validated' ? <Badge tone="blue">Validiert</Badge> : <Badge tone="danger">Abgewiesen</Badge>}<small className="subline">{i.totals.validRows}/{i.totals.rows} Zeilen gültig · {i.totals.findings} Befunde</small></td>
          <td>{i.totals.unitsDelivered} / {i.totals.unitsImplanted} / {i.totals.recipients}<small className="subline">{i.totals.additionalImplants} zusätzliche Implantate (Mehrfachimplantate)</small></td>
          <td><button className="button secondary" onClick={() => select(i.id)}>Details anzeigen</button></td>
        </tr>)}</tbody></table></div>}
  </section>
}

function DistributionDetail({ shared, value, close }: { shared: Shared; value: DistributionImport; close: () => void }) {
  const { actor, live, reload, setDialog, setError } = shared
  const initial = quarterOf(value.rows)
  const [batches, setBatches] = useState('')
  const [jurisdictions, setJurisdictions] = useState('')
  const [affected, setAffected] = useState<DistributionAffected | null>(null)
  const [region, setRegion] = useState('Worldwide')
  const [start, setStart] = useState(initial.start)
  const [end, setEnd] = useState(initial.end)
  const [preview, setPreview] = useState<DenominatorPreview | null>(null)
  const [approved, setApproved] = useState<Exposure | null>(null)
  const [busy, setBusy] = useState(false)
  const quality = actor.isHuman && actor.roles.includes('QualityManager')
  const path = `/imports/distribution/${encodeURIComponent(value.id)}`
  async function query<T>(url: string, receive: (result: T) => void) {
    setBusy(true); setError('')
    try { const result = await api<T>(url); if (live.current) receive(result) }
    catch (failure) { if (live.current) setError(failed(failure, 'Abfrage nicht bestätigt.')) }
    finally { if (live.current) setBusy(false) }
  }
  function approve(current: DenominatorPreview) {
    const { proposal } = current
    setDialog({ title: 'Nenner menschlich freigeben',
      description: `${current.targetExposureId} wird v${current.targetVersion + 1}: ${proposal.count} ${proposal.unit} (${proposal.region}, ${period(proposal.start, proposal.end)}; bisher ${current.currentCount ?? 'kein Nenner'}). Quelle ${proposal.import.id} v${proposal.import.version}, Zeilen ${proposal.lines.join(', ')}. Bereits gebundene Trend-/PSUR-Ergebnisse behalten ihre Nennerversion; nichts wird still neu berechnet.`,
      submitLabel: 'Nenner freigeben', fields: [reasonField(40)], onSubmit: async values => {
        const result = await api<Exposure>('/exposures/denominator-approval', { importId: value.id, importVersion: value.version,
          region: proposal.region, start: proposal.start, end: proposal.end, expectedCount: proposal.count,
          expectedVersion: current.targetVersion, reason: values.reason })
        if (!live.current) return
        setApproved(result); setPreview(null)
        reload()
      } })
  }
  return <section className="panel" aria-label="Lieferimport-Details">
    <SectionHead eyebrow={`LIEFERIMPORT v${value.version}`} title={value.original.fileName}><button className="button secondary" onClick={close}>Schließen</button></SectionHead>
    <p>{value.productId} · Eingang {date(value.receivedAt, true)} durch {value.importedBy} · {value.status === 'Validated' ? 'validiert' : 'abgewiesen'}</p>
    <p>Originalhash <span className="mono">{value.original.hash}</span> · {value.original.bytes} Bytes · <span className="mono">{value.original.uri}</span> · Vertrag {value.contract}</p>
    <p>Begründung: {value.reason}</p>
    <div className="callout">{value.boundary}</div>
    {value.findings.length > 0 && <><h3>Befunde ({value.findings.length}) · gesamte Datei abgewiesen</h3>
      <div className="workspace-table"><table className="case-table" aria-label="Importbefunde"><thead><tr><th>Zeile</th><th>Code</th><th>Befund</th></tr></thead>
        <tbody>{value.findings.map((f, i) => <tr key={`${f.line}-${f.code}-${i}`} data-line={f.line} data-code={f.code}><td>{f.line}<small className="subline mono">{hash(f.rowHash)}</small></td><td className="mono">{f.code}</td><td>{f.message}</td></tr>)}</tbody></table></div></>}
    <details><summary>Gültige Lieferzeilen ({value.rows.length}) anzeigen</summary>
      <div className="workspace-table"><table className="case-table" aria-label="Lieferzeilen"><thead><tr><th>Zeile / Referenz</th><th>UDI-DI / Charge</th><th>Standort / Markt</th><th>Lieferdatum</th><th>Geliefert / implantiert / Empfänger</th></tr></thead>
        <tbody>{value.rows.map(r => <tr key={r.line} data-line={r.line}><td>{r.line} · {r.rowRef}<small className="subline mono">{hash(r.rowHash)}</small></td><td>{r.udiDi}<small className="subline">{r.batch}</small></td>
          <td>{r.siteRef}<small className="subline">{r.country} · {r.jurisdiction}</small></td><td>{date(r.deliveredOn)}</td>
          <td>{r.unitsDelivered} / {r.unitsImplanted} / {r.recipients}{r.additionalImplants > 0 && <small className="subline">{r.additionalImplants} zusätzliche Implantate</small>}</td></tr>)}</tbody></table></div>
    </details>
    {value.status === 'Validated' && <>
      <h3>Lieferbezug nach Charge und Markt</h3>
      <form className="eudamed-actions" aria-label="Lieferbezug prüfen" onSubmit={e => {
        e.preventDefault()
        const parameters = new URLSearchParams({ batches, jurisdictions })
        void query<DistributionAffected>(`${path}/affected?${parameters}`, setAffected)
      }}>
        <label className="field">Chargen (kommagetrennt, DEMO-…)<input value={batches} onChange={e => setBatches(e.target.value)} required /></label>
        <label className="field">Jurisdiktionen (leer = alle freigegebenen Märkte)<input value={jurisdictions} onChange={e => setJurisdictions(e.target.value)} placeholder="EU-IE, GB, NI" /></label>
        <button className="button secondary" disabled={busy}>Lieferbezug prüfen</button>
      </form>
      {affected && <AffectedView value={affected} />}
      <h3>Nennervorschlag für eine abgeschlossene Periode</h3>
      <form className="eudamed-actions" aria-label="Nennervorschlag berechnen" onSubmit={e => {
        e.preventDefault(); setApproved(null)
        const parameters = new URLSearchParams({ region, start, end })
        void query<DenominatorPreview>(`${path}/denominator-proposal?${parameters}`, setPreview)
      }}>
        <label className="field">Region<select value={region} onChange={e => setRegion(e.target.value)}>{regions.map(r => <option key={r.value} value={r.value}>{r.label}</option>)}</select></label>
        <label className="field">Periodenbeginn<input type="date" value={start} onChange={e => setStart(e.target.value)} required /></label>
        <label className="field">Periodenende<input type="date" value={end} onChange={e => setEnd(e.target.value)} required /></label>
        <button className="button secondary" disabled={busy}><Scale size={16} />Vorschlag berechnen</button>
      </form>
      {preview && <div className="callout" role="status" aria-label="Nennervorschlag">
        <b>Vorschlag, keine Freigabe: {preview.proposal.count} {preview.proposal.unit}</b> · {preview.proposal.region} · {period(preview.proposal.start, preview.proposal.end)}
        <p>{preview.proposal.recipients} Empfänger (Obergrenze) · {preview.proposal.additionalImplants} zusätzliche Implantate · Zeilen {preview.proposal.lines.join(', ')} · Basis: {preview.proposal.dateBasis} · Lieferdaten der Datei {period(preview.proposal.coveredFrom, preview.proposal.coveredThrough)}</p>
        <p>Ziel <span className="mono">{preview.targetExposureId}</span> · bisher {preview.targetVersion ? `v${preview.targetVersion} mit ${preview.currentCount ?? '–'}` : 'kein Nenner'} · Vorschlagshash <span className="mono">{hash(preview.proposal.hash)}</span></p>
        <p>{preview.proposal.boundary}</p>
        {preview.unchanged ? <p><b>Genau dieser Vorschlag ist bereits freigegeben.</b></p>
          : !quality ? <p>Freigabe ausschließlich durch eine menschliche Quality-Rolle.</p>
          : actor.id === value.importedBy ? <p>Vier-Augen-Prinzip: Freigabe nur durch eine andere Person als die importierende.</p>
          : <button className="button primary" onClick={() => approve(preview)}>Nenner freigeben</button>}
      </div>}
      {approved && <div className="callout" role="status" aria-label="Freigegebener Nenner"><b>Freigegeben: {approved.id} v{approved.version}</b> · {approved.count} {approved.unit} · {approved.region} · {period(approved.start, approved.end)}</div>}
    </>}
  </section>
}

function AffectedView({ value }: { value: DistributionAffected }) {
  return <div aria-label="Lieferbezug">
    {value.unknownBatches.length > 0 && <p>Ohne Lieferzeile im geprüften Marktscope: <b>{value.unknownBatches.join(', ')}</b>. Das ist kein Nachweis fehlender Betroffenheit.</p>}
    {value.matched.length > 0 && <div className="workspace-table"><table className="case-table" aria-label="Betroffene Chargen"><thead><tr><th>Charge / UDI-DI</th><th>Märkte</th><th>Lieferzeitraum</th><th>Geliefert / implantiert / Empfänger</th></tr></thead>
      <tbody>{value.matched.map(m => <tr key={m.batch} data-batch={m.batch}><td>{m.batch}<small className="subline">{m.udiDi} · {m.rows} Zeilen</small></td><td>{m.jurisdictions.join(', ')}</td><td>{period(m.firstDelivery, m.lastDelivery)}</td><td>{m.unitsDelivered} / {m.unitsImplanted} / {m.recipients}</td></tr>)}</tbody></table></div>}
    {value.markets.length > 0 && <div className="workspace-table"><table className="case-table" aria-label="Betroffene Märkte"><thead><tr><th>Jurisdiktion</th><th>Rechtsrahmen</th><th>Geliefert / implantiert / Empfänger</th></tr></thead>
      <tbody>{value.markets.map(m => <tr key={m.jurisdiction} data-jurisdiction={m.jurisdiction}><td>{m.jurisdiction}<small className="subline">{m.country} · {m.rows} Zeilen</small></td><td>{m.scope}</td><td>{m.unitsDelivered} / {m.unitsImplanted} / {m.recipients}</td></tr>)}</tbody></table></div>}
    <p className="muted">{value.method} {value.boundary} Geprüfte Märkte: {value.jurisdictions.join(', ')} · Zeilen {value.lines.join(', ') || 'keine'} · <span className="mono">{hash(value.hash)}</span></p>
  </div>
}

function ExposureSection({ exposures }: { exposures: Exposure[] | null }) {
  const rows = [...exposures ?? []].sort((a, b) => a.productId.localeCompare(b.productId) || a.start.localeCompare(b.start) || a.region.localeCompare(b.region))
  return <section className="panel" aria-label="Freigegebene Nenner">
    <SectionHead eyebrow="NENNER · VERSIONIERT" title="Freigegebene Expositionsnenner" />
    <p>Jede Freigabe erzeugt eine neue Nennerversion. Bereits gebundene Trend- und PSUR-Ergebnisse behalten die beim Binden verwendete Version.</p>
    {!exposures ? <Loading /> : !rows.length ? <p>Kein Nenner in diesem Produktscope.</p> :
      <div className="workspace-table"><table className="case-table" aria-label="Expositionsnenner"><thead><tr><th>Nenner / Version</th><th>Periode / Region</th><th>Anzahl / Einheit</th><th>Herkunft</th></tr></thead>
        <tbody>{rows.map(e => <tr key={e.id} data-exposure-id={e.id} data-version={e.version}>
          <td>{e.id}<small className="subline">v{e.version} · {e.approved ? 'freigegeben' : 'nicht freigegeben'}</small></td>
          <td>{period(e.start, e.end)}<small className="subline">{e.region}</small></td>
          <td>{e.count} {e.unit}<small className="subline">{e.population}</small></td>
          <td>{e.source ? <>Lieferimport {e.source.import.id} v{e.source.import.version}<small className="subline">Zeilen {e.source.lines.join(', ')} · {e.source.recipients} Empfänger · {e.source.additionalImplants} zusätzliche Implantate · freigegeben durch {e.source.approvedBy} am {date(e.source.approvedAt, true)}</small></> : e.provenance}</td>
        </tr>)}</tbody></table></div>}
  </section>
}
