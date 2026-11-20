import type { Actor, Evidence, MedicalDecision, SafetyCase, SignalReview } from './types'
import { Badge, date, Empty, ErrorBanner, roleNames, type Dialog } from './ui'

type MedicalDisposition = 'Investigate' | 'Monitor' | 'NonReportable'
const dispositionNames: Record<string, string> = {
  Undetermined: 'Noch nicht entschieden', Investigate: 'Weitere Untersuchung',
  Monitor: 'Beobachtung', NonReportable: 'Nichtmeldepflicht', Reportable: 'Meldepflicht',
}
function dispositionName(value: string) {
  return Object.hasOwn(dispositionNames, value) ? dispositionNames[value] : value
}

export function CaseDispositionHistory({ c }: { c: SafetyCase }) {
  const reopenings = c.evidence.filter(e => e.kind === 'Reopening')
  if (!c.decisions.length && !reopenings.length) return null
  return <section className="disposition-history" aria-label="Dispositions- und Wiederaufnahmehistorie">
    <h3>Dispositions- und Wiederaufnahmehistorie</h3>
    <p className="muted">Gespeicherte Entscheidungen und Signaturen, keine aktuelle Freigabezusage. Eine neue Bewertung oder Wiederaufnahme erledigt die frueheren Gruende nicht automatisch.</p>
    {[...c.decisions].reverse().map(decision => {
      const assessments = c.assessments.filter(a => a.version === decision.assessmentVersion)
      const approvals = c.approvals.filter(a => a.role === 'RegulatoryAffairs' && a.scopeHash === decision.scopeHash)
      const exception = decision.expectedSideEffect
      return <article className="history-entry" key={decision.id} data-testid={`disposition-${decision.id}`}>
        <h4>Medical: {dispositionName(decision.disposition)}</h4>
        <p><time dateTime={decision.at}>{date(decision.at, true)}</time> · Gebundenes Assessment v{decision.assessmentVersion}</p>
        <p className="preserved-text">{decision.rationale}</p>
        {exception && <><p><b>Dokumentierte Nebenwirkungsausnahme:</b> {exception.effect}</p>
          <p className="preserved-text">{exception.rationale}</p>
          <details><summary>Gespeicherte Ausnahmebelege</summary>
            <p>Referenzrate: {exception.referenceRate ?? 'nicht quantifiziert'} · Nenner: {exception.denominator}<br />
              Trendregel: {exception.trendPolicyVersion} · Im Trendbericht: {exception.includedInTrendReporting ? 'ja' : 'nein'}</p>
            {([['Produktinformation', exception.productInformation], ['Technische Quantifizierung', exception.technicalQuantification],
              ['Trendbeobachtung', exception.trendMonitoring]] as const).map(([title, citation]) => <div key={title}>
                <b>{title}</b><blockquote className="preserved-text">{citation.quote}</blockquote>
                <p className="mono">{citation.evidenceId} · {citation.locator}<br />SHA-256 {citation.hash}</p>
              </div>)}
          </details></>}
        <details><summary>Technische Dispositionsbindung</summary>
          <p>Der Entscheidungsvertrag speichert keinen Medical-Anzeigenamen.</p>
          <p className="mono">Entscheidung: {decision.id}<br />Medical-Identitaet: {decision.actorId}<br />
            Originalzeitpunkt: {decision.at}<br />Entscheidungs-Scope: {decision.scopeHash}</p>
          {assessments.length === 1 ? <p className="mono">Gebundener Evidenzsnapshot: {assessments[0].evidenceHash}</p>
            : <p>Der zugehoerige Assessmentstand ist hier nicht eindeutig belegt.</p>}
        </details>
        {approvals.map(approval => <div className="history-entry" key={approval.id}>
          <b>Gespeicherte RA-Signatur: {approval.actorName}</b>
          <p>{date(approval.at, true)} · {approval.meaning}</p>
          <p className="preserved-text">{approval.rationale}</p>
          {approval.actorId === decision.actorId && <ErrorBanner message="Gleiche Identitaet: kein Nachweis einer unabhaengigen RA-Pruefung." />}
          <details><summary>Technische Signaturbindung</summary><p className="mono">
            Signatur: {approval.id}<br />RA-Identitaet: {approval.actorId}<br />Rolle: {approval.role}<br />
            Originalzeitpunkt: {approval.at}<br />Scope: {approval.scopeHash}</p></details>
        </div>)}
      </article>
    })}
    {[...reopenings].reverse().map(e => <article className="history-entry" key={`${e.id}:${e.version}`}>
      <h4>{e.title} · v{e.version}</h4>
      <p>{date(e.recordedAt, true)} · {e.synthetic ? 'Synthetischer Beleg' : 'Gespeicherter Beleg'}</p>
      <p className="preserved-text">{e.content}</p>
      <p className="muted">Aufgezeichnete Wiederaufnahmebegruendung, keine neue Fall- oder Meldefreigabe. Ein Autorname ist in diesem Evidenzvertrag nicht gespeichert.</p>
      <details><summary>Technische Wiederaufnahmebindung</summary><p className="mono">
        {e.id}@{e.version}<br />SHA-256 {e.hash}<br />Herkunft: {e.origin}<br />Fundstelle: {e.locator}</p></details>
    </article>)}
  </section>
}

function SignalHistory({ review }: { review: SignalReview }) {
  return <>{review.history.map((entry, index) => <div className="history-entry" key={index}>
    <b>{entry.action === 'Reject' ? 'Signal abgelehnt' : entry.action === 'Reopen' ? 'Signal wiederaufgenommen'
      : entry.action === 'ConfirmRelevance' ? 'Quellenrelevanz bestaetigt' : entry.action}</b>
    <p>{entry.origin === 'Agent' ? 'Agentenvorschlag' : entry.origin === 'Reviewer' ? 'Menschlicher Review' : entry.origin}
      {' · '}{entry.actorName} · {Object.hasOwn(roleNames, entry.role) ? roleNames[entry.role] : entry.role} · {date(entry.at, true)}</p>
    <p className="preserved-text">{entry.reason}</p>
    <details><summary>Technische Signalentscheidung</summary><p className="mono">
      {review.reviewReference}<br />Quelle: {review.sourceId}<br />Quellen-SHA-256: {entry.sourceHash}<br />
      Identitaet: {entry.actorId}<br />Aktenversion: {entry.ownerVersion}<br />Originalzeitpunkt: {entry.at}</p>
      {entry.origin === 'Agent' && <p className="mono">Lauf: {entry.agentRunId ?? 'nicht belegt'}<br />
        Modus: {entry.agentMode ?? 'nicht belegt'} · Modell: {entry.model ?? 'nicht belegt'} · Vertrag: {entry.promptVersion ?? 'nicht belegt'}</p>}
    </details>
  </div>)}</>
}

export default function CaseDispositionPanel({ c, actor, busy, openDialog, mutate, navigate, openSignals, openEvidence }: {
  c: SafetyCase; actor: Actor; busy: boolean; openDialog: (dialog: Dialog) => void;
  mutate: (path: string, body: unknown) => Promise<void>; navigate: (tab: string) => void;
  openSignals: () => void; openEvidence: (evidence: Evidence) => void;
}) {
  const medical = actor.isHuman && actor.roles.includes('MedicalSafetyReviewer')
  const regulatory = actor.isHuman && actor.roles.includes('RegulatoryAffairs')
  const closed = c.stage === 'Closed'
  const assessment = c.currentAssessment
  const unassessedEvidence = !!assessment && c.evidence.some(e => !assessment.evidenceIds.includes(e.id))
  const decision = c.decisions.at(-1)
  const pendingNonReportable = decision?.disposition === 'NonReportable' && c.disposition === 'Undetermined'
  const decisionVersionMatches = decision?.assessmentVersion === assessment?.version
  const sameReviewer = decision?.actorId === actor.id
  const reviews = c.signalReviews ?? []

  function propose(disposition: MedicalDisposition) {
    openDialog({
      title: disposition === 'NonReportable' ? 'Nichtmeldepflicht medizinisch vorschlagen' : `Fall-Disposition: ${dispositionName(disposition)}`,
      description: `${c.reference}, Fallversion ${c.version}, Assessment v${assessment?.version}. ` +
        (disposition === 'NonReportable'
          ? 'Formeller Nichtmeldeweg, nicht fehlende Relevanz oder eine Datenluecke. Der Server verlangt aktuelle, reviewbare Evidenz ohne offene Major-/Critical-Befunde. Erst eine getrennte RA-Identitaet kann offene Einzelpflichten als NonReportableReviewed einordnen.'
          : 'Begruendete Medical-Entscheidung, keine Nichtmeldepflicht. Fristen laufen weiter; dieser Vertrag hat keine gesonderte RA-Bestaetigung fuer Untersuchung oder Beobachtung.') +
        ' Bestehende Entwurfspakete werden ueberholt; historische Signaturen werden nicht uebernommen.',
      fields: [{ name: 'reason', label: 'Fachliche Dispositionsbegruendung', type: 'textarea', minLength: 40 }],
      submitLabel: 'Medical-Entscheidung speichern',
      onSubmit: values => mutate(`/cases/${encodeURIComponent(c.id)}/disposition`,
        { expectedVersion: c.version, disposition, reason: values.reason }),
    })
  }
  function approve(proposal: MedicalDecision) {
    openDialog({
      title: 'Nichtmeldepflicht unabhaengig pruefen',
      description: `${c.reference}, Fallversion ${c.version}. Medical-Vorschlag an Assessment v${proposal.assessmentVersion}. ` +
        'Der Server prueft Identitaet, aktuellen Evidenzstand und den exakten Entscheidungs-/Fristenscope erneut. ' +
        'Bei Bestaetigung werden offene Einzelpflichten NonReportableReviewed; kein Fallabschluss und keine Meldepaketfreigabe.',
      body: <p className="preserved-text">{proposal.rationale}</p>,
      fields: [{ name: 'reason', label: 'Regulatorische Pruefbegruendung', type: 'textarea', minLength: 40 }],
      submitLabel: 'Nichtmeldepflicht bestaetigen',
      onSubmit: values => mutate(`/cases/${encodeURIComponent(c.id)}/disposition/approve`,
        { expectedVersion: c.version, reason: values.reason }),
    })
  }
  function reopen() {
    openDialog({
      title: 'Abgeschlossenen Fall wiederaufnehmen',
      description: `${c.reference}, Fallversion ${c.version}. Nur fuer abgeschlossene Faelle. ` +
        'Der vorhandene Befehl speichert die Begruendung als neuen synthetischen Wiederaufnahmebeleg und verlangt eine neue aktuelle Bewertung. ' +
        'Er bindet keinen ausgewaehlten neuen Quellenbeleg. Keine automatische Korrelation, kein KI-Auftrag, keine Fristaenderung oder reaktivierte Freigabe.',
      fields: [{ name: 'reason', label: 'Neue Erkenntnisse und Wiederaufnahmebegruendung', type: 'textarea', minLength: 40 }],
      submitLabel: 'Begruendet wiederaufnehmen',
      onSubmit: values => mutate(`/cases/${encodeURIComponent(c.id)}/reopen`,
        { expectedVersion: c.version, reason: values.reason }),
    })
  }
  function signal(source: SafetyCase['sources'][number], action: 'reject' | 'reopen' | 'confirm-relevance') {
    openDialog({
      title: action === 'reopen' ? 'Quellensignal erneut pruefen' : action === 'reject' ? 'Quellensignal begruendet ablehnen' : 'Quellenrelevanz bestaetigen',
      description: `${source.title} · Fallversion ${c.version}. Entscheidung nur ueber dieses vorhandene Signal. ` +
        'Keine Nichtmeldepflicht, Meldungsrueckgabe oder neue Quellenkorrelation. Kenntnis und Fristen bleiben erhalten. ' +
        (action === 'reopen' ? 'Wiederaufnahme geht unmittelbar in den menschlichen Review; laufende Agentenauftraege werden aufgegeben. Neue Bewertung und eigene Freigaben bleiben erforderlich.' +
          (closed ? ' Die abgeschlossene Fallakte wird dabei wieder geoeffnet.' : '') : ''),
      body: <details><summary>Gebundene Originalquelle</summary><p className="mono">{source.id}<br />SHA-256 {source.hash}</p></details>,
      fields: [{ name: 'reason', label: 'Fachliche Signalbegruendung', type: 'textarea', minLength: 40 }],
      submitLabel: action === 'reopen' ? 'Signal zum Review wiederaufnehmen' : action === 'reject' ? 'Signal ablehnen' : 'Relevanz bestaetigen',
      onSubmit: values => mutate(`/signals/case/${encodeURIComponent(c.id)}/${action}`,
        { expectedVersion: c.version, sourceId: source.id, reason: values.reason }),
    })
  }

  return <section className="panel disposition-panel" aria-label="Fall-Disposition und Neubewertung">
    <h2>Fall-Disposition & Neubewertung</h2>
    <p>Gespeicherter Dispositionswert: <b>{dispositionName(c.disposition)}</b> · Fallversion {c.version}
      {c.closedAt && <> · Abschluss aufgezeichnet: {date(c.closedAt, true)}</>}</p>
    <p className="muted">Der gespeicherte Wert kann nach neuer Evidenz fortbestehen. Er ist allein kein Nachweis einer aktuellen Freigabe; die serverseitige Prozessansicht und erneute Bindungspruefung bleiben massgeblich.</p>
    <div className="callout"><b>Fehlende Relevanz ist keine Nichtmeldepflicht.</b>
      <p>Die Gruende „derzeit nicht relevant“ und „Beweislage noch unzureichend“ sind im vorhandenen Fallvertrag nicht getrennt codiert.
        Keine Ersatzstatus oder automatische Zuordnung zu Beobachtung, Untersuchung oder Nichtmeldepflicht. Eine Meldungsrueckgabe korrigiert nur ein Paket.</p>
    </div>
    {(!assessment || unassessedEvidence) && <p role="status">Eine aktuelle menschliche Bewertung ist erforderlich: {assessment ? 'Neue Beleg-IDs sind noch nicht im Assessment enthalten.' : 'Noch kein Assessment vorhanden.'} Auch unveraenderte Beleg-IDs beweisen keinen aktuellen Evidenzhash.</p>}
    <div className="row">
      <button className="button secondary" onClick={() => navigate('analysis')}>Fallbelege und Bewertung pruefen</button>
      <button className="button secondary" onClick={() => navigate('information')}>Gezielte Nachforderung oeffnen</button>
      <button className="button secondary" onClick={openSignals}>Signal Inbox oeffnen</button>
    </div>
    <h3>Medizinische Fall-Disposition</h3>
    <p>Medical kann den aktuellen Fall begruendet zur weiteren Untersuchung oder Beobachtung einordnen. Beides laesst Pflichten offen.
      Formelle Nichtmeldepflicht braucht dagegen einen medizinischen Vorschlag und eine unabhaengige RA-Pruefung.</p>
    {medical && !closed && <div className="row">
      <button className="button secondary" disabled={busy || !assessment || unassessedEvidence} onClick={() => propose('Investigate')}>Weitere Untersuchung begruenden</button>
      <button className="button secondary" disabled={busy || !assessment || unassessedEvidence} onClick={() => propose('Monitor')}>Beobachtung begruenden</button>
      <button className="button secondary" disabled={busy || !assessment || unassessedEvidence} onClick={() => propose('NonReportable')}>Nichtmeldepflicht vorschlagen</button>
    </div>}
    {pendingNonReportable && !closed && <div className="callout">
      <b>Gespeicherter Medical-Vorschlag zur Nichtmeldepflicht; unabhaengige RA-Pruefung offen.</b>
      <p className="preserved-text">{decision.rationale}</p>
      {(!decisionVersionMatches || unassessedEvidence) && <p>Assessment-/Belegstand hat sich geaendert. Medical muss die aktuelle Grundlage und einen neuen Vorschlag pruefen.</p>}
      {sameReviewer && <p>Die vorschlagende Identitaet darf nicht selbst als RA bestaetigen, auch nicht mit einer zweiten Rolle.</p>}
      {regulatory && <button className="button secondary" disabled={busy || sameReviewer || !assessment || !decisionVersionMatches || unassessedEvidence}
        onClick={() => approve(decision)}>Nichtmeldepflicht unabhaengig pruefen</button>}
    </div>}
    {closed && <div className="callout"><b>Fall abgeschlossen: neue Erkenntnisse ausdruecklich pruefen.</b>
      <p>Die Fall-Wiederaufnahme bindet derzeit nur ihre Begruendung als Evidenz, keine ausgewaehlte neue Quellversion.
        Danach freigegebene Evidenz konkret verknuepfen und Medical neu bewerten lassen. Keine automatische Fristpause oder neue Frist.</p>
      {(medical || regulatory) && <button className="button secondary" disabled={busy} onClick={reopen}>Abgeschlossenen Fall wiederaufnehmen</button>}
    </div>}
    {!medical && !regulatory && <p className="muted">Dieses Konto liest Dispositionen und Historie. Diese Schreibbefehle brauchen eine menschliche Medical- oder RA-Berechtigung.</p>}
    <CaseDispositionHistory c={c} />
    <h3>Vorhandene Quellen und Signalreviews</h3>
    <p>Nur gespeicherte Fallquellen und deren tatsaechliche Zuordnung. Neue Signale zuerst in der Signal Inbox pruefen;
      geeignete freigegebene Produkt-Evidenz danach unter Analyse & Evidenz verknuepfen. Ein neuer Signal-zu-Fall-Korrelationsbefehl fehlt hier.</p>
    {!c.sources.length && <Empty title="Keine gespeicherte Fallquelle">Keine Korrelation wird aus Titel oder Aehnlichkeit abgeleitet.</Empty>}
    {c.sources.map(source => {
      const matches = reviews.filter(review => review.sourceId === source.id)
      const bound = matches.length <= 1 && matches.every(review => review.sourceHash === source.hash)
      const review = bound ? matches[0] : undefined
      const originals = c.evidence.filter(e => e.id === source.id && e.hash === source.hash)
      return <article className="history-entry" key={source.id} data-testid={`case-signal-${source.id}`}>
        <h4>{source.title}</h4><p className="preserved-text">Gespeicherter Zuordnungsgrund: {source.matchReason}</p>
        <p>Originalkenntnis: {date(source.awarenessAt, true)} · Systemeingang: {date(source.receivedAt, true)}</p>
        {!bound ? <ErrorBanner message="Signalreview passt nicht eindeutig zum Originalquellen-Hash. Keine Signalaktion moeglich." />
          : <Badge tone={review?.status === 'Rejected' ? 'amber' : 'neutral'}>{review?.status === 'Rejected' ? 'Signal abgelehnt'
            : review?.status === 'ReviewRequired' ? 'Quellenreview erforderlich' : review?.status === 'Relevant' ? 'Quellenrelevanz bestaetigt'
              : review?.status ?? 'Keine dokumentierte Signalentscheidung'}</Badge>}
        <div className="row">
          {originals.length === 1 && <button className="button secondary" onClick={() => openEvidence(originals[0])}>Originalbeleg oeffnen</button>}
          {bound && (medical || regulatory) && (review?.status === 'Rejected'
            ? <button className="button secondary" disabled={busy} onClick={() => signal(source, 'reopen')}>Quellensignal erneut pruefen</button>
            : <button className="button secondary" disabled={busy} onClick={() => signal(source, 'reject')}>Quellensignal ablehnen</button>)}
          {bound && (medical || regulatory) && review?.status === 'ReviewRequired' &&
            <button className="button secondary" disabled={busy} onClick={() => signal(source, 'confirm-relevance')}>Quellenrelevanz bestaetigen</button>}
        </div>
        <details><summary>Technische Originalquellenbindung</summary><p className="mono">
          {source.id} · {source.externalId}<br />Art: {source.kind}<br />{source.uri}<br />SHA-256 {source.hash}</p></details>
        {matches.map((item, index) => <SignalHistory review={item} key={index} />)}
      </article>
    })}
    {reviews.filter(review => !c.sources.some(source => source.id === review.sourceId)).map((review, index) => <article className="history-entry" key={index}>
      <ErrorBanner message="Historischer Signalreview ohne zugeordnete Originalquelle in dieser Fallansicht; nur lesbar." /><SignalHistory review={review} />
    </article>)}
  </section>
}
