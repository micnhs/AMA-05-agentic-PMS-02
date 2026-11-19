import { useEffect, useRef, useState } from 'react'
import { api, downloadApi } from './api'
import { DocumentMatches } from './SignalDocumentImport'
import type { Actor, SignalDecision, SignalView } from './types'
import { Badge, date, Empty, ErrorBanner, Loading, roleNames, SectionHead, type Dialog } from './ui'

type Props = {
  actor: Actor; mode: 'rejected' | 'inbox' | 'review'; revision: number; reviewItems: SignalView[];
  openDialog: (dialog: Dialog) => void; mutate: (path: string, body: unknown) => Promise<void>;
  reopen: (signal: SignalView, reason: string) => Promise<void>; openCase: (id: string) => void;
}

export function signalPath(signal: SignalView, action: string) {
  return `/signals/${signal.ownerKind}/${encodeURIComponent(signal.ownerId)}/${action}`
}

function Origin({ decision }: { decision?: SignalDecision }) {
  if (!decision) return <p>Herkunft, Zeitpunkt und Begründung der Ablehnung sind nicht dokumentiert.</p>
  return <><p><b>{decision.origin === 'Agent' ? 'Agent' : decision.origin === 'Reviewer' ? 'Reviewer' : 'Herkunft unbekannt'}</b>
    {' · '}{decision.actorName} · {roleNames[decision.role] ?? decision.role} · {date(decision.at, true)}</p>
    <p>{decision.reason}</p>
    {decision.origin === 'Agent' && <p className="muted">Lauf {decision.agentRunId ?? 'nicht belegt'} · {decision.agentMode ?? 'Modus unbekannt'} · {decision.model ?? 'Modell unbekannt'} · {decision.promptVersion ?? 'Vertrag unbekannt'}</p>}
    <small>Identität: {decision.actorId} · Aktenversion {decision.ownerVersion}</small></>
}

export default function SignalWorkspace({ actor, mode, revision, reviewItems, openDialog, mutate, reopen, openCase }: Props) {
  const [data, setData] = useState<SignalView[] | null>(null)
  const [error, setError] = useState('')
  const downloads = useRef(new Set<AbortController>())
  useEffect(() => {
    const pending = downloads.current
    return () => { pending.forEach(controller => controller.abort()); pending.clear() }
  }, [])
  useEffect(() => {
    if (mode === 'review') return
    let active = true
    setData(null); setError('')
    api<SignalView[]>(`/signals?status=${mode === 'rejected' ? 'Rejected' : 'All'}`)
      .then(items => { if (active) setData(items) }).catch(e => { if (active) setError(e.message) })
    return () => { active = false }
  }, [mode, revision])
  const items = mode === 'review' ? reviewItems.filter(s => s.ownerKind === 'source')
    : data?.filter(s => mode !== 'inbox' || s.ownerKind === 'case' || s.document)
  const canReview = actor.roles.some(role => ['MedicalSafetyReviewer', 'RegulatoryAffairs'].includes(role))
  const title = mode === 'rejected' ? 'Aktuell abgelehnte Signale' : mode === 'review' ? 'Quellenreviews ohne Fallakte' : 'Signaltriage der Originaleingänge'
  async function original(signal: SignalView) {
    if (!signal.document) return
    const controller = new AbortController()
    downloads.current.add(controller); setError('')
    try {
      await downloadApi(`/sources/${encodeURIComponent(signal.ownerId)}/original?version=${signal.ownerVersion}`,
        signal.document.name, { signal: controller.signal })
    } catch (e) {
      if (!controller.signal.aborted) setError(e instanceof Error ? e.message : 'Originalabruf nicht bestätigt.')
    } finally { downloads.current.delete(controller) }
  }
  function approveDocument(signal: SignalView) {
    openDialog({
      title: 'Importdokument als Vergleichsevidenz einordnen',
      description: 'Eigene medizinische Quellenprüfung: tatsächlichen Text, Übertragbarkeit, Nutzungsrechte und Grenzen begründen. ' +
        'Die Relevanzbestätigung ersetzt diese Prüfung nicht. Keine Fallverknüpfung, Friständerung oder Kausalitätsfreigabe; diese bleiben separate bestehende Schritte.',
      fields: [{ name: 'reason', label: 'Medizinische Einordnung und Grenzen', type: 'textarea', minLength: 40 }],
      submitLabel: 'Vergleichsevidenz freigeben',
      onSubmit: values => mutate(`/sources/${encodeURIComponent(signal.ownerId)}/approve`,
        { expectedVersion: signal.ownerVersion, externalId: signal.sourceId, reason: values.reason }),
    })
  }
  function decide(signal: SignalView, action: 'reject' | 'reopen' | 'confirm-relevance') {
    const reopening = action === 'reopen'
    openDialog({
      title: reopening ? 'Signal direkt ins Review aufnehmen' : action === 'reject' ? 'Signal begründet ablehnen' : 'Quellenrelevanz menschlich bestätigen',
      description: `${signal.title} · Aktenversion ${signal.ownerVersion}. ${signal.boundary} ` +
        (reopening ? 'Sofortiger menschlicher Review ohne KI-Auftrag und ohne zusätzliche zweite Freigabe für diese Wiederaufnahme.' : ''),
      submitLabel: reopening ? 'Jetzt ins Review' : action === 'reject' ? 'Signal ablehnen' : 'Relevanz bestätigen',
      fields: [{ name: 'reason', label: 'Fachliche Signalbegründung', type: 'textarea', minLength: 40 }],
      onSubmit: values => reopening ? reopen(signal, values.reason)
        : mutate(signalPath(signal, action), { expectedVersion: signal.ownerVersion, sourceId: signal.sourceId, reason: values.reason }),
    })
  }
  return <section className="panel" role="region" aria-label={title}>
    <SectionHead eyebrow="SIGNALENTSCHEIDUNGEN · KEINE MELDEFREIGABE" title={title} />
    <p className="muted">Agenten-Triage und menschliche Ablehnung sind getrennt erkennbar. Meldungsrückgaben, Nichtmeldepflicht und Simulator-Ablehnungen gehören nicht in diese Liste. Frühere Entscheidungen bleiben erhalten.</p>
    {error && <ErrorBanner message={error} />}
    {!items && !error ? <Loading /> : items?.length === 0 ? <Empty title={mode === 'rejected' ? 'Keine aktuell abgelehnten Signale' : 'Keine Signalvorgänge in dieser Auswahl'}>Aus alten Statuswerten wird keine Ablehnungsherkunft abgeleitet.</Empty> : items?.map(signal => {
      const last = signal.history.at(-1)
      const rejection = last?.action === 'Reject' ? last : undefined
      return <article className="history-entry" key={`${signal.ownerKind}:${signal.ownerId}:${signal.sourceId}`} data-testid={`signal-${signal.ownerId}-${signal.sourceId}`}>
        <div className="row-between"><h3>{signal.title}</h3><Badge tone={signal.status === 'Rejected' ? 'amber' : 'blue'}>
          {signal.status === 'Rejected' ? rejection?.origin === 'Agent' ? 'Agenten-Ablehnung' : rejection?.origin === 'Reviewer' ? 'Reviewer-Ablehnung' : 'Ablehnung · Herkunft unbekannt'
            : signal.status === 'ReviewRequired' ? signal.document && !signal.history.length ? 'Dokumentimport · Quellenreview erforderlich' : 'Zum Review wiederaufgenommen' : signal.status === 'Relevant' ? 'Relevanz menschlich bestätigt' : 'Noch keine Signalentscheidung'}
        </Badge></div>
        <p>{signal.productId} · {signal.caseReference ?? signal.reviewReference ?? 'Öffentliche Vergleichsquelle ohne Fallakte'} · Quelle {signal.sourceReference}</p>
        {signal.status === 'Rejected' ? <Origin decision={rejection} /> : last && <Origin decision={last} />}
        <p className="muted">{signal.boundary}</p>
        <div className="row">
          {canReview && signal.status === 'Rejected' && <button className="button primary" onClick={() => decide(signal, 'reopen')}>Wiederaufnehmen</button>}
          {canReview && signal.status !== 'Rejected' && <button className="button secondary" onClick={() => decide(signal, 'reject')}>Signal ablehnen</button>}
          {canReview && signal.status === 'ReviewRequired' && <button className="button secondary" onClick={() => decide(signal, 'confirm-relevance')}>Relevanz prüfen und bestätigen</button>}
          {signal.document && <button className="button secondary" onClick={() => void original(signal)}>Unveränderte Originaldatei herunterladen</button>}
          {signal.document && signal.status === 'Relevant' && actor.roles.includes('MedicalSafetyReviewer') &&
            <button className="button secondary" onClick={() => approveDocument(signal)}>Importdokument als Vergleichsevidenz einordnen</button>}
          {signal.caseId && <button className="button secondary" onClick={() => openCase(signal.caseId!)}>Zugehörige Fallakte öffnen</button>}
        </div>
        {signal.document && <><p><b>{signal.document.name}</b> · {signal.document.scope === 'Synthetic' ? 'Synthetisches Dokument' : 'Öffentliches Dokument · Nutzungsrechte menschlich erklärt'} · {signal.document.original.length} Originalbytes · {signal.document.extractionVersion}</p>
          <p>Keine KI-Analyse. Bestätigung durch {signal.document.confirmedBy}; Metadaten sind keine medizinische Freigabe.</p>
          {signal.status === 'ReviewRequired' && <p>Nach der Relevanzbestätigung ist die getrennte medizinische Evidenzeinordnung in der Signal Inbox verfügbar.</p>}
          <DocumentMatches matches={signal.documentMatches ?? []} /></>}
        <details><summary>Originalquelle und Entscheidungsverlauf</summary>
          <p>Systemeingang: {date(signal.receivedAt, true)} · Frühere Organisationskenntnis: {signal.awarenessAt ? date(signal.awarenessAt, true) : 'nicht belegt; Abrufdatum ist keine Ereigniskenntnis'}</p>
          <p className="mono">{signal.sourceUri}<br />SHA-256 {signal.sourceHash}</p>
          {signal.document && <><p><b>Herkunft:</b> {signal.document.provenance}</p><p><b>Nutzungsrechte:</b> {signal.document.rights}</p>
            <p><b>Frühere Kenntnis:</b> {signal.document.knowledgeStatus === 'Known' ? 'belegt angegeben' : 'ausdrücklich nicht belegt'} · {signal.document.knowledgeReason}</p>
            <p className="mono">Text-SHA-256 {signal.document.text.hash} · {signal.document.text.length} UTF-8-Bytes ohne BOM</p>
            {signal.document.comparisonCase ? <p><b>Nur Vergleichsbezug:</b> {signal.document.comparisonCase.reference}@{signal.document.comparisonCase.source.version} · {signal.document.comparisonCase.reason}</p>
              : <p>Bewusst ohne Fallbezug aufgenommen; kein eigener Herstellerfall.</p>}</>}
          <pre className="report-preview">{signal.content}</pre>
          {signal.history.map((decision, index) => <div className="history-entry" key={index}><b>{decision.action === 'Reject' ? 'Abgelehnt' : decision.action === 'Reopen' ? 'Wiederaufgenommen' : 'Relevanz bestätigt'}</b><Origin decision={decision} /><small className="mono">{decision.sourceHash}</small></div>)}
        </details>
      </article>
    })}
  </section>
}
