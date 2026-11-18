import { useEffect, useRef, useState } from 'react'
import { api } from './api'
import type { Actor, Approval, VersionReference } from './types'
import type { Correspondence, DossierView, EudamedDossier } from './EudamedDossiers'
import { ActionDialog, Badge, date, ErrorBanner, hash, roleNames, type Dialog, type Field } from './ui'

interface Recipient { id: string; country: string; label: string }
interface FeedbackAssessment { original: Correspondence; disposition: string; rationale: string }
interface FeedbackView { dossier: VersionReference; ledger: VersionReference; feedback: Correspondence[] }
interface PublicationPlan {
  dossierSource: VersionReference; dossierRevision: number; packageHash: string; documentHash: string
  documentName: string; documentVersion: string; fscaSource: VersionReference; deliveryLedger: VersionReference
  audience: string; recipients: Recipient[]; channel: string; route: string; feedback: FeedbackAssessment[]
  changeAssessment: string; regulatoryDecision: string
}
export interface FsnPublicationReview {
  id: string; plan: PublicationPlan; scopeHash: string; reviewStatus: string; approvals: Approval[]
  createdBy: string; createdAt: string
}
export interface FsnDistribution {
  id: string; dossierRevision: number; packageHash: string; audience: string; channel: string
  evidenceHash: string; at: string; actorId: string; simulated: boolean
  publicationId?: string | null; publicationScopeHash?: string | null
  publicationSource?: VersionReference | null
}
const channels: Record<string, string> = { SimulatedEmail: 'E-Mail nur lokal simulieren', SimulatedPortal: 'Portal nur lokal simulieren' }
const routes: Record<string, string> = { AfterAuthorityFeedback: 'Nach fachlicher Annahme und bewerteter Rueckmeldung', UrgentAlternative: 'Dringlicher Alternativweg mit eigener RA-Entscheidung' }
const dispositions: Record<string, string> = { Incorporated: 'In der Dokumentfassung beruecksichtigt', NoChangeJustified: 'Begruendet keine Dokumentaenderung', DeferredUrgent: 'Dringlich begruendete Nachbearbeitung' }
const reviewStates: Record<string, string> = { Draft: 'Verteilungsentwurf', MedicalApproved: 'Medizinisch geprueft', Frozen: 'Gesondert freigegeben', Published: 'Veroeffentlichung und Verteilung simuliert' }
const reason: Field = { name: 'reason', label: 'Eigenstaendige Begruendung dieser Veroeffentlichungshandlung', type: 'textarea', minLength: 100 }

function readRecipients(value: string): Recipient[] {
  return value.split(/\r?\n/).map(line => line.trim()).filter(Boolean).map(line => {
    const parts = line.split('|').map(part => part.trim())
    if (parts.length !== 3 || parts.some(part => !part)) throw new Error('Je Empfaenger genau DEMO-Kennung | Land | SYNTHETISCH: Bezeichnung eingeben.')
    return { id: parts[0], country: parts[1], label: parts[2] }
  })
}

function Summary({ review, dossier, original }: {
  review: FsnPublicationReview; dossier: EudamedDossier
  original: (hash: string, name: string, version?: number) => Promise<void>
}) {
  const plan = review.plan
  return <>
    <dl className="eudamed-bindings">
      <dt>Freigegebener Verteilungsscope</dt><dd>{review.scopeHash}</dd>
      <dt>FSN-Dokument</dt><dd>{plan.documentName} / {plan.documentVersion} / {plan.documentHash}</dd>
      <dt>Originaldossier</dt><dd>v{plan.dossierSource.version}, Paket r{plan.dossierRevision} / {plan.packageHash}</dd>
      <dt>FSCA</dt><dd>{plan.fscaSource.id} / v{plan.fscaSource.version} / {plan.fscaSource.hash}</dd>
      <dt>Sprache / Jurisdiktion / Laender</dt><dd>{dossier.package.content.language} / {dossier.package.content.jurisdiction} / {dossier.package.content.countries.join(', ')}</dd>
      <dt>Behoerdenprofil, nicht Verteilungsempfaenger</dt><dd>{dossier.package.binding.profile.recipient} / {dossier.package.binding.profileHash}</dd>
      <dt>Umgebung und Verteilungskanal</dt><dd>Nur Simulation / {channels[plan.channel] ?? plan.channel}</dd>
      <dt>Veroeffentlichungsweg</dt><dd>{routes[plan.route] ?? plan.route}</dd>
      <dt>Gebundener Kommentar-/Versandstand</dt><dd>{plan.deliveryLedger.id} / v{plan.deliveryLedger.version} / {plan.deliveryLedger.hash}</dd>
    </dl>
    <div className="eudamed-actions">
      <button type="button" className="button secondary" onClick={() => void original(plan.documentHash, plan.documentName, plan.dossierSource.version)}>Gebundenes FSN-PDF lesen</button>
      <button type="button" className="button secondary" onClick={() => void original(plan.packageHash, 'COHORS-DEMO-FSN-Paket.json', plan.dossierSource.version)}>Gebundenes Originalpaket lesen</button>
    </div>
    <h4>Adressatenkreis und tatsaechlich simulierte Empfaenger</h4>
    <p>{plan.audience}</p>
    <ul>{plan.recipients.map(recipient => <li key={recipient.id}>{recipient.label} / {recipient.country} / {recipient.id} / simulator://fsn/{recipient.id}</li>)}</ul>
    <h4>Gebundene Sicherheitsanweisung</h4><p className="eudamed-narrative">{dossier.package.content.fsn?.instructions}</p>
    <h4>Bewertete Dokumentaenderungen</h4><p className="eudamed-narrative">{plan.changeAssessment}</p>
    <h4>Regulatorische Entscheidung / dringliche Ausnahme</h4><p className="eudamed-narrative">{plan.regulatoryDecision}</p>
    {!plan.feedback.length && <p className="callout">Keine Kommentare in diesem gebundenen Stand. Das ist keine Behoerden-Sicherheitsfreigabe und ersetzt keine der beiden menschlichen Pruefungen.</p>}
    {plan.feedback.map(item => <article className="eudamed-round" key={item.original.id}>
      <b>{item.original.kind} / {item.original.status} / {dispositions[item.disposition] ?? item.disposition}</b>
      <p className="eudamed-narrative">{item.original.content}</p>
      <p className="eudamed-narrative">{item.rationale}</p>
      <small>{item.original.id} / Original {item.original.originalHash}</small>
    </article>)}
    {review.approvals.map(approval => <article className="eudamed-round" key={approval.id}>
      <b>{roleNames[approval.role] ?? approval.role} / {approval.actorName}</b><p>{approval.rationale}</p>
      <small>{date(approval.at, true)} / {approval.scopeHash}</small>
    </article>)}
  </>
}

export default function FsnPublication({ actor, dossier, ledger, historical, profileCurrent, change, original }: {
  actor: Actor; dossier: EudamedDossier; ledger: DossierView['ledger']; historical: boolean; profileCurrent: boolean
  change: (path: string, body: unknown) => Promise<void>
  original: (hash: string, name: string, version?: number) => Promise<void>
}) {
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const [loadingFeedback, setLoadingFeedback] = useState(false)
  const [error, setError] = useState('')
  const live = useRef(true)
  useEffect(() => {
    live.current = true
    return () => { live.current = false }
  }, [])
  const review = dossier.fsnPublication
  const medical = actor.isHuman && actor.roles.includes('MedicalSafetyReviewer')
  const regulatory = actor.isHuman && actor.roles.includes('RegulatoryAffairs')
  const author = actor.isHuman && (medical || regulatory || actor.roles.includes('PmsSpecialist'))
  const editable = !historical && profileCurrent && dossier.reviewStatus === 'Frozen' &&
    dossier.package.binding.simulated && ['Submit', 'Revision'].includes(dossier.package.content.intendedAction)
  const current = review && review.plan.dossierRevision === dossier.revision && review.plan.packageHash === dossier.scopeHash &&
    review.plan.deliveryLedger.id === ledger.id && review.plan.deliveryLedger.version === ledger.version
  const base = `/eudamed/dossiers/${dossier.id}/fsn-publication`
  async function draft() {
    setLoadingFeedback(true)
    setError('')
    try {
      const source = await api<FeedbackView>(`${base}/feedback`)
      if (!live.current) return
      if (source.dossier.id !== dossier.id || source.dossier.version !== dossier.version || source.ledger.id !== ledger.id)
        throw new Error('Dossier- oder Kommentarstand wurde geaendert. Aktuelle Dossierfassung erneut oeffnen.')
      showDraft(source)
    } catch (failure) {
      if (live.current) setError(failure instanceof Error ? failure.message : 'Vollstaendige autorisierte FSN-Korrespondenz konnte nicht gelesen werden.')
    } finally { if (live.current) setLoadingFeedback(false) }
  }
  function showDraft(source: FeedbackView) {
    const feedback = source.feedback
    const old = review?.plan
    const fields: Field[] = [
      { name: 'recipients', label: 'Synthetische Empfaenger; je Zeile DEMO-Kennung | ISO-Land | SYNTHETISCH: Bezeichnung', type: 'textarea',
        value: old?.recipients.map(recipient => `${recipient.id} | ${recipient.country} | ${recipient.label}`).join('\n'), minLength: 20 },
      { name: 'channel', label: 'Nur simulierter Kommunikationskanal', type: 'select', value: old?.channel ?? '',
        options: [{ value: '', label: 'Explizit auswaehlen' }, ...Object.entries(channels).map(([value, label]) => ({ value, label }))] },
      { name: 'route', label: 'Veroeffentlichungsweg', type: 'select', value: old?.route ?? '',
        options: [{ value: '', label: 'Explizit entscheiden' }, ...Object.entries(routes)
          .filter(([value]) => value !== 'UrgentAlternative' || dossier.package.content.fsn?.urgent)
          .map(([value, label]) => ({ value, label }))] },
      { name: 'changeAssessment', label: 'Medizinisch-technisch bewertete Dokumentaenderungen, betroffene Anweisungen und Restrisiken', type: 'textarea', minLength: 100, value: old?.changeAssessment },
      { name: 'regulatoryDecision', label: 'Belegte Entscheidungsgrundlage; fehlende Rueckmeldungen oder dringlichen Alternativweg ausdruecklich begruenden', type: 'textarea', minLength: 100, value: old?.regulatoryDecision },
    ]
    feedback.forEach((item, index) => {
      const previous = old?.feedback.find(value => value.original.id === item.id && value.original.originalHash === item.originalHash)
      fields.push(
        { name: `disposition-${index}`, label: `${item.kind} / ${item.id}: ausdrueckliche Bewertung`, type: 'select', value: previous?.disposition ?? '',
          options: [{ value: '', label: 'Keine stillschweigende Uebernahme' }, ...Object.entries(dispositions).map(([value, label]) => ({ value, label }))] },
        { name: `rationale-${index}`, label: `${item.id}: Begruendung und Bezug zur konkreten Dokumentfassung`, type: 'textarea', minLength: 80, value: previous?.rationale },
      )
    })
    setDialog({ title: 'FSN-Veroeffentlichung gesondert vorbereiten',
      description: 'Die EUDAMED-Annahme bleibt getrennt. Dieser neue Entwurf bindet Dokument, FSCA, alle bekannten Kommentare und den konkreten synthetischen Empfaengerscope; alte Verteilungsfreigaben werden nicht uebernommen.',
      body: <><p>Vollstaendiger autorisierter Kommentar-/Versandstand v{source.ledger.version} / {source.ledger.hash}</p>
        {feedback.map(item => <article className="eudamed-round" key={item.id}>
        <b>{item.kind} / {item.status} / {item.id}</b><p>{item.content}</p><small>Original {item.originalHash}</small>
      </article>)}{!feedback.length && <p>Keine Kommentare im aktuellen Stand; beide menschlichen Freigaben bleiben trotzdem erforderlich.</p>}</>,
      fields: [...fields, reason], submitLabel: 'Neuen Verteilungsentwurf speichern',
      onSubmit: async values => change(base, { expectedVersion: source.dossier.version, expectedLedgerVersion: source.ledger.version,
        recipients: readRecipients(values.recipients), channel: values.channel, route: values.route,
        changeAssessment: values.changeAssessment, regulatoryDecision: values.regulatoryDecision, reason: values.reason,
        feedback: feedback.map((item, index) => ({ correspondenceId: item.id, originalHash: item.originalHash,
          disposition: values[`disposition-${index}`], rationale: values[`rationale-${index}`] })) }) })
  }
  function approve() {
    if (!review) return
    setDialog({ title: review.reviewStatus === 'Draft' ? 'FSN-Verteilung medizinisch freigeben' : 'FSN-Verteilung regulatorisch freigeben',
      description: 'Eigenstaendige Pruefung genau des angezeigten Veroeffentlichungs-/Empfaengerscopes. Getrennte Konten; noch keine simulierte Verteilung.',
      body: <Summary review={review} dossier={dossier} original={original} />, fields: [reason],
      submitLabel: 'Genau diesen Verteilungsscope freigeben',
      onSubmit: async values => change(`${base}/approve`, { expectedVersion: dossier.version, reason: values.reason, findingDisposition: null }) })
  }
  function publish() {
    if (!review) return
    setDialog({ title: 'FSN abschliessend nur simuliert veroeffentlichen und verteilen',
      description: 'Pruefen Sie das freigegebene Dokument, die Originalbeziehungen, beide Entscheidungen und jeden simulierten Empfaenger. Es werden nur lokale Belege erzeugt; kein FSN.PUT, keine E-Mail und kein externer Portalaufruf.',
      body: <Summary review={review} dossier={dossier} original={original} />,
      fields: [{ name: 'confirmation', label: 'Explizite finale Bestaetigung', type: 'select', value: '',
        options: [{ value: '', label: 'Noch nicht bestaetigt' }, { value: 'simulate', label: 'Genau diesen Scope ausschliesslich simuliert veroeffentlichen und verteilen' }] }, reason],
      submitLabel: 'Nur simuliert veroeffentlichen und verteilen',
      onSubmit: async values => change(`${base}/publish`, { expectedVersion: dossier.version,
        expectedLedgerVersion: ledger.version, publicationScopeHash: review.scopeHash,
        confirmSimulation: values.confirmation === 'simulate', reason: values.reason }) })
  }
  return <section className="eudamed-record" aria-label="Gesonderte FSN-Veroeffentlichung">
    <h3>FSN-Veroeffentlichung und Empfaengernachweis</h3>
    <p className="callout">Die unveraenderte Paketfassung bleibt als Entwurf gekennzeichnet. Die getrennte Freigabe und anschliessende Verteilungssimulation werden hier versioniert dokumentiert. Fehlende Kommentare sind keine Sicherheitsfreigabe.</p>
    {error && <ErrorBanner message={error} />}
    <Badge>{review ? reviewStates[review.reviewStatus] ?? review.reviewStatus : 'Noch kein gesonderter Verteilungsentwurf'}</Badge>
    {review && !current && !historical && review.reviewStatus !== 'Published' &&
      <p className="callout">Paket- oder Kommentarstand geaendert. Neue Fassung vorbereiten; alte Bestaetigungen sind nicht verwendbar.</p>}
    <div className="eudamed-actions">
      {editable && author && <button className="button secondary" disabled={loadingFeedback} onClick={() => void draft()}>
        {loadingFeedback ? 'Vollstaendige Korrespondenz lesen...' : 'FSN-Verteilung vorbereiten'}</button>}
      {editable && current && review && !review.approvals.some(approval => approval.actorId === actor.id) &&
        (medical && review.reviewStatus === 'Draft' || regulatory && review.reviewStatus === 'MedicalApproved') &&
        <button className="button primary" onClick={approve}>{review.reviewStatus === 'Draft' ? 'Medical-Verteilungsfreigabe' : 'RA-Verteilungsfreigabe'}</button>}
      {editable && current && regulatory && review?.reviewStatus === 'Frozen' &&
        <button className="button primary" onClick={publish}>FSN nur simuliert veroeffentlichen und verteilen</button>}
    </div>
    {review && <details><summary>Gebundene Verteilungsfassung / {hash(review.scopeHash)}</summary>
      <Summary review={review} dossier={dossier} original={original} /></details>}
    {!dossier.distributions.length && <p>Kein Kommunikationsbeleg. Eine EUDAMED-Annahme allein erzeugt keinen Empfaengernachweis.</p>}
    {dossier.distributions.map(distribution => <article className="eudamed-round" key={distribution.id}>
      <b>{distribution.simulated ? 'Lokal simulierter Kommunikationsbeleg' : 'Historischer Kommunikationsbeleg'} / Paket r{distribution.dossierRevision}</b>
      <p>{distribution.audience} / {channels[distribution.channel] ?? distribution.channel}</p>
      <p>{date(distribution.at, true)} / Paket {distribution.packageHash} / Verteilungsscope {distribution.publicationScopeHash ?? 'Historisch nicht separat erfasst'}</p>
      <button className="button secondary" onClick={() => void original(distribution.evidenceHash, `${distribution.id}.json`)}>Unveraenderten Empfaenger-/Kommunikationsbeleg lesen</button>
    </article>)}
    {dialog && <ActionDialog key={dialog.title} dialog={dialog} close={() => setDialog(null)} />}
  </section>
}
