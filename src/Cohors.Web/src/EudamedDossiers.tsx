import { useCallback, useEffect, useRef, useState } from 'react'
import { api, downloadApi } from './api'
import type { Actor, Approval, AuditEvent, Device, VersionReference } from './types'
import type { IntegrationWorkspace } from './EudamedWorkspace'
import { ActionDialog, Badge, date, ErrorBanner, hash, label, Loading, roleNames, SectionHead, type Dialog, type Field } from './ui'

export type DossierKind = 'MIR' | 'MTR' | 'PSR' | 'PSRP' | 'PSUR' | 'FSCA' | 'FSN'
interface Period { start: string; end: string; basis: string }
interface Binding {
  channelId: string; channelVersion: number; dispatchGeneration: number; profileHash: string
  profile: NonNullable<IntegrationWorkspace['activeChannel']>['profile']
  actorSource: VersionReference; referencesSource: VersionReference; actorIdentity: string; wsdlSha256: string; simulated: boolean
}
interface Validation { schemaVersion: string; mappingVersion: string; evidenceLevel: string; errors: string[]; warnings: string[] }
interface Attachment { name: string; mediaType: string; hash: string; source: VersionReference }
interface Attempt {
  id: string; correlationId: string; messageId: string; dossierRevision: number; packageHash: string; binding: Binding
  scenario: string; status: string; receiptHash: string | null; errorCode: string | null; queuedAt: string
}
interface BusinessResponse {
  inboxId: string; attemptId: string; originalHash: string; sequence: number; status: string; applied: boolean
  dossierCode: string | null; dossierVersion: number | null; errors: string[]; returnedPeriod: Period | null; at: string
}
interface Correspondence {
  id: string; inboxId: string; originalHash: string; issuer: string; kind: string; content: string; dossierRevision: number
  receivedAt: string; dueAt: string | null; status: string; replyDossierId: string | null; ownerRole: string
}
interface FscaContent {
  batches: string[]; serialNumbers: string[]; measureType: string; manufacturerAction: string; userAction: string
  responsiblePerson: string; effectivenessCriterion: string; dueAt: string; capaIds: string[]; decisionDate: string
  healthHazard: string; hazardProbability: string; rootCauseStatus: string; rootCauseRationale: string
  manufacturerActionTypes: string[]; userActionTypes: string[]; patientFollowUp: string
}
interface FsnContent {
  fscaId: string; fscaVersion: number; audience: string; instructions: string; documentHash: string; documentName: string
  documentVersion: string; publicationState: string; urgent: boolean; reportType: string; documentDate: string
}
export interface DossierContent {
  title: string; clinicalRationale: string; regulatoryRationale: string; language: string; jurisdiction: string; countries: string[]
  intendedAction: string; parentId: string | null; parentVersion: number | null; period: Period | null
  fsca: FscaContent | null; fsn: FsnContent | null; replyToCorrespondenceId: string | null
  psrTerms: { frequencyMonths: number; firstPeriodStart: string; rationale: string } | null
  mtr: { identifiedAt: string; clinicalInvestigationRelated: boolean; clinicalInvestigationId: string | null; rationale: string } | null
}
export interface DossierSummary {
  id: string; version: number; productId: string; kind: DossierKind; revision: number; title: string; reviewStatus: string
  scopeHash: string; binding: Binding; approvals: Approval[]; attempts: Attempt[]; businessResponses: BusinessResponse[]
  openCorrespondence: number; validation: Validation
}
export interface EudamedDossier {
  id: string; version: number; productId: string; kind: DossierKind; revision: number; reviewStatus: string; scopeHash: string
  approvals: Approval[]; frozenPackageHash: string | null; attempts: Attempt[]; businessResponses: BusinessResponse[]
  correspondence: Correspondence[]
  plannedResponses: { id: string; attemptId: string; sequence: number; kind: string; dueAt: string; status: string; publishedInboxId: string | null }[]
  package: {
    content: DossierContent; binding: Binding; validation: Validation; attachments: Attachment[]; businessXml: string; businessXmlHash: string
    sources: { kind: string; source: VersionReference; json: string }[]
    calendar: { id: string; version: string; timeZone: string }
    references: { certificate: { originalHash: string } }
  }
}
export interface DossierView {
  dossier: EudamedDossier
  ledger: { id: string; version: number; entries: { dossierId: string; attempt: Attempt; intendedAction: string; businessStatus: string }[] }
}
interface SourceOption {
  kind: string; id: string; version: number; productId: string; label: string
  language: string | null; jurisdiction: string | null; period: Period | null; dossierKind: DossierKind | null; originalHash: string | null
}
const primaryKinds: Record<DossierKind, string> = {
  MIR: 'case', MTR: 'trend', PSR: 'summaryAgreement', PSRP: 'summaryReport', PSUR: 'psur', FSCA: 'capa', FSN: 'eudamedDossier',
}
const kindNames: Record<DossierKind, string> = {
  MIR: 'MIR - Einzelvorkommnis', MTR: 'MTR - Trendbericht', PSR: 'PSR - Sammelvereinbarung', PSRP: 'PSRP - periodische Analyse',
  PSUR: 'PSUR - Sicherheitsbericht', FSCA: 'FSCA - Sicherheitskorrektur', FSN: 'FSN - Sicherheitsinformation',
}
const scenarios: Record<string, string> = {
  Accept: 'Fachlich annehmen', DraftOnly: 'Nur Entwurf erzeugen', Reject: 'Fachlich ablehnen',
  TechnicalFailure: 'Technischer Fehler', Unknown: 'Uebermittlung unklar', Delayed: 'Antwort nach 20 Sekunden',
  OutOfOrder: 'Vertauschte Antwortreihenfolge', Comment: 'Annahme mit Kommentar', InformationRequest: 'Annahme mit Nachforderung',
  PeriodMismatch: 'PSRP-Periodenabweichung mit spaeterer Korrektur',
}
const statusNames: Record<string, string> = {
  TransportDelivered: 'Transport lokal bestaetigt', TransportFailed: 'Transport fehlgeschlagen',
  BusinessAccepted: 'Fachlich simuliert angenommen', BusinessRejected: 'Fachlich simuliert abgelehnt',
  DraftCreated: 'Nur Entwurf erzeugt', Pending: 'Fachliche Antwort ausstehend', NotSent: 'Nicht gesendet',
  PeriodMismatch: 'Periodenabweichung - gesperrt', ActionMismatch: 'Widerspruechliche Handlung - gesperrt',
  Fenced: 'Originalkanal/Quelle gesperrt', Replied: 'Simuliert beantwortet',
}
const state = (value: string) => statusNames[value] ?? label(value)
const reasonField: Field = { name: 'reason', label: 'Begruendung dieser Dossierhandlung', type: 'textarea', minLength: 80 }
const lines = (value: string) => value.split(/[,;\n]+/).map(v => v.trim()).filter(Boolean)
const multi = (value: string | undefined): string[] => {
  const parsed: unknown = JSON.parse(value ?? '[]')
  if (!Array.isArray(parsed) || !parsed.every((v): v is string => typeof v === 'string')) throw new Error('Ungueltige Quellenauswahl.')
  return parsed
}
const sourceChoice = (value: SourceOption) => ({ value: value.id, label: `${value.label} - v${value.version}` })
const narrative = (name: string, label: string, value?: string, minLength = 80): Field => ({ name, label, value, minLength, type: 'textarea' })
function localTime(value?: string) {
  if (!value) return ''
  const instant = new Date(value)
  return new Date(instant.getTime() - instant.getTimezoneOffset() * 60000).toISOString().slice(0, 16)
}

function dossierForm(kind: DossierKind, source: SourceOption, options: SourceOption[],
  previous?: EudamedDossier, reply?: { parent: EudamedDossier; correspondence: Correspondence }) {
  const old = previous?.package.content
  const content = old ?? reply?.parent.package.content
  const period = source.period ?? content?.period
  const parentKind = kind === 'PSRP' ? 'PSR' : kind
  const fields: Field[] = [
    { name: 'title', label: 'Dossiertitel', value: reply ? `SYNTHETISCH: Antwort auf ${reply.correspondence.kind}` : content?.title ?? `SYNTHETISCH: ${kind} - ${source.label}`.slice(0, 250), minLength: 10 },
    narrative('clinicalRationale', 'Medizinisch-technische Einordnung mit Fundstellen und Unsicherheiten', reply ? '' : content?.clinicalRationale, 100),
    narrative('regulatoryRationale', 'Regulatorische Einordnung und konkrete Handlung', reply ? '' : content?.regulatoryRationale),
    { name: 'language', label: 'Gebundene Sprache', type: 'select', value: content?.language ?? source.language ?? 'de',
      options: [{ value: 'de', label: 'Deutsch' }, { value: 'en', label: 'English' }] },
    { name: 'jurisdiction', label: 'MDR-Jurisdiktion des Quellpakets', value: content?.jurisdiction ?? source.jurisdiction ?? 'EU-IE' },
    { name: 'countries', label: 'Betroffene Laender (explizite ISO-Codes, kommagetrennt)', value: content?.countries.join(', ') ?? 'IE' },
  ]
  if (!reply && content?.intendedAction !== 'Reply') fields.push(
    { name: 'intendedAction', label: 'Beabsichtigte Handlung', type: 'select', value: content?.intendedAction ?? 'Submit',
      options: [
        { value: 'Submit', label: 'Einreichen - separate fachliche Quittung erforderlich' },
        { value: 'DraftOnly', label: 'Nur Entwurf - keine Meldepflicht quittieren' },
        ...(['MIR', 'FSCA'].includes(kind) ? [{ value: 'FollowUp', label: 'Follow-up einer konkreten Vorfassung' }] : []),
        ...(kind === 'FSN' ? [{ value: 'Revision', label: 'FSN-Revision - ausschliesslich Simulator' }] : []),
      ] },
    { name: 'parentId', label: 'Gebundene Vorfassung (fuer Follow-up/Revision; PSRP verlangt PSR)', type: 'select',
      value: content?.parentId ?? '', required: kind === 'PSRP',
      options: [{ value: '', label: 'Keine Vorfassung' }, ...options.filter(o => o.kind === 'eudamedDossier' && o.dossierKind === parentKind).map(sourceChoice)] },
  )
  if (['MTR', 'PSRP', 'PSUR'].includes(kind)) fields.push(
    { name: 'periodStart', label: 'Quellzeitraum ab (muss unveraendert passen)', type: 'date', value: period?.start },
    { name: 'periodEnd', label: 'Quellzeitraum bis (muss unveraendert passen)', type: 'date', value: period?.end },
    narrative('periodBasis', 'Belegte Periodenbasis', period?.basis, 20),
  )
  if (kind === 'MTR') fields.push(
    { name: 'identifiedAt', label: 'Belegtes Trenderkennungsdatum', type: 'date', value: content?.mtr?.identifiedAt },
    { name: 'investigationRelated', label: 'Belegter Bezug zu klinischer Pruefung', type: 'select',
      value: content?.mtr ? String(content.mtr.clinicalInvestigationRelated) : '',
      options: [{ value: '', label: 'Explizit aus den Quellen bestimmen' }, { value: 'true', label: 'Ja' }, { value: 'false', label: 'Nein, quellenbelegt' }] },
    { name: 'investigationId', label: 'Kennzeichen der klinischen Pruefung (bei Ja)', value: content?.mtr?.clinicalInvestigationId ?? '', required: false },
    narrative('mtrRationale', 'Begruendung des Erkennungszeitpunkts und Pruefungsbezugs', content?.mtr?.rationale),
  )
  if (kind === 'PSR') fields.push(
    { name: 'frequencyMonths', label: 'Ausdruecklich beantragte Kalendermonate (2 bis 12)', value: content?.psrTerms?.frequencyMonths.toString() },
    { name: 'firstPeriodStart', label: 'Erster beantragter Periodenanker', type: 'date', value: content?.psrTerms?.firstPeriodStart },
    narrative('psrRationale', 'Begruendung der Kalenderperiodizitaet, keine Umrechnung von Legacy-Tagen', content?.psrTerms?.rationale),
  )
  if (kind === 'FSCA') {
    const fsca = content?.fsca
    fields.push(
      { name: 'additionalCapas', label: 'Weitere gebundene CAPAs (optional, Mehrfachauswahl)', type: 'select', multiple: true, required: false,
        values: fsca?.capaIds.filter(id => id !== source.id), options: options.filter(o => o.kind === 'capa' && o.id !== source.id).map(sourceChoice) },
      { name: 'batches', label: 'Betroffene Chargen (kommagetrennt)', value: fsca?.batches.join(', '), required: false },
      { name: 'serialNumbers', label: 'Betroffene Seriennummern (alternativ/ergaenzend)', value: fsca?.serialNumbers.join(', '), required: false },
      { name: 'measureType', label: 'Massnahmenart', value: fsca?.measureType, minLength: 5 },
      narrative('manufacturerAction', 'Konkrete Herstellermassnahme', fsca?.manufacturerAction),
      narrative('userAction', 'Konkrete Anwenderhandlung', fsca?.userAction),
      { name: 'responsiblePerson', label: 'Verantwortliche synthetische Person/Rolle', value: fsca?.responsiblePerson, minLength: 5 },
      narrative('effectivenessCriterion', 'Nachpruefbares Wirksamkeitskriterium', fsca?.effectivenessCriterion, 50),
      { name: 'dueAt', label: 'Massnahmentermin (lokale Zeitzone)', type: 'datetime-local', value: localTime(fsca?.dueAt) },
      { name: 'decisionDate', label: 'Belegtes FSCA-Entscheidungsdatum', type: 'date', value: fsca?.decisionDate },
      narrative('healthHazard', 'Gesundheitsgefaehrdung mit klinischer Einordnung', fsca?.healthHazard, 50),
      narrative('hazardProbability', 'Auftretenswahrscheinlichkeit einschliesslich Unsicherheit', fsca?.hazardProbability, 50),
      { name: 'rootCauseStatus', label: 'Belegter Ursachenstatus', type: 'select', value: fsca?.rootCauseStatus ?? '',
        options: [{ value: '', label: 'Explizit bestimmen' }, { value: 'YES', label: 'Ursache bekannt' }, { value: 'NO_INV_ONGOING', label: 'Untersuchung laeuft' }, { value: 'NO_INV_POS', label: 'Untersuchung nicht moeglich' }] },
      narrative('rootCauseRationale', 'Quellengestuetzte Ursachen-/Untersuchungseinordnung', fsca?.rootCauseRationale),
      { name: 'manufacturerActionTypes', label: 'Herstellerkategorien (offizielle Codes, z.B. CUSTOMER_INFORMATION_ONLY)', value: fsca?.manufacturerActionTypes.join(', ') },
      { name: 'userActionTypes', label: 'Anwenderkategorien (offizielle Codes, z.B. IDENTIFY_DEVICE)', value: fsca?.userActionTypes.join(', ') },
      narrative('patientFollowUp', 'Konkreter Patienten-Nachbeobachtungsplan', fsca?.patientFollowUp),
    )
  }
  if (kind === 'FSN') fields.push(
    { name: 'documentId', label: 'Aufgenommenes synthetisches PDF-Original', type: 'select',
      value: options.find(o => o.kind === 'eudamedDocument' && o.originalHash === content?.fsn?.documentHash)?.id,
      options: [{ value: '', label: 'PDF-Original explizit auswaehlen' }, ...options.filter(o => o.kind === 'eudamedDocument').map(sourceChoice)] },
    { name: 'documentVersion', label: 'Fachliche FSN-Dokumentfassung', value: content?.fsn?.documentVersion },
    { name: 'documentDate', label: 'Belegtes Dokumentdatum', type: 'date', value: content?.fsn?.documentDate },
    narrative('audience', 'Betroffener Adressatenkreis', content?.fsn?.audience, 20),
    narrative('instructions', 'Konkrete Sicherheitsanweisung', content?.fsn?.instructions),
    { name: 'urgent', label: 'Ausdruecklich bestimmte Dringlichkeit', type: 'select', value: content?.fsn ? String(content.fsn.urgent) : '',
      options: [{ value: '', label: 'Menschlich bestimmen' }, { value: 'true', label: 'Dringlich' }, { value: 'false', label: 'Nicht dringlich' }] },
    { name: 'reportType', label: 'FSN-Berichtsphase', type: 'select', value: content?.fsn?.reportType ?? '',
      options: [{ value: '', label: 'Menschlich bestimmen' }, { value: 'PRELIMINARY', label: 'Vorlaeufig' }, { value: 'FINAL', label: 'Final' }] },
  )
  return { fields: [...fields, reasonField], build: (v: Record<string, string>) => {
    const selected = (id: string, kind: string) => {
      const option = options.find(o => o.id === id && o.kind === kind)
      if (!option) throw new Error('Die konkrete Quellversion ist nicht mehr in dieser berechtigten Auswahl. Quellen neu lesen.')
      return option
    }
    const parent = reply ? selected(reply.parent.id, 'eudamedDossier')
      : content?.intendedAction === 'Reply' ? selected(content.parentId!, 'eudamedDossier')
        : v.parentId ? selected(v.parentId, 'eudamedDossier') : null
    const additional = kind === 'FSCA' ? multi(v.additionalCapas).map(id => selected(id, 'capa')) : []
    const document = kind === 'FSN' ? selected(v.documentId, 'eudamedDocument') : null
    const sources = [source, ...additional, ...(parent ? [parent] : []), ...(document ? [document] : [])]
      .filter((item, index, all) => all.findIndex(other => other.kind === item.kind && other.id === item.id) === index)
      .map(({ kind, id, version }) => ({ kind, id, version }))
    const next: DossierContent = {
      title: v.title, clinicalRationale: v.clinicalRationale, regulatoryRationale: v.regulatoryRationale,
      language: v.language, jurisdiction: v.jurisdiction, countries: lines(v.countries),
      intendedAction: reply || content?.intendedAction === 'Reply' ? 'Reply' : v.intendedAction,
      parentId: parent?.id ?? null, parentVersion: parent?.version ?? null,
      replyToCorrespondenceId: reply?.correspondence.id ?? (content?.intendedAction === 'Reply' ? content.replyToCorrespondenceId : null),
      period: ['MTR', 'PSRP', 'PSUR'].includes(kind) ? { start: v.periodStart, end: v.periodEnd, basis: v.periodBasis } : null,
      mtr: kind === 'MTR' ? { identifiedAt: v.identifiedAt, clinicalInvestigationRelated: v.investigationRelated === 'true',
        clinicalInvestigationId: v.investigationId || null, rationale: v.mtrRationale } : null,
      psrTerms: kind === 'PSR' ? { frequencyMonths: Number(v.frequencyMonths), firstPeriodStart: v.firstPeriodStart, rationale: v.psrRationale } : null,
      fsca: kind === 'FSCA' ? {
        batches: lines(v.batches), serialNumbers: lines(v.serialNumbers), measureType: v.measureType,
        manufacturerAction: v.manufacturerAction, userAction: v.userAction, responsiblePerson: v.responsiblePerson,
        effectivenessCriterion: v.effectivenessCriterion, dueAt: new Date(v.dueAt).toISOString(),
        capaIds: [source.id, ...additional.map(o => o.id)], decisionDate: v.decisionDate, healthHazard: v.healthHazard,
        hazardProbability: v.hazardProbability, rootCauseStatus: v.rootCauseStatus, rootCauseRationale: v.rootCauseRationale,
        manufacturerActionTypes: lines(v.manufacturerActionTypes), userActionTypes: lines(v.userActionTypes), patientFollowUp: v.patientFollowUp,
      } : null,
      fsn: document ? { fscaId: source.id, fscaVersion: source.version, audience: v.audience, instructions: v.instructions,
        documentHash: document.originalHash!, documentName: document.label, documentVersion: v.documentVersion, publicationState: 'Draft',
        urgent: v.urgent === 'true', reportType: v.reportType, documentDate: v.documentDate } : null,
    }
    return { expectedVersion: previous?.version ?? 0, productId: source.productId, kind, content: next, sources, reason: v.reason }
  } }
}

export default function EudamedDossiers({ actor, product, workspace, refresh }: {
  actor: Actor; product: Device; workspace: IntegrationWorkspace; refresh: () => Promise<void>
}) {
  const [kind, setKind] = useState<DossierKind>('MIR')
  const [sourceId, setSourceId] = useState('')
  const [sources, setSources] = useState<SourceOption[]>([])
  const [selectedId, setSelectedId] = useState('')
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [file, setFile] = useState<File | null>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  const live = useRef(true)
  const generation = useRef(0)
  const author = actor.isHuman && actor.roles.some(role => ['PmsSpecialist', 'MedicalSafetyReviewer', 'RegulatoryAffairs'].includes(role))
  const ready = workspace.effectiveMode === 'Simulator' && workspace.activeReadiness.canDispatch
  const loadSources = useCallback(async () => {
    const request = ++generation.current
    const values = await api<SourceOption[]>(`/eudamed/${encodeURIComponent(product.id)}/sources`)
    if (!live.current || request !== generation.current) return null
    setSources(values)
    return values
  }, [product.id])
  useEffect(() => {
    live.current = true
    void loadSources().catch(failure => { if (live.current) setError(failure instanceof Error ? failure.message : 'Quellen nicht geladen.') })
    return () => { live.current = false; generation.current++ }
  }, [loadSources])
  async function edit(previous?: EudamedDossier, reply?: { parent: EudamedDossier; correspondence: Correspondence }) {
    setBusy(true); setError('')
    try {
      const options = await loadSources()
      if (!options) return
      const target = previous?.kind ?? reply?.parent.kind ?? kind
      const base = previous ?? reply?.parent
      const originalSource = base?.package.sources.find(s => s.kind === primaryKinds[target] &&
        (target !== 'FSN' || s.source.id === base.package.content.fsn?.fscaId))
      const id = originalSource?.source.id ?? sourceId
      const source = options.find(o => o.id === id && o.kind === primaryKinds[target])
      if (!source) throw new Error('Eine konkrete verfuegbare Quellfassung auswaehlen.')
      const form = dossierForm(target, source, options, previous, reply)
      setDialog({ title: reply ? 'Originalgebundene Korrespondenzantwort' : previous ? 'Dossier neu fassen' : `${target}-Dossier entwerfen`,
        description: reply ? `Antwort ausschliesslich auf ${reply.correspondence.issuer}: ${reply.correspondence.content} Neue Medical-/RA-Freigaben; keine erneute native Einreichung.`
          : `Bindet ${source.label}, v${source.version}, und das ausdruecklich aktivierte Profil. Quellen, Sprache, Originale und Begruendungen werden eingefroren. Jede Aenderung braucht neue Freigaben.`,
        fields: form.fields, submitLabel: 'Dossierfassung speichern', onSubmit: async values => {
          const result = await api<EudamedDossier>(previous ? `/eudamed/dossiers/${previous.id}` : '/eudamed/dossiers', form.build(values))
          if (!live.current) return
          setSelectedId(result.id); await refresh()
        } })
    } catch (failure) { if (live.current) setError(failure instanceof Error ? failure.message : 'Dossierentwurf nicht vorbereitet.') }
    finally { if (live.current) setBusy(false) }
  }
  function upload() {
    if (!file) return
    const original = file
    if (original.size > 2 * 1024 * 1024 || !original.name.toLowerCase().endsWith('.pdf')) {
      setError('Nur ein ausdruecklich synthetisches PDF-Original bis 2 MiB auswaehlen.'); return
    }
    setDialog({ title: 'Synthetisches PDF-Original aufnehmen', description: `${original.name} (${original.size} Bytes). Keine echten Patientenunterlagen. Originalbytes werden unveraendert hashgebunden gespeichert, nicht in PDF umetikettiert.`,
      submitLabel: 'PDF-Original speichern', fields: [
        narrative('provenance', 'Explizite Originalprovenienz (mit SYNTHETISCH: beginnen)', undefined, 40), reasonField,
      ], onSubmit: async values => {
        const contentBase64 = await new Promise<string>((resolve, reject) => {
          const reader = new FileReader()
          reader.onerror = () => reject(new Error('PDF-Datei konnte nicht gelesen werden.'))
          reader.onload = () => typeof reader.result === 'string' && reader.result.includes(',')
            ? resolve(reader.result.slice(reader.result.indexOf(',') + 1)) : reject(new Error('PDF-Originaldaten fehlen.'))
          reader.readAsDataURL(original)
        })
        if (!live.current) return
        await api('/eudamed/documents', { productId: product.id, name: original.name, mediaType: 'application/pdf', contentBase64, ...values })
        if (!live.current) return
        setFile(null); if (fileInput.current) fileInput.current.value = ''
        await loadSources()
      } })
  }
  const selected = workspace.dossiers.find(d => d.id === selectedId)
  const primaries = sources.filter(o => o.kind === primaryKinds[kind] && (kind !== 'FSN' || o.dossierKind === 'FSCA'))
  return <>
    <section className="panel"><SectionHead eyebrow="4 - QUELLEN, REVIEW UND ORIGINALPAKET" title="EUDAMED-Dossiers">
      <button className="button secondary" disabled={busy} onClick={() => {
        void loadSources().catch(failure => { if (live.current) setError(failure instanceof Error ? failure.message : 'Quellen nicht geladen.') })
      }}>Dossierquellen neu lesen</button>
    </SectionHead>
      {error && <ErrorBanner message={error} dismiss={() => setError('')} />}
      <p>Fachlicher Entwurf, zwei getrennte Freigaben, Transport und fachliche Antwort sind eigenstaendige Schritte. Eine Antwort quittiert keine neue native Meldepflicht.</p>
      {author && <div className="eudamed-create">
        <label className="field">Dossiertyp<select value={kind} onChange={event => {
          const selected = Object.keys(kindNames).find((key): key is DossierKind => key === event.target.value)
          if (selected) { setKind(selected); setSourceId('') }
        }}>{Object.entries(kindNames).map(([value, name]) => <option key={value} value={value}>{name}</option>)}</select></label>
        <label className="field">Primaere versionierte Dossierquelle<select value={sourceId} onChange={event => setSourceId(event.target.value)}>
          <option value="">Konkrete Quelle auswaehlen</option>{primaries.map(o => <option key={o.id} value={o.id}>{sourceChoice(o).label}</option>)}
        </select></label>
        <button className="button primary" disabled={busy || !ready || !sourceId} onClick={() => void edit()}>Neues Dossier entwerfen</button>
        {!ready && <small>Zuerst Actor/Referenzen und ein vollstaendiges Simulatorprofil aktivieren. Kein externer Ersatzweg.</small>}
      </div>}
      {!workspace.dossiers.length && <p>Noch kein persistiertes Dossier in diesem Produktscope.</p>}
      {workspace.dossiers.map(d => <article className="eudamed-record" data-dossier-id={d.id} key={d.id}>
        <button className="case-title" onClick={() => setSelectedId(d.id)}>{d.title}</button>
        <p>{d.kind} · Paketrevision {d.revision} · Datensatz v{d.version} · <Badge>{state(d.reviewStatus)}</Badge>
          {d.openCorrespondence > 0 && <Badge tone="amber">{d.openCorrespondence} offene Rueckfragen</Badge>}</p>
        <small>Gebundener Kanal: {d.binding.profile.mode}/{d.binding.profile.environment} · Generation {d.binding.dispatchGeneration} · {d.binding.profile.recipient}</small>
      </article>)}
      {author && <details><summary>FSN: synthetisches PDF-Original aufnehmen</summary>
        <div className="eudamed-actions"><label className="field">Synthetisches PDF bis 2 MiB<input ref={fileInput} type="file" accept=".pdf,application/pdf" onChange={event => setFile(event.target.files?.[0] ?? null)} /></label>
          <button className="button secondary" disabled={!file || busy} onClick={upload}>PDF-Provenienz erfassen</button></div>
      </details>}
    </section>
    {selected && <DossierDetail key={selected.id} actor={actor} summary={selected} workspace={workspace} refresh={refresh}
      edit={edit} close={() => setSelectedId('')} open={setSelectedId} />}
    {dialog && <ActionDialog key={dialog.title} dialog={dialog} close={() => { if (live.current) setDialog(null) }} />}
  </>
}

function DossierDetail({ actor, summary, workspace, refresh, edit, close, open }: {
  actor: Actor; summary: DossierSummary; workspace: IntegrationWorkspace; refresh: () => Promise<void>
  edit: (previous?: EudamedDossier, reply?: { parent: EudamedDossier; correspondence: Correspondence }) => Promise<void>
  close: () => void; open: (id: string) => void
}) {
  const [view, setView] = useState<DossierView | null>(null)
  const [history, setHistory] = useState<EudamedDossier[] | null>(null)
  const [historicalVersion, setHistoricalVersion] = useState<number | null>(null)
  const [audit, setAudit] = useState<AuditEvent[] | null>(null)
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const [error, setError] = useState('')
  const live = useRef(true)
  const generation = useRef(0)
  const historyGeneration = useRef(0)
  const auditGeneration = useRef(0)
  const downloadGeneration = useRef(0)
  const downloadController = useRef<AbortController | null>(null)
  const load = useCallback(async () => {
    const request = ++generation.current
    try {
      const result = await api<DossierView>(`/eudamed/dossiers/${summary.id}`)
      if (live.current && request === generation.current) setView(result)
    } catch (failure) { if (live.current && request === generation.current) setError(failure instanceof Error ? failure.message : 'Dossier nicht geladen.') }
  }, [summary.id])
  useEffect(() => {
    live.current = true
    return () => {
      live.current = false; generation.current++; historyGeneration.current++; auditGeneration.current++
      downloadGeneration.current++; downloadController.current?.abort()
    }
  }, [])
  useEffect(() => { void load() }, [load, summary.version])
  const d = historicalVersion === null ? view?.dossier : history?.find(value => value.version === historicalVersion)
  if (!view || !d) return <section className="panel">{error ? <ErrorBanner message={error} /> : <Loading />}</section>
  const dossier = d
  const currentView = view
  const historical = historicalVersion !== null
  const medical = actor.isHuman && actor.roles.includes('MedicalSafetyReviewer')
  const regulatory = actor.isHuman && actor.roles.includes('RegulatoryAffairs')
  const author = actor.isHuman && (medical || regulatory || actor.roles.includes('PmsSpecialist'))
  const ownEntries = view.ledger.entries.filter(e => e.attempt.dossierRevision === dossier.revision)
  const latest = ownEntries.at(-1)
  const lastAttempt = dossier.attempts.filter(a => a.dossierRevision === dossier.revision).at(-1)
  const historicalBusiness = dossier.businessResponses.filter(r => r.attemptId === lastAttempt?.id && (r.applied || r.status === 'ActionMismatch'))
    .sort((left, right) => right.sequence - left.sequence)[0]?.status
  const businessStatus = historical ? historicalBusiness : latest?.businessStatus
  const pending = view.ledger.entries.some(e => ['Queued', 'Started', 'TransmissionUnknown'].includes(e.attempt.status) ||
    ['TransportAccepted', 'TransportDelivered'].includes(e.attempt.status) && ['Pending', 'PeriodMismatch', 'ActionMismatch'].includes(e.businessStatus))
  const profileCurrent = workspace.activeChannel?.enabled && workspace.activeReadiness.canDispatch &&
    workspace.activeChannel.id === dossier.package.binding.channelId &&
    workspace.activeChannel.version === dossier.package.binding.channelVersion &&
    workspace.activeChannel.dispatchGeneration === dossier.package.binding.dispatchGeneration
  async function change(path: string, body: unknown) {
    await api(path, body)
    if (!live.current) return
    setHistory(null); setHistoricalVersion(null); setAudit(null); historyGeneration.current++; auditGeneration.current++
    await load(); if (live.current) await refresh()
  }
  function approve() {
    setDialog({ title: dossier.reviewStatus === 'Draft' ? 'Dossier medizinisch freigeben' : 'Dossier regulatorisch einfrieren',
      description: `Paket ${dossier.scopeHash}. Bindet alle Quellen, PDF/XML, Sprache, Empfaenger und Originalkanal. Getrennte Konten; keine autonome Risikoakzeptanz.`,
      submitLabel: 'Genau dieses Paket freigeben', fields: [reasonField,
        ...(dossier.reviewStatus === 'Draft' ? [narrative('findingDisposition', 'Medizinische Einordnung offener Befunde und verbleibender Unsicherheit', undefined, 80)] : [])],
      onSubmit: async v => change(`/eudamed/dossiers/${dossier.id}/approve`,
        { expectedVersion: dossier.version, reason: v.reason, findingDisposition: v.findingDisposition ?? null }) })
  }
  function submit() {
    setDialog({ title: dossier.package.content.intendedAction === 'Reply' ? 'Freigegebene Antwort simuliert senden' : 'Freigegebenes Dossier simuliert senden',
      description: `Originalprofil ${dossier.package.binding.profile.recipient}, Generation ${dossier.package.binding.dispatchGeneration}. Handlung ${dossier.package.content.intendedAction}. Transport ist keine fachliche Annahme. Keine realen Netzwerkaufrufe.`,
      submitLabel: 'Originalgebunden einplanen', fields: [
        { name: 'scenario', label: 'Explizites Dossier-Simulatorszenario', type: 'select', value: '',
          options: [{ value: '', label: 'Szenario bewusst auswaehlen' }, ...Object.entries(scenarios).filter(([key]) => key !== 'PeriodMismatch' || dossier.kind === 'PSRP').map(([value, label]) => ({ value, label }))] },
        reasonField,
      ], onSubmit: async v => change(`/eudamed/dossiers/${dossier.id}/submit`,
        { expectedVersion: dossier.version, expectedLedgerVersion: currentView.ledger.version, ...v }) })
  }
  function advance(eventId: string, duplicate: boolean) {
    setDialog({ title: duplicate ? 'Originaleingang erneut zustellen' : 'Persistierte Simulatorantwort einlesen',
      description: 'Verwendet ausschliesslich das gespeicherte Ereignis des urspruenglichen Versuchs. Original, Eingang und fachliche Projektion bleiben getrennt. Auch ein deaktivierter Kanal wird nicht umgeleitet.',
      submitLabel: duplicate ? 'Duplikat beobachten' : 'Originalereignis einlesen', fields: [reasonField],
      onSubmit: async v => change(`/eudamed/dossiers/${dossier.id}/simulator`,
        { expectedVersion: dossier.version, eventId, duplicate, reason: v.reason }) })
  }
  async function original(originalHash: string, filename: string) {
    const request = ++downloadGeneration.current
    downloadController.current?.abort()
    const controller = new AbortController()
    downloadController.current = controller
    try {
      await downloadApi(`/eudamed/dossiers/${dossier.id}/originals/${originalHash}?version=${dossier.version}`, filename,
        { signal: controller.signal, isCurrent: () => live.current && request === downloadGeneration.current })
    } catch (failure) {
      if (live.current && request === downloadGeneration.current)
        setError(failure instanceof Error ? failure.message : 'Originaldownload nicht bestaetigt.')
    }
  }
  async function showHistory() {
    const request = ++historyGeneration.current
    try {
      const result = await api<EudamedDossier[]>(`/eudamed/dossiers/${dossier.id}/history`)
      if (live.current && request === historyGeneration.current) setHistory(result)
    } catch (failure) { if (live.current && request === historyGeneration.current) setError(failure instanceof Error ? failure.message : 'Historie nicht geladen.') }
  }
  async function showAudit(kind: string, id: string) {
    const request = ++auditGeneration.current
    try {
      const result = await api<AuditEvent[]>(`/eudamed/audit/${kind}/${id}`)
      if (live.current && request === auditGeneration.current) setAudit(result)
    } catch (failure) { if (live.current && request === auditGeneration.current) setError(failure instanceof Error ? failure.message : 'Audit nicht geladen.') }
  }
  return <section className="panel eudamed-dossier-detail" aria-label="Dossier-Workbench">
    <SectionHead eyebrow={`${dossier.kind} - ${historical ? 'HISTORISCH / NUR LESEN' : 'AKTUELLE FASSUNG'}`} title={dossier.package.content.title}>
      <button className="button secondary" onClick={close}>Dossier schliessen</button>
    </SectionHead>
    {error && <ErrorBanner message={error} dismiss={() => setError('')} />}
    <div className="eudamed-status-grid">
      <div><h3>Menschliche Freigabe</h3><Badge>{state(dossier.reviewStatus)}</Badge><p>Paketrevision {dossier.revision} · Datensatz v{dossier.version}</p></div>
      <div><h3>Technischer Transport</h3><Badge tone="amber">{lastAttempt ? state(lastAttempt.status) : 'Nicht eingeplant'}</Badge></div>
      <div><h3>Fachliche Rueckmeldung</h3><Badge tone="amber">{businessStatus ? state(businessStatus) : 'Keine Antwort'}</Badge><p>Handlung: {dossier.package.content.intendedAction}</p></div>
    </div>
    <p className="callout">Nur synthetisch. Kein ACK, keine simulierte Annahme und keine fehlende Rueckfrage sind eine CA-/NB-Sicherheitsentscheidung.</p>
    <div className="eudamed-actions">
      {!historical && author && !pending && <button className="button secondary" onClick={() => void edit(dossier)}>Neue Dossierfassung</button>}
      {!historical && profileCurrent && !pending &&
        (medical && dossier.reviewStatus === 'Draft' || regulatory && dossier.reviewStatus === 'MedicalApproved') &&
        !dossier.approvals.some(a => a.actorId === actor.id) &&
        <button className="button primary" onClick={approve}>{dossier.reviewStatus === 'Draft' ? 'Medical-Dossierfreigabe' : 'RA-Dossierfreigabe'}</button>}
      {!historical && regulatory && profileCurrent && !pending && dossier.reviewStatus === 'Frozen' &&
        !ownEntries.some(e => ['BusinessAccepted', 'DraftCreated', 'BusinessRejected'].includes(e.businessStatus)) &&
        <button className="button primary" onClick={submit}>Paket dem Simulator uebergeben</button>}
      {dossier.frozenPackageHash && <button className="button secondary" onClick={() => void original(dossier.frozenPackageHash!, `COHORS-DEMO-${dossier.id}-r${dossier.revision}.json`)}>Eingefrorenes Originalpaket</button>}
      <button className="button secondary" onClick={() => void showHistory()}>Dossierhistorie lesen</button>
      <button className="button secondary" onClick={() => void showAudit('eudamedDossier', dossier.id)}>Dossier-Audit</button>
      <button className="button secondary" onClick={() => void showAudit('eudamedDelivery', currentView.ledger.id)}>Versand-Audit</button>
      {historical && <button className="button secondary" onClick={() => setHistoricalVersion(null)}>Zur aktuellen Fassung</button>}
    </div>
    {pending && !historical && <p className="callout">Laufender oder ungeklaerter Originalversuch: kein Zweitversand und keine Umleitung. Gespeicherte Rueckmeldung einlesen; Widersprueche nicht als Erfolg umetikettieren.</p>}
    {!profileCurrent && <p className="callout">Dieses Originalprofil ist nicht mehr fuer neue Claims aktiv. Alte Versuche und Originalantworten behalten ihre Kanalbindung.</p>}
    <h3>Medizinisch-technische und regulatorische Begruendung</h3>
    <p className="eudamed-narrative">{dossier.package.content.clinicalRationale}</p>
    <p className="eudamed-narrative">{dossier.package.content.regulatoryRationale}</p>
    <dl className="eudamed-bindings"><dt>Signierter Pakethash</dt><dd>{dossier.scopeHash}</dd>
      <dt>Originalprofil</dt><dd>{dossier.package.binding.profile.mode}/{dossier.package.binding.profile.environment} · {dossier.package.binding.profile.recipient} · Generation {dossier.package.binding.dispatchGeneration}</dd>
      <dt>Profilhash</dt><dd>{dossier.package.binding.profileHash}</dd>
      <dt>Sprache / Kalender</dt><dd>{dossier.package.content.language} · {dossier.package.content.jurisdiction} · {dossier.package.calendar.version} · {dossier.package.calendar.timeZone}</dd>
      <dt>Vertragspruefung</dt><dd>{dossier.package.validation.schemaVersion} · {dossier.package.validation.mappingVersion} · {dossier.package.validation.evidenceLevel}</dd>
    </dl>
    {dossier.package.validation.errors.map(value => <ErrorBanner key={value} message={value} />)}
    <details><summary>Pruefgrenzen und Quellenmanifest ({dossier.package.sources.length})</summary>
      <ul>{dossier.package.validation.warnings.map(value => <li key={value}>{value}</li>)}</ul>
      {dossier.package.sources.map(source => <details key={`${source.kind}:${source.source.id}`} className="eudamed-round">
        <summary>{source.kind} · {source.source.id} · v{source.source.version} · {hash(source.source.hash)}</summary>
        <pre className="eudamed-audit">{source.json}</pre>
      </details>)}
    </details>
    <details><summary>Unveraenderte Fach-XML-Projektion · {hash(dossier.package.businessXmlHash)}</summary><pre className="eudamed-audit">{dossier.package.businessXml}</pre></details>
    <h3>Freigaben genau dieses Pakets</h3>
    {!dossier.approvals.length && <p>Noch keine menschliche Freigabe.</p>}
    {dossier.approvals.map(approval => <article key={approval.id} className="eudamed-round">
      <b>{roleNames[approval.role] ?? approval.role} · {approval.actorName}</b><p>{approval.rationale}</p>
      <small>{date(approval.at, true)} · Signatur {approval.id} · Scope {hash(approval.scopeHash)}</small>
    </article>)}
    <div className="eudamed-actions">{dossier.package.attachments.map(attachment =>
      <button key={attachment.hash} className="button secondary" onClick={() => void original(attachment.hash, attachment.name)}>
        PDF-Original: {attachment.name} · {hash(attachment.hash)}</button>)}</div>
    <h3>Versuche und persistierte Simulatorereignisse</h3>
    {dossier.attempts.map(attempt => <article className="eudamed-record" key={attempt.id} data-attempt-id={attempt.id}>
      <b>{state(attempt.status)} · Paketrevision {attempt.dossierRevision} · {scenarios[attempt.scenario] ?? attempt.scenario}</b>
      <p>Korrelation {attempt.correlationId} · Originalgeneration {attempt.binding.dispatchGeneration} · {attempt.binding.profile.recipient}</p>
      {attempt.errorCode && <p>{attempt.errorCode}</p>}
      {attempt.receiptHash && <button className="button secondary" onClick={() => void original(attempt.receiptHash!, `${attempt.id}-transport.json`)}>Technischen Originalbeleg lesen</button>}
      {dossier.plannedResponses.filter(event => event.attemptId === attempt.id).map(event => <div key={event.id} className="eudamed-round">
        <b>{event.kind} · Sequenz {event.sequence} · {event.status}</b><p>Faellig {date(event.dueAt, true)}</p>
        {!historical && regulatory && attempt.binding.simulated && <button className="button secondary"
          disabled={new Date(event.dueAt).getTime() > Date.now()} onClick={() => advance(event.id, event.status === 'Published')}>
          {event.status === 'Published' ? 'Originalduplikat zustellen' : 'Simulatorereignis einlesen'}</button>}
      </div>)}
    </article>)}
    <h3>Fachliche Originalantworten</h3>
    {dossier.businessResponses.map(response => <article key={response.inboxId} className="eudamed-record">
      <b>{state(response.status)} · Sequenz {response.sequence} · {response.applied ? 'Auf diesen Stand angewandt' : 'Original behalten, nicht angewandt'}</b>
      <p>{response.dossierCode ?? 'Kein Remote-Dossierkennzeichen'} · {date(response.at, true)}</p>
      {response.returnedPeriod && <p>Zurueckgemeldete Periode: {response.returnedPeriod.start} bis {response.returnedPeriod.end}</p>}
      {response.errors.map(value => <p key={value}>{value}</p>)}
      <button className="button secondary" onClick={() => void original(response.originalHash, `${response.inboxId}.json`)}>Fachliches Original lesen</button>
    </article>)}
    <h3>Originalgebundene Kommentare und Nachforderungen</h3>
    {dossier.correspondence.map(item => <article key={item.id} className="eudamed-record" data-correspondence-id={item.id}>
      <b>{item.kind} · {item.issuer} · {state(item.status)}</b><p className="eudamed-narrative">{item.content}</p>
      <p>Verantwortlich: {roleNames[item.ownerRole] ?? item.ownerRole} · {item.dueAt ? `Antworttermin ${date(item.dueAt, true)}` : 'Kein eigener Termin angegeben'}</p>
      <div className="eudamed-actions"><button className="button secondary" onClick={() => void original(item.originalHash, `${item.id}.json`)}>Korrespondenzoriginal lesen</button>
        {!historical && author && item.status === 'Open' && !pending && <button className="button primary"
          onClick={() => void edit(undefined, { parent: dossier, correspondence: item })}>Getrennte Antwort entwerfen</button>}
        {item.replyDossierId && <button className="button secondary" onClick={() => open(item.replyDossierId!)}>Freigegebene Antwort oeffnen</button>}</div>
    </article>)}
    {history && <section><h3>Dossierhistorie - unveraenderte Originalstaende</h3><div className="eudamed-actions">
      {history.map(value => <button className="button secondary" key={value.version} onClick={() => setHistoricalVersion(value.version)}>
        v{value.version} / Paket r{value.revision} / {state(value.reviewStatus)}</button>)}
    </div></section>}
    {audit && <section><h3>Originalgebundener Auditverlauf</h3>{audit.map(item => <article className="eudamed-round" key={item.sequence}>
      <b>{item.action} · v{item.version} · {item.actorName}</b><p>{item.reason}</p><small>{date(item.at, true)} · {hash(item.hash)}</small>
    </article>)}</section>}
    {dialog && <ActionDialog key={dialog.title} dialog={dialog} close={() => { if (live.current) setDialog(null) }} />}
  </section>
}
