import { useEffect, useId, useRef, useState } from 'react'
import { X } from 'lucide-react'
import { api } from './api'
import type { Approval } from './types'
import { Badge, date, ErrorBanner, Loading, roleNames } from './ui'

export interface DispatchBinding {
  route: string; aggregateId: string; aggregateVersion: number; packageVersion: number
  scopeHash: string; payloadHash: string; ledgerVersion: number | null
}
interface DispatchItem { name: string; kind: string; hash: string; sourceId: string; sourceVersion: number | null }
interface DispatchPreview {
  binding: DispatchBinding; productId: string; businessRecipient: string; technicalRecipient: string
  jurisdiction: string; countries: string[]; channel: string; environment: string; language: string
  narrative: string; businessXml: string | null; frozenPayload: string
  originals: DispatchItem[]; attachments: DispatchItem[]; approvals: Approval[]; boundaries: string[]
}

export default function DispatchPreviewDialog({ path, expected, scenarios, reasonLength, confirm, close }: {
  path: string; expected: Omit<DispatchBinding, 'payloadHash'>; scenarios: { value: string; label: string }[]
  reasonLength: number; confirm: (binding: DispatchBinding, scenario: string, reason: string) => Promise<void>; close: () => void
}) {
  const dialog = useRef<HTMLDialogElement>(null)
  const attempted = useRef(false)
  const titleId = useId()
  const [preview, setPreview] = useState<DispatchPreview | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [finishedAttempt, setFinishedAttempt] = useState(false)
  const { route, aggregateId, aggregateVersion, packageVersion, scopeHash, ledgerVersion } = expected
  useEffect(() => {
    const element = dialog.current!
    const opener = document.activeElement
    element.showModal()
    return () => { element.close(); if (opener instanceof HTMLElement && opener.isConnected) opener.focus() }
  }, [])
  useEffect(() => {
    let active = true
    setPreview(null); setError('')
    api<DispatchPreview>(path).then(value => {
      if (!active) return
      const b = value.binding
      if (b.route !== route || b.aggregateId !== aggregateId || b.aggregateVersion !== aggregateVersion ||
        b.packageVersion !== packageVersion || b.scopeHash !== scopeHash || b.ledgerVersion !== ledgerVersion)
        throw new Error('Die Antwort ist nicht die ausgewählte freigegebene Paketfassung. Kein Versand.')
      setPreview(value)
    }).catch(e => { if (active) setError(e instanceof Error ? e.message : 'Vorschau nicht bestätigt.') })
    return () => { active = false }
  }, [path, route, aggregateId, aggregateVersion, packageVersion, scopeHash, ledgerVersion])
  return <dialog ref={dialog} className="dialog document-dialog" aria-labelledby={titleId}
    onCancel={event => { event.preventDefault(); if (!busy) close() }}>
    <header><div><span className="eyebrow">ABSCHLIESSENDE RA-VERSANDVORSCHAU</span><h2 id={titleId}>Genau dieses Fachpaket</h2></div>
      <button type="button" className="icon-button" disabled={busy} onClick={close} aria-label="Ohne Versand schließen"><X /></button></header>
    <p>Lesen und Abbrechen lösen weder einen Versand noch eine Reservierung aus. Erst der ausdrückliche Abschluss beauftragt den Simulator.</p>
    {error && <ErrorBanner message={error} />}
    {!preview && !error && <Loading />}
    {preview && <>
      <Badge tone="amber">{preview.environment} · KEIN BEHÖRDENVERSAND</Badge>
      <dl className="document-meta">
        <div><dt>Paket / Datensatz</dt><dd>{aggregateId} · Paket v{packageVersion} · Datensatz v{aggregateVersion}</dd></div>
        <div><dt>Fachlicher Empfänger (gebundenes Profil)</dt><dd>{preview.businessRecipient}</dd></div>
        <div><dt>Technischer Empfänger</dt><dd>{preview.technicalRecipient}</dd></div>
        <div><dt>Jurisdiktion / Paket- oder Quellländer</dt><dd>{preview.jurisdiction} / {preview.countries.join(', ')}</dd></div>
        <div><dt>Kanal / Umgebung</dt><dd>{preview.channel} / {preview.environment}</dd></div>
        <div><dt>Produkt / Sprache</dt><dd>{preview.productId} / {preview.language}</dd></div>
      </dl>
      <p className="mono document-hash">Freigabe-Scope SHA256: {scopeHash}<br />Original-Payload SHA256: {preview.binding.payloadHash}</p>
      <h3>Freigegebener Fachinhalt</h3><div className="document-content"><pre>{preview.narrative}</pre></div>
      <Items title="Gebundene Original- und Quellenbezüge" values={preview.originals} />
      <Items title="Enthaltene Anhänge" values={preview.attachments} />
      {preview.businessXml && <details><summary>Feststehendes Business-/MIR-XML (keine AS4-Hülle)</summary>
        <div className="document-content"><pre>{preview.businessXml}</pre></div></details>}
      <details><summary>Vollständiger eingefrorener Original-Payload</summary><div className="document-content"><pre>{preview.frozenPayload}</pre></div></details>
      <h3>Freigaben dieser Fassung</h3>{preview.approvals.map(a => <p key={a.id}>{roleNames[a.role] ?? a.role}: {a.actorName} · {date(a.at, true)}<br />{a.rationale}</p>)}
      <h3>Fachpaket und Transportgrenze</h3>{preview.boundaries.map(text => <p key={text} className="muted">{text}</p>)}
    </>}
    <form onSubmit={async event => {
      event.preventDefault()
      if (!preview || attempted.current) return
      const form = new FormData(event.currentTarget)
      attempted.current = true; setBusy(true); setError('')
      try { await confirm(preview.binding, String(form.get('scenario')), String(form.get('reason'))); close() }
      catch (e) { setError(e instanceof Error ? e.message : 'Abschluss nicht bestätigt.'); setFinishedAttempt(true) }
      finally { setBusy(false) }
    }}>
      <fieldset disabled={!preview || busy || finishedAttempt}>
        <label className="field">Explizites Simulatorszenario<select name="scenario" required defaultValue="">
          <option value="">Szenario bewusst auswählen</option>{scenarios.map(s => <option key={s.value} value={s.value}>{s.label}</option>)}</select></label>
        <label className="field">Fachliche Abschlussbegründung<textarea name="reason" required minLength={reasonLength} maxLength={4000} rows={3} /></label>
        <label><input type="checkbox" required /> Ich bestätige die angezeigte Paketfassung, ihre Anhänge und das simulierte Ziel.</label>
      </fieldset>
      {finishedAttempt && <p className="callout">Kein blinder Wiederholungsversuch. Dialog schließen, aktuellen Zustand neu laden und einen unklaren Empfang über den bestehenden Abgleich klären.</p>}
      <footer><button type="button" className="button secondary" disabled={busy} onClick={close}>{finishedAttempt ? 'Schließen und Zustand prüfen' : 'Abbrechen ohne Versand'}</button>
        <button className="button primary" disabled={!preview || busy || finishedAttempt}>{busy ? 'Abschluss wird geprüft …' : 'Genau dieses Paket simuliert senden'}</button></footer>
    </form>
  </dialog>
}

function Items({ title, values }: { title: string; values: DispatchItem[] }) {
  return <section><h3>{title}</h3>{values.length ? <div className="table-scroll"><table className="case-table">
    <thead><tr><th>Name / Art</th><th>Gebundene Quelle</th><th>SHA256</th></tr></thead>
    <tbody>{values.map((v, i) => <tr key={`${v.hash}-${i}`}><td>{v.name}<small className="subline">{v.kind}</small></td>
      <td>{v.sourceId}{v.sourceVersion !== null && `@${v.sourceVersion}`}</td><td className="mono document-hash">{v.hash}</td></tr>)}</tbody>
  </table></div> : <p>Keine Einträge im gebundenen Paket; kein Live-Ersatz.</p>}</section>
}
