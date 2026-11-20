import { useRef, useState } from 'react'
import { FileText, Link2, PencilLine, Sparkles } from 'lucide-react'
import { downloadApi } from './api'
import type { MirFacts, MirSimilarIncidents, Report, SafetyCase } from './types'
import { Badge, date, ErrorBanner, hash, type Dialog, type Field } from './ui'

type FactKey = Exclude<keyof MirFacts, 'evidenceIds' | 'rationale' | 'similarIncidents' | 'submitterIsManufacturerContact'>
const facts: { name: FactKey; label: string; type?: Field['type']; final?: boolean; afterInitial?: boolean; options?: string[] }[] = [
  { name: 'manufacturerName', label: 'Herstellername · führendes Produktprofil' },
  { name: 'manufacturerSrn', label: 'Hersteller-SRN · synthetisches Produktprofil' },
  { name: 'manufacturerCountry', label: 'Herstellerland · ISO-Ländercode' },
  { name: 'contactFirstName', label: 'Belegter Hersteller-/Melderkontakt · Vorname' },
  { name: 'contactLastName', label: 'Belegter Hersteller-/Melderkontakt · Nachname' },
  { name: 'contactStreet', label: 'Kontaktanschrift · Straße' },
  { name: 'contactCity', label: 'Kontaktanschrift · Ort' },
  { name: 'contactPostcode', label: 'Kontaktanschrift · Postleitzahl' },
  { name: 'contactPhone', label: 'Belegte Kontakt-Telefonnummer' },
  { name: 'contactEmail', label: 'Belegte Kontakt-E-Mail' },
  { name: 'eventDateFrom', label: 'Belegtes Ereignisdatum von', type: 'date' },
  { name: 'eventDateTo', label: 'Belegtes Ereignisdatum bis', type: 'date' },
  { name: 'eventDateRationale', label: 'Begründung einer belegten Spanne bei unbekanntem Ereignisinstant', type: 'textarea' },
  { name: 'authorityReference', label: 'NCA-Aktenzeichen / ausdrücklich begründetes Unknown · keine Simulatorquittung', afterInitial: true },
  { name: 'authorityReferenceRationale', label: 'Beleg für NCA-Referenz oder konkrete Unknown-Begründung', type: 'textarea', afterInitial: true },
  { name: 'eventClassification', label: 'Belegte MIR-Ereignisklassifikation', type: 'select',
    options: ['Serious public health threat', 'Death', 'Unanticipated serious deterioration in state of health', 'All other reportable incidents'] },
  { name: 'deviceProblemCode', label: 'Problem-Code (synthetischer Testcode, z. B. ASYN; keine geprüfte IMDRF-Semantik)' },
  { name: 'deviceLocation', label: 'Tatsächlich bekannter Verbleib', type: 'select',
    options: ['Healthcare facility/carer', 'Patient/user', 'In transit to manufacturer', 'Manufacturer', 'Distributor', 'Discarded', 'Remains implanted', 'Unknown'] },
  { name: 'nextReportDate', label: 'Menschlich zugesagter fester Folgeberichtstermin', type: 'date', final: false },
  { name: 'preliminaryAnalysis', label: 'Begründete Ursachenanalyse und Unsicherheit', type: 'textarea' },
  { name: 'initialCorrectiveAction', label: 'Bereits belegte erste Maßnahmen (keine erfundene Durchführung)', type: 'textarea' },
  { name: 'furtherInvestigation', label: 'Gezielter weiterer Untersuchungsplan / verbleibende Nachverfolgung', type: 'textarea' },
  { name: 'udiDi', label: 'Belegte UDI-DI (Pilot: ausschließlich fiktive Kennung)', final: true },
  { name: 'udiPi', label: 'Belegte UDI-PI / Charge', final: true },
  { name: 'udiEntity', label: 'UDI-Vergabestelle (N/A nur für das synthetische Testprofil)', final: true, type: 'select', options: ['N/A', 'GS1', 'HIBCC', 'ICCBBA', 'IFA', 'EUDAMED'] },
  { name: 'basicUdiEntity', label: 'Basic-UDI-Vergabestelle', final: true, type: 'select', options: ['N/A', 'GS1', 'HIBCC', 'ICCBBA', 'IFA', 'EUDAMED'] },
  { name: 'distribution', label: 'Belegte Verteilung / betroffene Märkte', final: true, type: 'textarea' },
  { name: 'rootCauses', label: 'Untersuchungsergebnis und verbleibende Kausalitätsunsicherheit', final: true, type: 'textarea' },
  { name: 'rootCauseConfirmed', label: 'Ursache tatsächlich bestätigt?', final: true, type: 'select', options: ['Yes', 'No'] },
  { name: 'riskAssessmentReviewed', label: 'Risikobewertung tatsächlich geprüft?', final: true, type: 'select', options: ['Yes', 'No'] },
  { name: 'rationaleNoRiskReview', label: 'Bei Nein: konkrete Begründung der nicht erfolgten Risikoprüfung', final: true, type: 'textarea' },
  { name: 'riskAssessmentAdequate', label: 'Bei erfolgtem Review: ist die Risikobewertung angemessen?', final: true, type: 'select', options: ['Yes', 'No'] },
  { name: 'riskAssessmentResults', label: 'Bei erfolgtem Review: Ergebnisse und verbleibende Grenzen', final: true, type: 'textarea' },
  { name: 'investigationTypeCode', label: 'Untersuchungstyp-Code (nur synthetischer Testcode)', final: true },
  { name: 'investigationFindingCode', label: 'Befund-Code (nur synthetischer Testcode)', final: true },
  { name: 'investigationConclusionCode', label: 'Schlussfolgerungs-Code (nur synthetischer Testcode)', final: true },
  { name: 'componentCode', label: 'Komponenten-Code (nur synthetischer Testcode)', final: true },
  { name: 'correctiveAction', label: 'Begründete Maßnahmen und Nachverfolgung', final: true, type: 'textarea' },
  { name: 'finalComments', label: 'Abschließende Kommentare / ausdrücklich verbleibende Grenzen', final: true, type: 'textarea' },
]

const originNames: Record<string, string> = { LeadingData: 'Führende Falldaten', ProductProfile: 'Produkt-/Herstellerprofil', TargetProfile: 'Zielprofil',
  System: 'Systemmetadatum', AgentDraft: 'Agentenvorschlag · unbestätigt', AssessmentDraft: 'Assessment-Auszug · neuer Review nötig',
  HumanCoding: 'Menschliche Kodierung · neuer Review nötig', PriorMedicalReport: 'Bisherige medizinische Angabe · neuer Review nötig',
  HumanEntry: 'Menschlich ergänzt · noch keine Paketfreigabe', Missing: 'Fehlend / offen',
  ControlledContactEvidence: 'Freigegebene Kontaktquelle · Vorschlag, neuer Review nötig',
  ExistingReportEntry: 'Vorhandene Angabe · unbestätigt im neuen Paket' }
const systemNames: Record<string, string> = { $reference: 'Hersteller-Fallreferenz', $description: 'Originaler Ereignistext',
  $country: 'Dokumentiertes Ereignisland',
  $firstAwareness: 'Erste Organisationskenntnis', $reportabilityAwareness: 'Kenntnis gemäß gebundener Meldepflicht',
  $reportDate: 'Entwurfsdatum', $authority: 'Fachlich zuständige Behörde', $simulator: 'Technischer Pilotempfänger',
  $device: 'Produkt / Modell', $basicUdi: 'Basic-UDI', $batch: 'Dokumentierte Charge',
  submitterIsManufacturerContact: 'Melder ist der belegte Herstellerkontakt', similarIncidents: 'Ähnliche Ereignisse / Perioden / Nenner',
  $contact: 'Hersteller-/Melderkontakt', $target: 'Zielprofil und nationale Pflichtangaben', $schema: 'Formularstruktur / Werteprüfung',
  $reportHistory: 'Abgeglichener Vorbericht', evidenceIds: 'Konkrete freigegebene Fallbelege', rationale: 'Medizinisch-technische Begründung' }
const reviewGroups: { key: string; title: string; fields: string[] }[] = [
  { key: 'assessment', title: 'Bewertung, Ursachen- und Risikoanalyse', fields: [
    'preliminaryAnalysis', 'rootCauses', 'rootCauseConfirmed', 'riskAssessmentReviewed', 'riskAssessmentAdequate',
    'riskAssessmentResults', 'rationaleNoRiskReview', 'eventClassification', 'deviceProblemCode', 'investigationTypeCode',
    'investigationFindingCode', 'investigationConclusionCode', 'componentCode', 'similarIncidents', 'rationale',
  ] },
  { key: 'actions', title: 'Begründete Maßnahmen und Nachverfolgung', fields: [
    'initialCorrectiveAction', 'correctiveAction', 'furtherInvestigation', 'nextReportDate', 'finalComments',
  ] },
  { key: 'incident', title: 'Ereignis und betroffenes Produkt', fields: [
    '$description', 'eventDateFrom', 'eventDateTo', 'eventDateRationale', '$country', '$device', 'deviceLocation',
    'distribution', 'udiDi', 'udiPi', '$batch', '$basicUdi', 'udiEntity', 'basicUdiEntity',
  ] },
  { key: 'administration', title: 'Hersteller, Kontakt und Meldeverwaltung', fields: [
    '$reference', 'manufacturerName', 'manufacturerSrn', 'manufacturerCountry', 'contactFirstName', 'contactLastName',
    'contactStreet', 'contactCity', 'contactPostcode', 'contactPhone', 'contactEmail', 'submitterIsManufacturerContact',
    '$contact', '$firstAwareness', '$reportabilityAwareness', 'authorityReference', 'authorityReferenceRationale',
    '$authority', '$simulator', '$reportDate', '$target', '$schema', '$reportHistory', 'evidenceIds',
  ] },
]
const issueReasons: Record<string, string> = { MissingSource: 'Fehlende oder nicht verwendbare Quelle', ConflictingSources: 'Widersprüchliche Angaben',
  HumanDecision: 'Menschliche Entscheidung erforderlich', Unsupported: 'Derzeit nicht unterstützte Konstellation' }
function fieldLabel(name: string) { return facts.find(f => f.name === name)?.label ?? systemNames[name] ?? 'Weitere Meldeangabe' }
function friendlyLegacyError(text: string) {
  for (const [name, title] of [...facts.map(f => [f.name, f.label]), ...Object.entries(systemNames)]
    .sort(([a], [b]) => b.length - a.length)) {
    text = text.replaceAll(name[0].toUpperCase() + name.slice(1), title)
  }
  return text
}

function isSimilarIncidents(value: unknown): value is MirSimilarIncidents {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false
  const strings = ['variant', 'details', 'denominatorBasis', 'method', 'geography']
  if (!strings.every(key => typeof Reflect.get(value, key) === 'string') ||
    !('otherDenominator' in value) || value.otherDenominator !== null && typeof value.otherDenominator !== 'string' ||
    !('periods' in value) || !Array.isArray(value.periods)) return false
  return value.periods.every((p: unknown) => typeof p === 'object' && p !== null &&
    ['start', 'end', 'missingDataReason'].every(key => typeof Reflect.get(p, key) === 'string') &&
    ['countryIncidents', 'regionalIncidents', 'worldwideIncidents', 'countryDevices', 'regionalDevices', 'worldwideDevices']
      .every(key => { const n: unknown = Reflect.get(p, key); return n === null || typeof n === 'number' && Number.isInteger(n) && n >= 0 }))
}

function SimilarReview({ value }: { value: MirSimilarIncidents }) {
  return <div><p>{value.variant}: {value.details}</p><p><b>Methode:</b> {value.method}</p><p><b>Geografien:</b> {value.geography}</p>
    <p><b>Nennerbasis:</b> {value.denominatorBasis}{value.otherDenominator ? ` · ${value.otherDenominator}` : ''}</p>
    {value.periods.map((p, i) => <div key={i} className="report-target"><b>{p.start} bis {p.end}</b><div className="table-wrap"><table>
      <thead><tr><th>Region</th><th>Ähnliche Ereignisse</th><th>Exposition</th></tr></thead><tbody>
        <tr><td>Ereignisland</td><td>{p.countryIncidents ?? 'Nicht verfügbar'}</td><td>{p.countryDevices ?? 'Nicht verfügbar'}</td></tr>
        <tr><td>EWR / CH / TR</td><td>{p.regionalIncidents ?? 'Nicht verfügbar'}</td><td>{p.regionalDevices ?? 'Nicht verfügbar'}</td></tr>
        <tr><td>Weltweit, nicht addiert</td><td>{p.worldwideIncidents ?? 'Nicht verfügbar'}</td><td>{p.worldwideDevices ?? 'Nicht verfügbar'}</td></tr>
      </tbody></table></div>{p.missingDataReason && <p className="callout">{p.missingDataReason}</p>}</div>)}
  </div>
}

export default function MirPanel({ c, report, canEdit, busy, openDialog, mutate, showClaim, navigate }: {
  c: SafetyCase; report: Report; canEdit: boolean; busy: boolean;
  openDialog: (dialog: Dialog) => void; mutate: (path: string, body: unknown) => Promise<void>; showClaim: (id: string) => void;
  navigate: (tab: string) => void
}) {
  const [error, setError] = useState('')
  const technical = useRef<HTMLDetailsElement>(null)
  const mir = report.mir
  const preparation = report.preparation
  const editable = canEdit && report.jurisdiction !== 'GB' && ['Draft', 'MedicalApproved', 'Frozen'].includes(report.state)
  const issues = mir?.checks?.completionIssues ?? preparation?.problems ?? []
  const legacyGaps = mir?.checks?.completionIssues == null ? (mir?.checks?.contentErrors ?? mir?.errors ?? []).filter(e => !e.startsWith('XSD ')) : []
  const visibleFacts = facts.filter(f => (f.final === undefined || f.final === (report.kind === 'Final')) && (!f.afterInitial || report.kind !== 'Initial'))
  const visibleFields = preparation?.fields.filter(field => {
    const knownFact = facts.some(f => f.name === field.field) || ['submitterIsManufacturerContact', 'similarIncidents'].includes(field.field)
    if (field.value !== null || !knownFact) return true
    if (report.jurisdiction === 'GB') return ['preliminaryAnalysis', 'furtherInvestigation'].includes(field.field)
    return visibleFacts.some(f => f.name === field.field) || field.field === 'submitterIsManufacturerContact' ||
      field.field === 'similarIncidents' && report.kind === 'Final'
  }) ?? []
  const groupedFields = [
    ...reviewGroups.map(group => ({ ...group, fields: group.fields.flatMap(name => visibleFields.filter(field => field.field === name)) })),
    { key: 'other', title: 'Weitere Meldeangaben', fields: visibleFields.filter(field => !reviewGroups.some(group => group.fields.includes(field.field))) },
  ].filter(group => group.fields.length > 0)
  function edit(requested?: string) {
    const final = report.kind === 'Final'
    const focus = requested === '$contact' ? 'contactFirstName' : requested
    const editableFacts = facts.filter(f => visibleFacts.includes(f) || f.name === focus)
      .sort((a, b) => a.name === focus ? -1 : b.name === focus ? 1 : 0)
    openDialog({
      title: 'Fehlende Angaben / Korrekturen belegen',
      description: 'Vorhandene Werte sind vorausgefüllt. Nur belegte synthetische Angaben ergänzen; nichts zur Beseitigung eines Fehlers erfinden. Leere Angaben dürfen als offene Lücken gespeichert werden, sperren aber nötigenfalls die Freigabe. Ein bewusst eingegebener Folgetermin ist eine menschliche Zusage, kein Agententermin. Änderungen brauchen neue Medical- und RA-Freigaben.',
      fields: [
        ...editableFacts.map(f => ({
          ...f, required: false, value: mir?.facts[f.name] ?? '',
          options: f.options ? [{ value: '', label: 'Belegten Wert auswählen' }, ...f.options.map(value => ({ value, label: value }))] : undefined,
        })),
        { name: 'submitterIsManufacturerContact', label: 'Belegt: der genannte Herstellerkontakt ist auch Melder',
          type: 'select', required: false, value: mir?.facts.submitterIsManufacturerContact == null ? '' : String(mir.facts.submitterIsManufacturerContact),
          options: [{ value: '', label: 'Nicht bestätigt' }, { value: 'true', label: 'Ja, ausdrücklich belegt' }, { value: 'false', label: 'Nein · andere Melderkonstellation derzeit nicht implementiert' }] },
        ...(final ? [{ name: 'similarIncidents', label: 'Belegter Ähnlichkeitsreview als JSON: variant, details, denominatorBasis, otherDenominator, method, geography und periods (N bis N-3). Fehlende Zahlen null mit missingDataReason, niemals erfundene 0.',
          type: 'textarea' as const, required: false, value: mir?.facts.similarIncidents ? JSON.stringify(mir.facts.similarIncidents, null, 2) : '' }] : []),
        { name: 'evidenceIds', label: 'Konkrete Evidenz-IDs, eine pro Zeile (unten verfügbare Fundstellen prüfen)', type: 'textarea',
          value: mir?.facts.evidenceIds.join('\n') ?? '' },
        { name: 'rationale', label: 'Medizinisch-technische Begründung der Angaben und Testkodierungen', type: 'textarea', value: mir?.facts.rationale, minLength: 40 },
        ...((preparation?.problems?.length ?? 0) > 0 ? [{
          name: 'resolveSourceProblems', label: 'Automatisch erkannte Quellenprobleme ausdrücklich menschlich geklärt?',
          type: 'select' as const, value: 'false', options: [
            { value: 'false', label: 'Nein · Quellenprobleme bleiben offen und sperren die Freigabe' },
            { value: 'true', label: 'Ja · die konkrete Klärung und ausgewählten Belege begründe ich ausdrücklich' },
          ],
        }] : []),
        { name: 'reason', label: 'Änderungsbegründung / prüfbare Herkunft', type: 'textarea', minLength: (preparation?.problems?.length ?? 0) > 0 ? 40 : 30 },
      ],
      submitLabel: 'Versioniert speichern und gegen Original-XSD prüfen',
      onSubmit: values => {
        const body: MirFacts = { ...mir?.facts, evidenceIds: values.evidenceIds.split(/\r?\n/).map(x => x.trim()).filter(Boolean), rationale: values.rationale }
        for (const f of editableFacts) body[f.name] = values[f.name] || null
        body.submitterIsManufacturerContact = values.submitterIsManufacturerContact ? values.submitterIsManufacturerContact === 'true' : null
        if (values.similarIncidents?.trim()) {
          const parsed: unknown = JSON.parse(values.similarIncidents)
          if (!isSimilarIncidents(parsed)) throw new Error('Ähnlichkeitsreview: vollständiges strukturiertes Objekt mit expliziten Perioden, Zahlen oder null und Datenlückenbegründung erforderlich.')
          body.similarIncidents = parsed
        } else if (final) body.similarIncidents = null
        return mutate(`/cases/${c.id}/mir`, { expectedVersion: c.version, facts: body, reason: values.reason,
          resolveSourceProblems: values.resolveSourceProblems === 'true' })
      },
    })
  }
  function complete() {
    openDialog({
      title: 'Pflichtfelder erneut durch Agenten vervollständigen',
      description: 'Derselbe Autorenweg prüft den bestehenden Bericht gegen aktuell gebundene Daten und ausdrücklich freigegebene Kontaktquellen. Bekannte Eingaben und eine bereits menschlich zugesagte Frist bleiben erhalten. Widersprüche werden nicht überschrieben; fehlende Fakten bleiben offen. Es entsteht eine neue unbestätigte Fassung. Frühere Signaturen bleiben nur historische Belege, keine aktuelle Freigabe. Kein Versand.',
      fields: [{ name: 'reason', label: 'Begründung des erneuten Vervollständigungsversuchs', type: 'textarea', minLength: 30,
        value: 'Belegte Pflichtangaben erneut automatisch prüfen; bekannte menschliche Eingaben erhalten und verbleibende Lücken offen ausweisen.' }],
      submitLabel: 'Erneut vervollständigen',
      onSubmit: values => mutate(`/cases/${c.id}/report`, { expectedVersion: c.version, kind: report.kind,
        jurisdiction: report.jurisdiction, language: report.language, preliminary: report.preliminary,
        openQuestions: report.openQuestions, reason: values.reason, completeCurrent: true }),
    })
  }
  function showTechnical() {
    if (technical.current) {
      technical.current.open = true
      technical.current.scrollIntoView({ block: 'center' })
      technical.current.querySelector('summary')?.focus()
    }
  }
  async function download(path: string, filename: string) {
    setError('')
    try { await downloadApi(path, filename) } catch (e) { setError(e instanceof Error ? e.message : 'Export nicht bestätigt.') }
  }
  return <section className="qa-panel">
    <div className="row-between"><h3>Prüfansicht des vorausgefüllten Meldungsentwurfs</h3><Badge tone="amber">{preparation?.mode ?? 'Altstand'} · keine automatische Freigabe</Badge></div>
    {preparation ? <div className="report-target">
      <p><b>Fachlich zuständige Behörde:</b> {preparation.target.authority}</p>
      <p><b>Technischer Empfänger:</b> {preparation.target.simulator} · ausschließlich Pilot-Simulator</p>
      <p>{preparation.target.jurisdiction} · {preparation.target.formFamily} · {preparation.target.version}</p>
      <details><summary>Nationale Versandbereitschaft nicht nachgewiesen</summary>{preparation.target.externalRequirements.map(text => <p key={text}>{text}</p>)}</details>
      <small>Quellfall v{preparation.caseSource.version} · {hash(preparation.caseSource.hash)}. Vollständige Herkunft wird mit dem Paket gebunden.</small>
    </div> : <p className="callout">Historisches Minimal-Mapping, kein nachträglich behaupteter Vorbefüllungs-/Vollständigkeitsnachweis. Für einen neuen Prüfstand einen eigenen Entwurf vorbereiten.</p>}
    <div className="row">
      <Badge tone={mir?.checks?.schemaValid ? 'blue' : 'amber'}>{mir?.checks?.schemaValid ? 'Struktur geprüft' : report.jurisdiction === 'GB' ? 'GB-MIR nicht implementiert' : 'XML-Struktur offen'}</Badge>
      <Badge tone={mir?.valid ? 'blue' : 'amber'}>{report.jurisdiction === 'GB' ? 'Nur GB-SIM-Narrativ, keine GB-MIR-Freigabe' : mir?.valid ? mir.checks ? 'Implementierte Pilot-Pflichtprüfungen bestanden' : 'Historische Formularprüfung · kein neuer Vollständigkeitsnachweis' : 'Freigaberelevante Lücken offen'}</Badge>
    </div>
    <p className="muted">XSD-Konformität, fachlicher Human Review und nationale Versandbereitschaft sind getrennt. Medical Safety prüft weiterhin zuerst; RA prüft das konkrete Paket oder gibt es zur Korrektur an PMS zurück. Original-PDF und Schutz bleiben unverändert.</p>
    {error && <ErrorBanner message={error} />}
    {editable && <button disabled={busy} className="button secondary" onClick={complete}><Sparkles size={16} />Pflichtfelder erneut durch Agenten vervollständigen</button>}
    {(issues.length > 0 || legacyGaps.length > 0) && <section className="callout" data-testid="report-gaps" aria-label="Nicht automatisch befüllbare Angaben">
      <h3>Nicht automatisch befüllbar – Angaben oder Belege erforderlich</h3>
      <p>Erforderliche Lücken und ungeklärte Widersprüche sperren die Freigabe. Automatische Übernahme ist ein nachvollziehbarer Vorschlag, keine medizinische Bestätigung.</p>
      {issues.map((issue, i) => <article key={`${issue.field}-${i}`} data-issue-field={issue.field}>
        <h4>{fieldLabel(issue.field)}</h4><p><b>Grund:</b> {issueReasons[issue.reason] ?? 'Gesonderte Prüfung erforderlich'}</p>
        <p>{issue.explanation}</p><p><b>Benötigt:</b> {issue.requiredInformation}</p>
        <div className="row">
          {issue.field === '$schema' ? <button className="button secondary" onClick={showTechnical}>Originalbefunde prüfen</button> :
            issue.field === 'evidenceIds' ? <button className="button secondary" onClick={() => navigate('analysis')}>Fallbelege prüfen / verknüpfen</button> :
            issue.field === '$reportHistory' ? <button className="button secondary" onClick={() => navigate('history')}>Vorberichte und Empfang prüfen</button> :
            editable && issue.field !== '$target' && <button disabled={busy} className="button secondary" onClick={() => edit(issue.field)}>
              {issue.reason === 'HumanDecision' ? 'Menschlich festlegen und begründen' : 'Angabe belegen / Widerspruch klären'}</button>}
          {canEdit && issue.reason === 'MissingSource' && <button disabled={busy} className="button secondary" onClick={() => navigate('information')}>Informationsnachforderung öffnen</button>}
        </div>
        {issue.sources.length > 0 && <details><summary>Gebundene Quellen des Befunds</summary>{issue.sources.map((s, j) => <p className="mono" key={j}>{s}</p>)}</details>}
      </article>)}
      {legacyGaps.length > 0 && <><p>Historischer Prüfstand: die Ursache wurde noch nicht strukturiert getrennt ermittelt. Ein erneuter Vervollständigungsversuch erzeugt einen eigenen aktuellen Prüfstand, ohne die Historie zu ändern.</p>
        <ul>{legacyGaps.map((e, i) => <li key={i}>{friendlyLegacyError(e)}</li>)}</ul>
        {editable && <button disabled={busy} className="button secondary" onClick={() => edit()}>Vorhandene Angaben belegen</button>}</>}
    </section>}
    {!!mir?.errors.length && <details ref={technical}><summary>Technische Originalbefunde der Pflicht- und XSD-Prüfung</summary>{mir.errors.map((e, i) => <p key={i}>{e}</p>)}</details>}
    {groupedFields.length > 0 && <div className="prepared-fields" aria-label="Vorausgefüllte Felder und Herkunft">{groupedFields.map(group =>
      <section key={group.key} data-field-group={group.key} aria-label={group.title}><h3>{group.title}</h3>{group.fields.map(field => <article key={field.field} data-field={field.field}>
      <div className="row-between"><h4>{facts.find(f => f.name === field.field)?.label ?? (Object.hasOwn(systemNames, field.field) ? systemNames[field.field] : field.field)}</h4>
        <Badge tone={field.origin === 'AgentDraft' || field.origin === 'Missing' ? 'amber' : 'neutral'}>{Object.hasOwn(originNames, field.origin) ? originNames[field.origin] : field.origin}</Badge></div>
      {field.field === 'similarIncidents' && mir?.facts.similarIncidents ? <SimilarReview value={mir.facts.similarIncidents} /> :
        <p className="prepared-value">{field.value === null ? 'Nicht belegt / offen' : field.field === 'submitterIsManufacturerContact' ? field.value === 'true' ? 'Ja, ausdrücklich dokumentiert' : 'Nein' : field.value}</p>}
      <p className="muted">{field.rationale}</p>
      <details className="field-metadata"><summary>Technische Herkunft und Metadaten</summary><p className="mono">{field.source}</p></details>
      <div className="citation-row">{field.claimIds.map(id => <button key={id} className="citation" disabled={report.assessmentVersion !== c.currentAssessment?.version} onClick={() => showClaim(id)}><Link2 size={11} />{id} · Originalbeleg</button>)}</div>
      {field.citations.length > 0 && <details><summary>Wortgetreue Fundstellen dieses Vorschlags</summary>{field.citations.map((citation, i) => <div key={i}><small className="mono">{citation.evidenceId} · {citation.hash} · {citation.locator}</small><blockquote>{citation.quote}</blockquote></div>)}</details>}
    </article>)}</section>)}</div>}
    {mir && <details><summary>Prüfstand, Herkunft und Grenzen</summary><p className="mono">{mir.schemaFile}<br />Schema {mir.schemaHash}<br />XML {mir.xmlHash}<br />Mapping {mir.mappingVersion}</p><p>{mir.facts.rationale}</p>{mir.limitations.map(s => <p key={s}>{s}</p>)}</details>}
    {mir?.followUpSchedule && <div className="callout"><b>Bei bestätigtem simulierten Empfang verbindlich nachverfolgen</b><p>{date(mir.followUpSchedule.dueAt, true, mir.followUpSchedule.timeZone)} · {mir.followUpSchedule.timeZone}<br />Interne SafetyDue: {date(mir.followUpSchedule.safetyDue, true, mir.followUpSchedule.timeZone)}</p><small>{mir.followUpSchedule.calendarVersion} · {mir.followUpSchedule.ruleVersion}. Keine neue Initialfrist.</small></div>}
    {!!report.followUpObligationIds?.length && <p className="mono">Dieser Bericht erledigt ausschließlich: {report.followUpObligationIds.join(', ')}</p>}
    <details><summary>Verfügbare eingefrorene Fallbelege für die Eingabe</summary>{c.evidence.map(e => <p key={`${e.id}-${e.version}`}><b>{e.title} · v{e.version}</b><br /><code>{e.id}</code><br /><small>{hash(e.hash)} · {e.locator}</small></p>)}</details>
    <div className="row">
      {editable && <button disabled={busy} className="button secondary" onClick={() => edit()}><PencilLine size={16} />MIR-Pflichtdaten bearbeiten</button>}
      {report.jurisdiction !== 'GB' && <button disabled={!mir?.valid} className="button secondary" onClick={() => void download(`/cases/${c.id}/mir/xml`, `${c.reference}-${report.kind}-SIMULIERT.xml`)}><FileText size={16} />Geprüftes XML herunterladen</button>}
    </div>
  </section>
}
