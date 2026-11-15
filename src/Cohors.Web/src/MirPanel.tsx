import { useState } from 'react'
import { FileText, PencilLine } from 'lucide-react'
import { downloadApi } from './api'
import type { MirFacts, Report, SafetyCase } from './types'
import { Badge, date, ErrorBanner, hash, type Dialog, type Field } from './ui'

type FactKey = Exclude<keyof MirFacts, 'evidenceIds' | 'rationale'>
const facts: { name: FactKey; label: string; type?: Field['type']; final?: boolean; options?: string[] }[] = [
  { name: 'eventDateFrom', label: 'Belegtes Ereignisdatum von', type: 'date' },
  { name: 'eventDateTo', label: 'Belegtes Ereignisdatum bis', type: 'date' },
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
  { name: 'investigationTypeCode', label: 'Untersuchungstyp-Code (nur synthetischer Testcode)', final: true },
  { name: 'investigationFindingCode', label: 'Befund-Code (nur synthetischer Testcode)', final: true },
  { name: 'investigationConclusionCode', label: 'Schlussfolgerungs-Code (nur synthetischer Testcode)', final: true },
  { name: 'componentCode', label: 'Komponenten-Code (nur synthetischer Testcode)', final: true },
  { name: 'correctiveAction', label: 'Begründete Maßnahmen und Nachverfolgung', final: true, type: 'textarea' },
  { name: 'finalComments', label: 'Abschließende Kommentare / ausdrücklich verbleibende Grenzen', final: true, type: 'textarea' },
]

export default function MirPanel({ c, report, canEdit, busy, openDialog, mutate }: {
  c: SafetyCase; report: Report; canEdit: boolean; busy: boolean;
  openDialog: (dialog: Dialog) => void; mutate: (path: string, body: unknown) => Promise<void>
}) {
  const [error, setError] = useState('')
  if (report.jurisdiction === 'GB') return <p className="muted">Eigenes GB-Demoprofil; kein EU-MIR-Export und keine Behauptung eines produktiven MHRA-Formulars.</p>
  const mir = report.mir
  function edit() {
    const final = report.kind === 'Final'
    openDialog({
      title: 'Belegte MIR-Pflichtdaten dokumentieren',
      description: 'Nur synthetische Angaben. Leere Fakten werden nicht erfunden. Jeder Wert und seine Begründung werden im Paket gebunden; Änderungen heben vorhandene Freigaben auf. Ein Folgetermin ist ein fest zugesagter Kalendertag ohne gesetzliche Wochenendverlängerung. Die Eingabe ist noch keine Freigabe.',
      fields: [
        ...facts.filter(f => f.final === undefined || f.final === final).map(f => ({
          ...f, value: mir?.facts[f.name] ?? '',
          options: f.options ? [{ value: '', label: 'Belegten Wert auswählen' }, ...f.options.map(value => ({ value, label: value }))] : undefined,
        })),
        { name: 'evidenceIds', label: 'Konkrete Evidenz-IDs, eine pro Zeile (unten verfügbare Fundstellen prüfen)', type: 'textarea',
          value: mir?.facts.evidenceIds.join('\n') ?? '' },
        { name: 'rationale', label: 'Medizinisch-technische Begründung der Angaben und Testkodierungen', type: 'textarea', value: mir?.facts.rationale, minLength: 40 },
        { name: 'reason', label: 'Änderungsbegründung / prüfbare Herkunft', type: 'textarea', minLength: 30 },
      ],
      submitLabel: 'Versioniert speichern und gegen Original-XSD prüfen',
      onSubmit: values => {
        const body: MirFacts = { evidenceIds: values.evidenceIds.split(/\r?\n/).map(x => x.trim()).filter(Boolean), rationale: values.rationale }
        for (const f of facts) body[f.name] = values[f.name] || null
        return mutate(`/cases/${c.id}/mir`, { expectedVersion: c.version, facts: body, reason: values.reason })
      },
    })
  }
  async function download(path: string, filename: string) {
    setError('')
    try { await downloadApi(path, filename) } catch (e) { setError(e instanceof Error ? e.message : 'Export nicht bestätigt.') }
  }
  return <section className="qa-panel">
    <div className="row-between"><h3>MIR · Original-XSD 7.3.1</h3><Badge tone={mir?.valid ? 'blue' : 'amber'}>{mir?.valid ? 'Struktur geprüft' : 'Pflichtdaten / Prüfung offen'}</Badge></div>
    <p className="muted">XSD-Konformität ist keine fachliche Kodierungsprüfung und keine Behördenannahme. Original-PDF und Schutz bleiben unverändert.</p>
    {error && <ErrorBanner message={error} />}
    {mir?.errors.map((e, i) => <ErrorBanner key={i} message={e} />)}
    {mir && <details><summary>Prüfstand, Herkunft und Grenzen</summary><p className="mono">{mir.schemaFile}<br />Schema {mir.schemaHash}<br />XML {mir.xmlHash}<br />Mapping {mir.mappingVersion}</p><p>{mir.facts.rationale}</p>{mir.limitations.map(s => <p key={s}>{s}</p>)}</details>}
    {mir?.followUpSchedule && <div className="callout"><b>Bei bestätigtem simulierten Empfang verbindlich nachverfolgen</b><p>{date(mir.followUpSchedule.dueAt, true, mir.followUpSchedule.timeZone)} · {mir.followUpSchedule.timeZone}<br />Interne SafetyDue: {date(mir.followUpSchedule.safetyDue, true, mir.followUpSchedule.timeZone)}</p><small>{mir.followUpSchedule.calendarVersion} · {mir.followUpSchedule.ruleVersion}. Keine neue Initialfrist.</small></div>}
    {!!report.followUpObligationIds?.length && <p className="mono">Dieser Bericht erledigt ausschließlich: {report.followUpObligationIds.join(', ')}</p>}
    <details><summary>Verfügbare eingefrorene Fallbelege für die Eingabe</summary>{c.evidence.map(e => <p key={`${e.id}-${e.version}`}><b>{e.title} · v{e.version}</b><br /><code>{e.id}</code><br /><small>{hash(e.hash)} · {e.locator}</small></p>)}</details>
    <div className="row">
      {canEdit && ['Draft', 'MedicalApproved', 'Frozen'].includes(report.state) && <button disabled={busy} className="button secondary" onClick={edit}><PencilLine size={16} />MIR-Pflichtdaten bearbeiten</button>}
      <button disabled={!mir?.valid} className="button secondary" onClick={() => void download(`/cases/${c.id}/mir/xml`, `${c.reference}-${report.kind}-SIMULIERT.xml`)}><FileText size={16} />Geprüftes XML herunterladen</button>
    </div>
  </section>
}
