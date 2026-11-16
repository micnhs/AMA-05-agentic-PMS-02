import { useState } from 'react'
import { FileText, Link2, PencilLine } from 'lucide-react'
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
  HumanEntry: 'Menschlich ergänzt · noch keine Paketfreigabe', Missing: 'Fehlend / offen' }
const systemNames: Record<string, string> = { $reference: 'Hersteller-Fallreferenz', $description: 'Originaler Ereignistext',
  $firstAwareness: 'Erste Organisationskenntnis', $reportabilityAwareness: 'Kenntnis gemäß gebundener Meldepflicht',
  $reportDate: 'Entwurfsdatum', $authority: 'Fachlich zuständige Behörde', $simulator: 'Technischer Pilotempfänger',
  $device: 'Produkt / Modell', $basicUdi: 'Basic-UDI', $batch: 'Dokumentierte Charge',
  submitterIsManufacturerContact: 'Melder ist der belegte Herstellerkontakt', similarIncidents: 'Ähnliche Ereignisse / Perioden / Nenner' }

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

export default function MirPanel({ c, report, canEdit, busy, openDialog, mutate, showClaim }: {
  c: SafetyCase; report: Report; canEdit: boolean; busy: boolean;
  openDialog: (dialog: Dialog) => void; mutate: (path: string, body: unknown) => Promise<void>; showClaim: (id: string) => void
}) {
  const [error, setError] = useState('')
  const mir = report.mir
  const preparation = report.preparation
  const visibleFacts = facts.filter(f => (f.final === undefined || f.final === (report.kind === 'Final')) && (!f.afterInitial || report.kind !== 'Initial'))
  const visibleFields = preparation?.fields.filter(f => report.jurisdiction === 'GB'
    ? f.field.startsWith('$') || ['preliminaryAnalysis', 'furtherInvestigation'].includes(f.field)
    : f.field.startsWith('$') ||
    visibleFacts.some(fact => fact.name === f.field) || f.field === 'submitterIsManufacturerContact' ||
    f.field === 'similarIncidents' && report.kind === 'Final') ?? []
  function edit() {
    const final = report.kind === 'Final'
    openDialog({
      title: 'Fehlende Angaben / Korrekturen belegen',
      description: 'Vorhandene Werte sind vorausgefüllt. Nur belegte synthetische Angaben ergänzen; nichts zur Beseitigung eines Fehlers erfinden. Leere Angaben dürfen als offene Lücken gespeichert werden, sperren aber nötigenfalls die Freigabe. Ein bewusst eingegebener Folgetermin ist eine menschliche Zusage, kein Agententermin. Änderungen brauchen neue Medical- und RA-Freigaben.',
      fields: [
        ...visibleFacts.map(f => ({
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
        { name: 'reason', label: 'Änderungsbegründung / prüfbare Herkunft', type: 'textarea', minLength: 30 },
      ],
      submitLabel: 'Versioniert speichern und gegen Original-XSD prüfen',
      onSubmit: values => {
        const body: MirFacts = { evidenceIds: values.evidenceIds.split(/\r?\n/).map(x => x.trim()).filter(Boolean), rationale: values.rationale }
        for (const f of visibleFacts) body[f.name] = values[f.name] || null
        body.submitterIsManufacturerContact = values.submitterIsManufacturerContact ? values.submitterIsManufacturerContact === 'true' : null
        if (values.similarIncidents?.trim()) {
          const parsed: unknown = JSON.parse(values.similarIncidents)
          if (!isSimilarIncidents(parsed)) throw new Error('Ähnlichkeitsreview: vollständiges strukturiertes Objekt mit expliziten Perioden, Zahlen oder null und Datenlückenbegründung erforderlich.')
          body.similarIncidents = parsed
        }
        return mutate(`/cases/${c.id}/mir`, { expectedVersion: c.version, facts: body, reason: values.reason })
      },
    })
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
      <Badge tone={mir?.valid ? 'blue' : 'amber'}>{report.jurisdiction === 'GB' ? 'Nur GB-SIM-Narrativ, keine GB-MIR-Freigabe' : mir?.valid ? 'Implementierte Pilot-Pflichtprüfungen bestanden' : 'Freigaberelevante Lücken offen'}</Badge>
    </div>
    <p className="muted">XSD-Konformität, fachlicher Human Review und nationale Versandbereitschaft sind getrennt. Medical Safety prüft weiterhin zuerst; RA prüft das konkrete Paket oder gibt es zur Korrektur an PMS zurück. Original-PDF und Schutz bleiben unverändert.</p>
    {error && <ErrorBanner message={error} />}
    {(mir?.checks?.contentErrors ?? mir?.errors ?? []).length > 0 && <div className="callout" data-testid="report-gaps"><b>Vor Freigabe zu klären</b><ul>{(mir?.checks?.contentErrors ?? mir?.errors ?? []).map((e, i) => <li key={i}>{e}</li>)}</ul></div>}
    {!!mir?.errors.some(e => e.startsWith('XSD ')) && <details><summary>Zusätzliche Original-XSD-Befunde</summary>{mir.errors.filter(e => e.startsWith('XSD ')).map((e, i) => <p key={i}>{e}</p>)}</details>}
    {visibleFields.length > 0 && <div className="prepared-fields" aria-label="Vorausgefüllte Felder und Herkunft">{visibleFields.map(field => <article key={field.field} data-field={field.field}>
      <div className="row-between"><h4>{facts.find(f => f.name === field.field)?.label ?? systemNames[field.field] ?? field.field}</h4>
        <Badge tone={field.origin === 'AgentDraft' || field.origin === 'Missing' ? 'amber' : 'neutral'}>{originNames[field.origin] ?? field.origin}</Badge></div>
      {field.field === 'similarIncidents' && mir?.facts.similarIncidents ? <SimilarReview value={mir.facts.similarIncidents} /> :
        <p className="prepared-value">{field.value === null ? 'Nicht belegt / offen' : field.field === 'submitterIsManufacturerContact' ? field.value === 'true' ? 'Ja, ausdrücklich dokumentiert' : 'Nein' : field.value}</p>}
      <p className="muted">{field.rationale}</p><small className="mono">{field.source}</small>
      <div className="citation-row">{field.claimIds.map(id => <button key={id} className="citation" disabled={report.assessmentVersion !== c.currentAssessment?.version} onClick={() => showClaim(id)}><Link2 size={11} />{id} · Originalbeleg</button>)}</div>
      {field.citations.length > 0 && <details><summary>Wortgetreue Fundstellen dieses Vorschlags</summary>{field.citations.map((citation, i) => <div key={i}><small>{citation.evidenceId} · {hash(citation.hash)} · {citation.locator}</small><blockquote>{citation.quote}</blockquote></div>)}</details>}
    </article>)}</div>}
    {mir && <details><summary>Prüfstand, Herkunft und Grenzen</summary><p className="mono">{mir.schemaFile}<br />Schema {mir.schemaHash}<br />XML {mir.xmlHash}<br />Mapping {mir.mappingVersion}</p><p>{mir.facts.rationale}</p>{mir.limitations.map(s => <p key={s}>{s}</p>)}</details>}
    {mir?.followUpSchedule && <div className="callout"><b>Bei bestätigtem simulierten Empfang verbindlich nachverfolgen</b><p>{date(mir.followUpSchedule.dueAt, true, mir.followUpSchedule.timeZone)} · {mir.followUpSchedule.timeZone}<br />Interne SafetyDue: {date(mir.followUpSchedule.safetyDue, true, mir.followUpSchedule.timeZone)}</p><small>{mir.followUpSchedule.calendarVersion} · {mir.followUpSchedule.ruleVersion}. Keine neue Initialfrist.</small></div>}
    {!!report.followUpObligationIds?.length && <p className="mono">Dieser Bericht erledigt ausschließlich: {report.followUpObligationIds.join(', ')}</p>}
    <details><summary>Verfügbare eingefrorene Fallbelege für die Eingabe</summary>{c.evidence.map(e => <p key={`${e.id}-${e.version}`}><b>{e.title} · v{e.version}</b><br /><code>{e.id}</code><br /><small>{hash(e.hash)} · {e.locator}</small></p>)}</details>
    <div className="row">
      {canEdit && report.jurisdiction !== 'GB' && ['Draft', 'MedicalApproved', 'Frozen'].includes(report.state) && <button disabled={busy} className="button secondary" onClick={edit}><PencilLine size={16} />MIR-Pflichtdaten bearbeiten</button>}
      {report.jurisdiction !== 'GB' && <button disabled={!mir?.valid} className="button secondary" onClick={() => void download(`/cases/${c.id}/mir/xml`, `${c.reference}-${report.kind}-SIMULIERT.xml`)}><FileText size={16} />Geprüftes XML herunterladen</button>}
    </div>
  </section>
}
