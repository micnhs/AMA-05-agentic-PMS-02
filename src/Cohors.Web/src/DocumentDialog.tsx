import { useEffect, useId, useRef, useState } from 'react'
import { FileText, X } from 'lucide-react'
import { api } from './api'
import type { Claim, Evidence, EvidenceDocument } from './types'
import { Badge, date, ErrorBanner, EvidenceCategory, Loading } from './ui'

export type DocumentRequest = { evidence: Evidence; path: string; context: string; claim?: Claim }

export default function DocumentDialog({ request, close }: { request: DocumentRequest; close: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null)
  const titleId = useId()
  const descriptionId = useId()
  const [loaded, setDocument] = useState<EvidenceDocument | null>(null)
  const [error, setError] = useState('')
  const document = loaded?.evidence.id === request.evidence.id && loaded.evidence.version === request.evidence.version &&
    loaded.evidence.hash === request.evidence.hash ? loaded : null
  useEffect(() => {
    const element = dialog.current!
    const opener = window.document.activeElement
    element.showModal()
    return () => {
      element.close()
      if (opener instanceof HTMLElement && opener.isConnected) opener.focus()
    }
  }, [])
  useEffect(() => {
    let active = true
    setDocument(null); setError('')
    api<EvidenceDocument>(request.path).then(result => {
      if (!active) return
      if (result.evidence?.id !== request.evidence.id || result.evidence.version !== request.evidence.version ||
        result.evidence.hash !== request.evidence.hash || result.hashVerified !== true ||
        result.evidence.kind !== request.evidence.kind || result.evidence.title !== request.evidence.title ||
        result.evidence.locator !== request.evidence.locator ||
        result.content !== result.evidence.content) throw new Error('Die Antwort bestätigt nicht die ausgewählte Dokumentfassung. Kein verifizierter Inhalt wird angezeigt.')
      setDocument(result)
    }).catch(error => { if (active) setError(error instanceof Error ? error.message : 'Dokumentinhalt konnte nicht verifiziert werden.') })
    return () => { active = false }
  }, [request])
  const evidence = request.evidence
  return <dialog ref={dialog} className="dialog document-dialog" aria-labelledby={titleId} aria-describedby={descriptionId}
    onCancel={event => { event.preventDefault(); close() }}>
    <header><div><span className="eyebrow">GESPEICHERTE EVIDENZ · VERSION {evidence.version}</span>
      <h2 id={titleId}><FileText size={22} aria-hidden="true" />{evidence.title}</h2></div>
      <button type="button" className="icon-button" onClick={close} aria-label="Dokument schließen"><X /></button></header>
    <p id={descriptionId}>{request.context}. Gespeicherter Textinhalt beziehungsweise Auszug, keine vollständige Original-PDF- oder Word-Datei.</p>
    <div className="document-meta"><span><b>Dokumentenkategorie</b><EvidenceCategory kind={evidence.kind} /></span>
      <span><b>Ausgewählte Version</b>v{evidence.version} · {evidence.productId}</span>
      <span><b>Fundstelle</b>{evidence.locator}</span><span><b>Erfasst / fachlich gültig ab</b>{date(evidence.recordedAt, true)} / {date(evidence.validFrom, true)}</span>
      <span><b>Herkunft</b>{evidence.origin}</span></div>
    <div className="row document-integrity" aria-live="polite"><Badge tone={document ? 'blue' : error ? 'danger' : 'amber'}>
      {document ? 'Ausgewählte Textfassung · Original-Hash verifiziert' : error ? 'Nicht verifiziert' : 'Originalprüfung läuft'}
    </Badge><Badge>{evidence.synthetic ? 'Synthetische Evidenz' : 'Öffentlicher Text / Metadaten'}</Badge></div>
    <p className="mono document-hash">Referenzhash: {evidence.hash}</p>
    {request.claim && <details className="document-claim" open><summary>Claim {request.claim.id} und seine Fundstellen</summary><p>{request.claim.text}</p>
      {request.claim.citations.filter(c => c.evidenceId === evidence.id && c.hash === evidence.hash).map((c, i) =>
        <blockquote key={i}><b>{c.locator}</b><br />{c.quote}</blockquote>)}</details>}
    {error ? <ErrorBanner message={error} /> : document ? <section className="document-content" aria-label="Verifizierter Dokumenttext" tabIndex={0}>
      <pre>{document.content}</pre></section> : <Loading />}
    <footer><button type="button" className="button secondary" onClick={close}>Dokument schließen</button></footer>
  </dialog>
}
