import { useEffect, useRef, useState, type FormEvent } from 'react'
import { api } from './api'
import type { CaseSummary, Device, SignalDocumentImportResult, SignalDocumentMatch } from './types'
import { date, ErrorBanner, SectionHead } from './ui'

export function DocumentMatches({ matches }: { matches: SignalDocumentMatch[] }) {
  return matches.length ? <div className="callout" role="status"><b>Mögliche Mehrfachaufnahme – keine automatische Zusammenführung</b>
    <p>Bytegleiche Dokumente oder dieselbe Quellreferenz beweisen keinen identischen Patienten oder Vorfall.</p>
    {matches.map(match => <p key={match.source.id}>{match.match === 'IdenticalBytes' ? 'Bytegleiches Original' : 'Gleiche Quellreferenz, andere Originalbytes'}: {match.title}
      <br /><small className="mono">{match.source.id}@{match.source.version} · {match.originalHash}</small></p>)}</div> : null
}

type Fields = {
  productId: string; externalId: string; title: string; scope: string; originUri: string; provenance: string; rights: string;
  knowledgeStatus: string; firstKnownAt: string; knowledgeReason: string; reason: string; comparisonReason: string;
  comparison: Pick<CaseSummary, 'id' | 'version' | 'reference'> | null;
  metadataConfirmed: boolean; rightsConfirmed: boolean; contentEligibilityConfirmed: boolean;
}
type SelectedFile = { name: string; mediaType: string; contentBase64: string; text: string; hash: string; length: number }

export default function SignalDocumentImport({ products, cases, onImported }: {
  products: Device[]; cases: CaseSummary[]; onImported: () => void;
}) {
  const [form, setForm] = useState<Fields>({
    productId: '', externalId: '', title: '', scope: '', originUri: '', provenance: '', rights: '',
    knowledgeStatus: '', firstKnownAt: '', knowledgeReason: '', reason: '', comparisonReason: '', comparison: null,
    metadataConfirmed: false, rightsConfirmed: false, contentEligibilityConfirmed: false,
  })
  const [file, setFile] = useState<SelectedFile | null>(null)
  const [key, setKey] = useState(() => crypto.randomUUID())
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')
  const [result, setResult] = useState<SignalDocumentImportResult | null>(null)
  const generation = useRef(0)
  const controller = useRef<AbortController | null>(null)
  useEffect(() => () => { generation.current++; controller.current?.abort() }, [])
  function change(update: Partial<Fields>, confirmation = false) {
    setForm(current => ({ ...current, ...update, ...(!confirmation ? {
      metadataConfirmed: false, rightsConfirmed: false, contentEligibilityConfirmed: false,
    } : {}) }))
    setKey(crypto.randomUUID()); setResult(null)
  }
  async function selectFile(selected?: File) {
    const token = ++generation.current
    change({}); setFile(null); setResult(null); setError('')
    if (!selected) { setBusy(''); return }
    setBusy('Datei wird lokal gelesen…')
    try {
      if (!/\.txt$/i.test(selected.name) || selected.size > 131072)
        throw new Error('Nur UTF-8-.txt bis 128 KiB. PDF, Office und OCR sind nicht angebunden.')
      if (!selected.size) throw new Error('Die Originaldatei ist leer.')
      const buffer = await selected.arrayBuffer()
      if (token !== generation.current) return
      let text: string
      try { text = new TextDecoder('utf-8', { fatal: true }).decode(buffer) }
      catch { throw new Error('Ungültiges UTF-8; kein Ersatztext erzeugt.') }
      if (text.trim().length < 20 || text.length > 30000)
        throw new Error('20 bis 30000 lesbare Textzeichen erforderlich.')
      const digest = await crypto.subtle.digest('SHA-256', buffer)
      if (token !== generation.current) return
      const hash = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('')
      setFile({ name: selected.name, mediaType: selected.type || 'text/plain', text, hash, length: buffer.byteLength,
        contentBase64: btoa(Array.from(new Uint8Array(buffer), byte => String.fromCharCode(byte)).join('')) })
      change({})
    } catch (e) {
      if (token === generation.current) setError(e instanceof Error ? e.message : 'Originaldatei nicht gelesen.')
    } finally { if (token === generation.current) setBusy('') }
  }
  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!file || busy || result) return
    const token = generation.current
    const abort = new AbortController()
    controller.current = abort
    setBusy('Original und bestätigte Metadaten werden gespeichert…'); setError('')
    try {
      const saved = await api<SignalDocumentImportResult>('/signals/import', {
        productId: form.productId, name: file.name, mediaType: file.mediaType, contentBase64: file.contentBase64,
        externalId: form.externalId, title: form.title, scope: form.scope,
        originUri: form.scope === 'PublicAuthorized' ? form.originUri : null,
        provenance: form.provenance, rights: form.rights, knowledgeStatus: form.knowledgeStatus,
        firstKnownAt: form.knowledgeStatus === 'Known' ? new Date(form.firstKnownAt).toISOString() : null,
        knowledgeReason: form.knowledgeReason, metadataConfirmed: form.metadataConfirmed,
        rightsConfirmed: form.rightsConfirmed, contentEligibilityConfirmed: form.contentEligibilityConfirmed,
        comparisonCaseId: form.comparison?.id ?? null, comparisonCaseVersion: form.comparison?.version ?? null,
        comparisonReason: form.comparison ? form.comparisonReason : null, reason: form.reason,
      }, { signal: abort.signal, idempotencyKey: key })
      if (token !== generation.current) return
      if (saved.source.document?.contract !== 'cohors-signal-document-1' ||
          saved.source.document.original.hash !== file.hash || saved.source.document.original.length !== file.length)
        throw new Error('Importantwort ist nicht an die ausgewählten Originalbytes gebunden; Ergebnis prüfen, kein Erfolg bestätigt.')
      setResult(saved); onImported()
    } catch (e) {
      if (token === generation.current) setError(e instanceof Error ? e.message : 'Import nicht bestätigt.')
    } finally { if (token === generation.current) { setBusy(''); controller.current = null } }
  }
  const currentComparison = cases.find(value => value.id === form.comparison?.id)
  const staleComparison = form.comparison && currentComparison?.version !== form.comparison.version
  const comparisonValue = form.comparison ? `${form.comparison.id}@${form.comparison.version}` : ''
  return <section className="panel" role="region" aria-label="Signaldokument importieren">
    <SectionHead eyebrow="TATSÄCHLICHE DATEI · KEINE KI-ANALYSE" title="Signaldokument on demand einlesen" />
    <p className="muted">Nur UTF-8-.txt, maximal 128 KiB und 30000 Textzeichen. Originalbytes bleiben unverändert; ein UTF-8-BOM gehört zum Original, nicht zum gelesenen Text. Keine PDF-/Office-Extraktion oder OCR.</p>
    <p>Nur synthetische oder ausdrücklich nutzbare öffentliche Vergleichsdokumente. Keine echten Patienten-/Klinikdaten, Beschwerde- oder Expositionsimporte. Der Import erstellt keine Fallakte, keine Analyse und keine Freigabe.</p>
    {error && <ErrorBanner message={error} />}
    <details><summary>Dokument auswählen und Aufnahmekontext bestätigen</summary><form onSubmit={event => void submit(event)}>
      <fieldset disabled={!!busy}>
        <legend>Originaldatei und ausdrücklich bestätigter Aufnahmekontext</legend>
        <label className="field">Originaldatei (.txt)<input type="file" accept=".txt,text/plain" required onChange={event => void selectFile(event.target.files?.[0])} /></label>
        {file && <details><summary>Gelesenen Originaltext vor Aufnahme prüfen</summary><p className="mono">{file.name} · {file.length} Bytes · SHA-256 {file.hash}</p><pre className="report-preview">{file.text}</pre></details>}
        <label className="field">Bestätigtes Vergleichsprodukt<select required value={form.productId} onChange={event => change({ productId: event.target.value, comparison: null })}>
          <option value="">Produkt ausdrücklich wählen</option>{products.map(product => <option key={product.id} value={product.id}>{product.name}</option>)}</select></label>
        <label className="field">Dokumenttitel<input required minLength={5} maxLength={200} value={form.title} onChange={event => change({ title: event.target.value })} /></label>
        <label className="field">Externe Dokument-/Quellreferenz<input required minLength={3} maxLength={100} value={form.externalId} onChange={event => change({ externalId: event.target.value })} /></label>
        <label className="field">Erlaubter Dokumentumfang<select required value={form.scope} onChange={event => change({ scope: event.target.value, originUri: '' })}>
          <option value="">Umfang ausdrücklich wählen</option><option value="Synthetic">Synthetisches Demonstrationsdokument</option><option value="PublicAuthorized">Öffentliches Dokument mit ausdrücklich geklärten Nutzungsrechten</option></select></label>
        {form.scope === 'PublicAuthorized' && <label className="field">Öffentliche Originaladresse (kein Abruf)<input type="url" required maxLength={2000} value={form.originUri} onChange={event => change({ originUri: event.target.value })} /><small>Stabile HTTPS-Adresse ohne Zugangsdaten, Query oder Fragment. Die Adresse wird nicht aufgerufen.</small></label>}
        <label className="field">Herkunft und Übertragungsweg<textarea required minLength={40} maxLength={4000} value={form.provenance} onChange={event => change({ provenance: event.target.value })} /><small>Synthetische Herkunft ausdrücklich mit „SYNTHETISCH:“ beginnen.</small></label>
        <label className="field">Konkrete Nutzungsrechte und erlaubter Umfang<textarea required minLength={40} maxLength={4000} value={form.rights} onChange={event => change({ rights: event.target.value })} /></label>
        <label className="field">Frühere Organisationskenntnis dieses Signals<select required value={form.knowledgeStatus} onChange={event => change({ knowledgeStatus: event.target.value, firstKnownAt: '' })}>
          <option value="">Kenntnis ausdrücklich einordnen</option><option value="Known">Früherer Kenntniseingang ist belegt</option><option value="NotEstablished">Früherer Kenntniseingang ist nicht belegt</option></select></label>
        {form.knowledgeStatus === 'Known' && <label className="field">Belegter Kenntniszeitpunkt (lokale Browserzeit)<input type="datetime-local" required value={form.firstKnownAt} onChange={event => change({ firstKnownAt: event.target.value })} /></label>}
        <label className="field">Beleg oder offene Grenze der früheren Kenntnis<textarea required minLength={40} maxLength={4000} value={form.knowledgeReason} onChange={event => change({ knowledgeReason: event.target.value })} /><small>Dateidatum und Uploadzeit ersetzen keine frühere Kenntnis. Ein konkreter eigener Vorfall muss separat im bestehenden Fall-/Awarenesspfad bewertet werden; keine Frist endet durch diesen Import.</small></label>
        <label className="field">Optionaler Vergleichsfall (keine Vorfallzuordnung)<select value={comparisonValue} onChange={event => {
          const value = cases.find(candidate => `${candidate.id}@${candidate.version}` === event.target.value)
          change({ comparison: value ? { id: value.id, version: value.version, reference: value.reference } : null })
        }}><option value="">Bewusst ohne Fallbezug</option>
          {staleComparison && <option value={comparisonValue} disabled>Veraltete Fassung {form.comparison?.reference}@{form.comparison?.version} – neu wählen</option>}
          {cases.filter(value => value.productId === form.productId).map(value => <option key={value.id} value={`${value.id}@${value.version}`}>{value.reference}@{value.version} · {value.title}</option>)}</select></label>
        {form.comparison && <label className="field">Vergleichsbezug, keine Vorfallidentität<textarea required minLength={40} maxLength={4000} value={form.comparisonReason} onChange={event => change({ comparisonReason: event.target.value })} /></label>}
        <label className="field">Begründete Signalaufnahme<textarea required minLength={40} maxLength={4000} value={form.reason} onChange={event => change({ reason: event.target.value })} /></label>
        <p><label><input type="checkbox" required checked={form.metadataConfirmed} onChange={event => change({ metadataConfirmed: event.target.checked }, true)} /> Produkt, Umfang, Vergleichsbezug und frühere Kenntnis ausdrücklich bestätigt</label></p>
        <p><label><input type="checkbox" required checked={form.rightsConfirmed} onChange={event => change({ rightsConfirmed: event.target.checked }, true)} /> Nutzungsrechte für diesen Inhalt und diese Pilotverwendung bestätigt</label></p>
        <p><label><input type="checkbox" required checked={form.contentEligibilityConfirmed} onChange={event => change({ contentEligibilityConfirmed: event.target.checked }, true)} /> Nur synthetischer oder zulässiger öffentlicher Inhalt; keine Patienten-/Klinikdaten oder Zugangsdaten</label></p>
        <button className="button primary" disabled={!file || !!result || !!staleComparison}>Original sichern und Quellenreview anlegen</button>
      </fieldset>
    </form></details>
    {busy && <p role="status">{busy}</p>}
    {result && <div role="status" className="callout">
      <b>{result.reused ? 'Vorhandene Importquittung wiederverwendet' : 'Datei tatsächlich eingelesen und als Quellenreview gespeichert'}</b>
      <p>Systemeingang {date(result.source.at, true)} · keine KI-Analyse, kein Replay-Ersatz, keine Fallfreigabe. Medical/RA können den Quellenreview prüfen; eine Evidenzeinordnung ist anschließend ein eigener Medical-Schritt.</p>
      <p className="mono">{result.source.id}@{result.source.version} · Original SHA-256 {result.source.document.original.hash}</p>
      <DocumentMatches matches={result.matches} />
    </div>}
  </section>
}
