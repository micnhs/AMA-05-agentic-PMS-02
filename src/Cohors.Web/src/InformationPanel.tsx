import { ArrowUpRight, Send } from 'lucide-react'
import type { Actor, InformationRecipient, InformationResponse, InfoRequest, SafetyCase } from './types'
import { Badge, date, Empty, hash, label, roleNames, type Dialog, type Field } from './ui'

const channels: Record<string, string> = { Email: 'E-Mail', Phone: 'Telefon', Portal: 'Portal' }
const states: Record<string, string> = {
  Draft: 'Entwurf', Approved: 'Freigegeben - noch nicht versendet', SendPending: 'Versand ausstehend',
  SimulatedSent: 'Im Simulator zugestellt', SendFailed: 'Versand fehlgeschlagen',
  TransmissionUnknown: 'Versand unklar - Abgleich erforderlich', ResponseReceived: 'Antwort eingegangen - Zuordnung offen',
  SimulatedAnswered: 'Antwort zugeordnet',
}
const reason: Field = { name: 'reason', label: 'Fachliche Begruendung', type: 'textarea', minLength: 40 }
const recipientFields = (recipient?: InformationRecipient): Field[] => [
  { name: 'contactId', label: 'Synthetische Kontaktreferenz', value: recipient?.id, minLength: 3 },
  { name: 'organization', label: 'Organisation des Kontakts', value: recipient?.organization, minLength: 3 },
  { name: 'contact', label: 'Benannter Kontakt oder eindeutige Empfangsstelle', value: recipient?.contact, minLength: 3 },
  { name: 'recipientRole', label: 'Rolle des Kontakts', value: recipient?.role, minLength: 3 },
  { name: 'intendedChannel', label: 'Vorgesehener Kontaktkanal (nur simuliert)', type: 'select', value: recipient?.intendedChannel ?? '',
    options: [{ value: '', label: 'Kanal ausdruecklich waehlen' }, ...Object.entries(channels).map(([value, name]) => ({ value, label: name }))] },
  { name: 'destination', label: 'Synthetische Zielreferenz - keine echten Kontaktdaten', value: recipient?.destination, minLength: 3 },
  { name: 'synthetic', label: 'Pilotgrenze bestaetigen', type: 'select', value: '',
    options: [{ value: '', label: 'Ausdrueckliche Bestaetigung erforderlich' }, { value: 'true', label: 'Kontakt und Inhalt sind ausschliesslich synthetisch' }] },
]
const recipientFrom = (v: Record<string, string>): InformationRecipient => ({ id: v.contactId,
  organization: v.organization, contact: v.contact, role: v.recipientRole, intendedChannel: v.intendedChannel,
  destination: v.destination, synthetic: v.synthetic === 'true' })
function Recipient({ value }: { value: InformationRecipient }) {
  return <div className="rfi-recipient"><p><b>{value.contact}</b> · {value.organization}</p>
    <p>{value.role} · {channels[value.intendedChannel] ?? value.intendedChannel} · {value.destination}</p>
    <small>Kontaktreferenz {value.id} · synthetisch · kein reales Kommunikationsziel</small></div>
}

export default function InformationPanel({ value: c, actor, openDialog, mutate, busy }: {
  value: SafetyCase; actor: Actor; openDialog: (dialog: Dialog) => void; mutate: (path: string, body: unknown) => Promise<void>; busy: boolean
}) {
  const medical = actor.roles.includes('MedicalSafetyReviewer')
  const regulatory = actor.roles.includes('RegulatoryAffairs')
  const editor = medical || actor.roles.includes('PmsSpecialist')
  const sender = editor || regulatory
  const approvalRole = medical ? 'MedicalSafetyReviewer' : 'RegulatoryAffairs'
  const responses = c.informationResponses ?? []
  const action = (title: string, path: string, fields: Field[], extra: object = {}, submitLabel?: string) =>
    openDialog({ title, fields, submitLabel,
      description: `${c.reference}, Fallversion ${c.version}. Nur interne Simulation; keine echte Nachricht. Fristen laufen unveraendert weiter.`,
      onSubmit: values => mutate(`/cases/${c.id}/${path}`, { expectedVersion: c.version, ...extra, ...values }) })
  function draft() {
    openDialog({ title: 'Gezielte simulierte Informationsanforderung', submitLabel: 'Anfrageentwurf speichern',
      description: 'Den tatsaechlich vorgesehenen synthetischen Kontakt selbst benennen. Weder Adresse noch Empfaenger werden aus einem Reviewer-Konto oder der letzten Anfrage erfunden.',
      fields: [
        { name: 'question', label: 'Konkrete medizinisch-technische Frage', type: 'textarea', minLength: 20,
          value: 'Welche direkt dokumentierten Befunde sprechen fuer oder gegen einen mechanischen, infektioesen oder anderen Beitrag zum Funktionsverlust?' },
        { name: 'minimalData', label: 'Exakt erforderlicher Minimaldatensatz', type: 'textarea', minLength: 15,
          value: 'Relevanter Eingriffs-/Revisionsbefund, Funktionsbefund und Ergebnis bereits durchgefuehrter mikrobiologischer Abklaerung; keine Identifikatoren.' },
        { name: 'relevance', label: 'Warum veraendert dies die Unsicherheit?', type: 'textarea', minLength: 30,
          value: 'Die Befundausschnitte erlauben die Abgrenzung konkurrierender Ursachen ohne eine nicht erforderliche vollstaendige Krankengeschichte.' },
        ...recipientFields(),
        { name: 'dueAt', label: 'Interne Antwortfrist (UTC)', type: 'datetime-local', value: new Date(Date.now() + 7 * 86400000).toISOString().slice(0, 16) },
        { name: 'manufacturerBasis', label: 'Herstellerseitige Verarbeitung (Pilotbegruendung)', type: 'textarea', minLength: 20,
          value: 'Ausschliesslich synthetische Demonstration. Vor echter Verarbeitung Art. 6 und 9 DSGVO sowie Zweckbindung separat pruefen.' },
        { name: 'disclosureBasis', label: 'Eigenstaendige Offenlegungsbefugnis der abgebenden Stelle', type: 'textarea', minLength: 20,
          value: 'Nur simulierte Klinikantwort. Eine reale Offenlegung muesste durch die abgebende Stelle eigenstaendig rechtlich geprueft werden.' },
      ], onSubmit: values => mutate(`/cases/${c.id}/information`, { expectedVersion: c.version,
        question: values.question, minimalData: values.minimalData, relevance: values.relevance,
        recipientRole: values.recipientRole, manufacturerBasis: values.manufacturerBasis, disclosureBasis: values.disclosureBasis,
        dueAt: new Date(`${values.dueAt}Z`).toISOString(), recipient: recipientFrom(values) }) })
  }
  function receive(request?: InfoRequest) {
    openDialog({ title: 'Synthetischen Ruecklauf erfassen', submitLabel: 'Originalruecklauf speichern',
      description: 'Nur vorhandene Originalangaben eintragen. Fehlende Referenzen leer lassen: der Ruecklauf bleibt dann sichtbar zurueckgehalten. Kein Matching nach Betreff, zeitlicher Naehe oder letzter Anfrage. Der Eingang ist keine Bestaetigung der Befunde.',
      fields: [
        { name: 'externalId', label: 'Originalreferenz des Ruecklaufs', minLength: 3,
          value: request ? `DEMO-RESPONSE-${crypto.randomUUID()}` : '' },
        { name: 'sourceDescription', label: 'Dokumentierte Ruecklaufherkunft', type: 'textarea', minLength: 10,
          value: request ? 'Manuell erzeugter synthetischer Demo-Ruecklauf; kein Klinikabruf und kein echter Nachrichteneingang.' : '' },
        { name: 'requestId', label: 'Anfrage-ID laut Original (leer, wenn nicht vorhanden)', value: request?.id ?? '', required: false },
        { name: 'correlationId', label: 'Korrelation laut Original (leer, wenn nicht vorhanden)', value: request?.lifecycle?.correlationId ?? '', required: false },
        { name: 'caseId', label: 'Fall-ID laut Original (leer, wenn nicht vorhanden)', value: request ? c.id : '', required: false },
        { name: 'productId', label: 'Produkt-ID laut Original (leer, wenn nicht vorhanden)', value: request ? c.productId : '', required: false },
        ...recipientFields(request?.lifecycle?.recipient),
        { name: 'response', label: 'Synthetischer Originalbefund', type: 'textarea', minLength: 30,
          value: request ? 'SYNTHETISCH: Der gezielte Befund beschreibt mechanische Lockerung. Die technische Ursache ist damit nicht abschliessend geklaert. Unabhaengige Quellenpruefung und medizinisch-technische Neubewertung bleiben erforderlich.' : '' },
        reason,
      ], onSubmit: values => mutate(`/cases/${c.id}/information/respond`, { expectedVersion: c.version,
        requestId: values.requestId, response: values.response, reason: values.reason,
        envelope: { externalId: values.externalId, sourceDescription: values.sourceDescription, caseId: values.caseId || null,
          productId: values.productId || null, correlationId: values.correlationId || null,
          sender: recipientFrom(values), synthetic: values.synthetic === 'true' } }) })
  }
  function map(response: InformationResponse) {
    const available = c.informationRequests.filter(r => r.lifecycle?.dispatches.some(d => d.status === 'SimulatedSent' && d.receiptReference))
    openDialog({ title: 'Ruecklauf am Original zuordnen', submitLabel: 'Als unbestaetigte Evidenz zuordnen',
      description: `${response.envelope.externalId}: ${response.matchExplanation} Widerspruechliche Referenzen oder fremder Absender bleiben serverseitig gesperrt. Die Zuordnung ist keine medizinische Quellen- oder Fallfreigabe.`,
      fields: [
        { name: 'requestId', label: 'Exakte Anfrage mit bestaetigtem simuliertem Empfang', type: 'select',
          value: response.status === 'Received' && response.candidateRequestIds.length === 1 ? response.candidateRequestIds[0] : '',
          options: [{ value: '', label: 'Anfrage bewusst auswaehlen - keine letzte Anfrage vorbelegt' },
            ...available.map(r => ({ value: r.id, label: `${r.id} · ${r.lifecycle?.recipient.contact} · ${r.question}` }))] },
        { name: 'confirmMissingReferences', label: 'Fehlende Originalreferenzen', type: 'select', value: 'false',
          options: [{ value: 'false', label: 'Keine fehlenden Referenzen bestaetigen; nur vollstaendige Originalbindung' },
            { value: 'true', label: 'Fehlende Referenzen zur ausgewaehlten Anfrage anhand des Originals begruendet bestaetigt' }] },
        reason,
      ], onSubmit: values => {
        const request = available.find(r => r.id === values.requestId)
        const dispatch = request?.lifecycle?.dispatches.find(d => d.status === 'SimulatedSent' && d.receiptReference)
        if (!request?.lifecycle || !dispatch) throw new Error('Exakter bestaetigter Sendeauftrag fehlt.')
        return mutate(`/cases/${c.id}/information/responses/${response.id}/map`, { expectedVersion: c.version,
          requestId: request.id, correlationId: request.lifecycle.correlationId, recipientHash: dispatch.recipientHash,
          confirmMissingReferences: values.confirmMissingReferences === 'true', reason: values.reason })
      } })
  }
  return <section className="panel information-panel" aria-label="Nachforderungen und Ruecklaeufe">
    <div className="row-between"><div><span className="eyebrow">GEZIELT. BEGRUENDET. DATENSPARSAM.</span><h2>Informationen nachfordern</h2></div>
      {editor && <div className="row"><button disabled={busy} className="button secondary" onClick={() => receive()}>Ruecklauf erfassen</button>
        <button disabled={busy} className="button primary" onClick={draft}><ArrowUpRight size={17} />Anfrage entwerfen</button></div>}</div>
    <p className="callout">Medizinische und getrennte RA-Freigabe erlauben nur die exakt gebundene Anfrage. Freigabe ist kein Versand.
      Der separate Sendeauftrag geht ausschliesslich an den internen Simulator, nicht an den eingetragenen Kontaktkanal. Fehlende Antworten pausieren keine Frist.</p>
    {!c.informationRequests.length && <Empty title="Noch keine Nachforderung">Assurance-Befunde helfen, genau die benoetigte Information zu benennen.</Empty>}
    {c.informationRequests.map(r => {
      const life = r.lifecycle
      const approved = r.approvals.filter(a => a.scopeHash === r.scopeHash)
      const canApprove = life && r.status === 'Draft' && (medical || regulatory) &&
        !approved.some(a => a.actorId === actor.id || a.role === approvalRole) &&
        (medical || approved.some(a => a.role === 'MedicalSafetyReviewer'))
      const delivered = life?.dispatches.some(d => d.status === 'SimulatedSent' && d.receiptReference)
      return <article className="information-card" key={r.id} aria-label={`Anfrage ${r.id}`}>
        <div className="row-between"><h3>{r.question}</h3><Badge tone={r.status === 'SendFailed' ? 'danger' : 'amber'}>
          {life ? r.status === 'Draft' && approved.length ? 'Medizinisch genehmigt - RA offen' : states[r.status] ?? r.status : `Historischer Status: ${label(r.status)}`}</Badge></div>
        <p>{r.minimalData}</p><p><b>Relevanz:</b> {r.relevance}</p>
        {life ? <><h4>Vorgesehener menschlicher Empfaenger</h4><Recipient value={life.recipient} /></> :
          <p className="callout">Altbestand: nur Empfaengerrolle „{r.recipientRole}“ erfasst. Organisation, konkreter Kontakt, Kanal, Korrelation und eigenstaendiger Versandbeleg sind nicht nachgewiesen. Keine nachtraegliche Zustellungsbehauptung; eine neue Anfrage vollstaendig erfassen.</p>}
        <p className="muted">Interne Antwortfrist {date(r.dueAt, true)} · {approved.length}/2 gebundene Freigaben</p>
        <details><summary>Freigaben, Rechtsgrundlagen und genaue Referenzen</summary>
          <p>{r.manufacturerBasis}</p><p>{r.disclosureBasis}</p>
          <p className="mono">Anfrage {r.id}<br />Korrelation {life?.correlationId ?? 'Nicht erfasst'}<br />Fall {life?.caseId ?? c.id} · Produkt {life?.productId ?? c.productId}<br />Inhaltsbindung {r.scopeHash}</p>
          {r.approvals.map(a => <div key={a.id}><p><b>{a.actorName}</b> · {roleNames[a.role] ?? a.role} · {date(a.at, true)}</p><p>{a.rationale}</p>
            <small>{a.scopeHash === r.scopeHash ? 'An diese Anfrage gebunden; kein Versandbeleg.' : 'Historische Signatur, nicht aktuell gebunden.'}</small></div>)}
        </details>
        {life?.dispatches.map(d => <div className="rfi-dispatch" key={d.id}>
          <p><b>Interner Simulator: {states[d.status] ?? d.status}</b></p>
          <p>Auftrag {date(d.queuedAt, true)}{d.attemptedAt && ` · Tatsaechlicher Simulatorversuch ${date(d.attemptedAt, true)}`}</p>
          {d.receiptReference && <p>Technische Simulatorquittung: <span className="mono">{d.receiptReference}</span>. Kein Nachweis einer echten Nachricht oder medizinischen Kenntnisnahme.</p>}
          {d.error && <p className="danger-text">Technischer Befund: {d.error}. Keine erfolgreiche Zustellung behauptet.</p>}
          {sender && d.status === 'TransmissionUnknown' && <button disabled={busy} className="button secondary"
            onClick={() => action('Exakten Simulator-Empfang abgleichen', `information/${r.id}/dispatch/${d.id}/reconcile`, [reason])}>Empfang gezielt abgleichen</button>}
        </div>)}
        <div className="row">
          {canApprove && <button disabled={busy} className="button secondary" onClick={() =>
            action('Simulierte Anfrage begruendet genehmigen', `information/${r.id}/approve`, [reason])}>Als {medical ? 'Medical Safety' : 'RA'} genehmigen</button>}
          {sender && life && ['Approved', 'SendFailed'].includes(r.status) && <button disabled={busy} className="button secondary" onClick={() =>
            action('Separaten simulierten Versand beauftragen', `information/${r.id}/dispatch`, [
              { name: 'scenario', label: 'Explizites Simulator-Szenario', type: 'select', value: 'Accept', options: [
                { value: 'Accept', label: 'Technische Annahme durch internen Simulator' }, { value: 'Fail', label: 'Fehler vor Zustellung' },
                { value: 'Unknown', label: 'Unklarer Ausgang; gezielten Empfangsabgleich verlangen' }] }, reason,
            ], {}, 'Nur internen Simulator beauftragen')}><Send size={16} />Simulierten Versand beauftragen</button>}
          {editor && delivered && <button disabled={busy} className="button secondary" onClick={() => receive(r)}>Antwort simulieren</button>}
        </div>
        {!life && r.response && <blockquote>{r.response}</blockquote>}
      </article>
    })}
    <h3>Eingegangene Ruecklaeufe</h3>
    {!responses.length && <p className="muted">Noch kein Ruecklauf mit getrenntem Original- und Zuordnungsnachweis erfasst.</p>}
    {responses.map(r => <article className="information-card" key={r.id} aria-label={`Ruecklauf ${r.envelope.externalId}`}>
      <div className="row-between"><h4>{r.envelope.externalId}</h4><Badge tone={r.status === 'Held' ? 'danger' : 'amber'}>
        {r.status === 'Held' ? 'Zurueckgehalten - Zuordnung unklar' : r.mapping ? r.review ? 'Quelle fuer Bewertung geprueft' : 'Zugeordnet - Befund unbestaetigt' : 'Eingegangen - Zuordnung offen'}</Badge></div>
      <p>{r.matchExplanation}</p><p>{r.envelope.sourceDescription} · Eingang {date(r.receivedAt, true)}</p>
      <h4>Absender laut Ruecklauf</h4><Recipient value={r.envelope.sender} /><blockquote>{r.content}</blockquote>
      <details><summary>Unveraenderte Originalreferenzen und Hashes</summary><p className="mono">
        Anfrage {r.declaredRequestId || 'Nicht vorhanden'}<br />Korrelation {r.envelope.correlationId || 'Nicht vorhanden'}<br />
        Fall {r.envelope.caseId || 'Nicht vorhanden'} · Produkt {r.envelope.productId || 'Nicht vorhanden'}<br />
        Original {r.originalHash}<br />Text {r.contentHash}</p></details>
      {r.mapping && <div><p><b>Menschliche Zuordnung:</b> {r.mapping.actorName} · {date(r.mapping.at, true)}</p><p>{r.mapping.reason}</p>
        <p className="mono">Anfrage {r.mapping.requestId} · {r.mapping.correlationId} · Scope {hash(r.mapping.requestScopeHash)}</p>
        {r.mapping.missingReferencesConfirmed && <p>Fehlende Originalreferenzen wurden separat menschlich bestaetigt; das Original wurde nicht ergaenzt oder ueberschrieben.</p>}</div>}
      {r.review ? <p><b>Quellenpruefung:</b> {r.review.actorName} · {date(r.review.at, true)} · {r.review.reason}<br />Keine abschliessende Kausalitaets-, Fall- oder Meldefreigabe.</p> :
        r.mapping && <p className="callout">Dieser Befund bleibt unbestaetigte Evidenz. Medical Safety muss Herkunft, Relevanz und Grenzen separat pruefen, bevor er als freigegebene Bewertungsquelle verwendet wird. Assessment und Paket brauchen danach neue Pruefung.</p>}
      <div className="row">{editor && !r.mapping && <button disabled={busy} className="button secondary" onClick={() => map(r)}>Ruecklauf begruendet zuordnen</button>}
        {medical && r.mapping && !r.review && <button disabled={busy} className="button secondary" onClick={() =>
          action('Ruecklauf als Bewertungsquelle pruefen', `information/responses/${r.id}/review`, [reason], {},
            'Nur Quelle fuer Bewertung freigeben')}>Herkunft und Befundquelle pruefen</button>}</div>
    </article>)}
  </section>
}
