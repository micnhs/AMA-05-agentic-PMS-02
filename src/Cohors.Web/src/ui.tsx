import { useEffect, useRef, useState, type ReactNode } from 'react'
import { AlertTriangle, ArrowUpRight, Check, ChevronRight, FileText, HeartPulse, LoaderCircle, X } from 'lucide-react'
import type { CaseOutcome, CaseOutcomeSource, CaseSummary, Deadline, VersionReference } from './types'

export const roleNames: Record<string, string> = { PmsSpecialist: 'PMS Specialist', MedicalSafetyReviewer: 'Medical Safety Reviewer', RegulatoryAffairs: 'Regulatory Affairs', QualityManager: 'Quality Manager', Auditor: 'Auditor' }
const names: Record<string, string> = { Intake: 'Intake', Assessing: 'Analyse läuft', AwaitingInformation: 'Befund ausstehend', AwaitingMedicalReview: 'Medical Review', Investigating: 'Untersuchung', Monitoring: 'Beobachtung', Closed: 'Abgeschlossen', Draft: 'Entwurf', MedicalApproved: 'RA-Review', Frozen: 'Freigegeben & eingefroren', TransmissionUnknown: 'Übermittlung unklar', SimulatedAccepted: 'Simuliert angenommen', SimulatedReported: 'Simuliert erledigt', NonReportableReviewed: 'Nichtmeldepflicht geprüft', Rejected: 'Abgelehnt', Superseded: 'Überholt', Undetermined: 'Offen', Reportable: 'Meldepflicht bestätigt', NonReportable: 'Nicht meldepflichtig', Completed: 'Abgeschlossen', Queued: 'Eingeplant', Running: 'In Bearbeitung', Failed: 'Fehlgeschlagen', Abandoned: 'Kontrolliert abgebrochen', SimulatedSent: 'Simuliert angefragt', SimulatedAnswered: 'Simuliert beantwortet', Open: 'Offen', Verified: 'Wirksamkeit bestätigt', Approved: 'Freigegeben' }
export const label = (value: string) => value === 'ReturnedForCorrection' ? 'Zur Korrektur' : names[value] ?? value
export const date = (value?: string | null, time = false, timeZone?: string) => value ? new Intl.DateTimeFormat('de-DE', {
  day: '2-digit', month: 'short', year: 'numeric', timeZone: /^\d{4}-\d{2}-\d{2}$/.test(value) ? 'UTC' : timeZone,
  ...(time ? { hour: '2-digit', minute: '2-digit' } : {}),
}).format(new Date(value)) : 'Nicht bekannt'
type DueDisplay = Omit<Deadline, 'legalDue'> & { dueAt: string; kind: 'statutory' | 'follow-up'; title: string }
export const deadlineItems = (c: CaseSummary): DueDisplay[] => [
  ...c.deadlines.map(d => ({ ...d, dueAt: d.legalDue, kind: 'statutory' as const, title: 'LegalDue' })),
  ...(c.followUps ?? []).map(f => ({ id: f.id, jurisdiction: f.jurisdiction, triggerAt: f.createdAt, status: f.status,
    ...f.schedule, kind: 'follow-up' as const, title: 'Zugesagter Folgetermin', immediateAction: false,
    calculation: [`Menschlich freigegebener fester Kalendertag ${f.schedule.reportDate}; keine 2/10/15-Tage- oder Wochenendverlängerung.`,
      `Vorbericht v${f.sourceReportVersion} · Paket ${f.sourceReportHash}`, `Simulierter Empfang ${f.sourceReceiptReference}`,
      ...(f.completedByReportHash ? [`Erledigt ausschließlich durch Paket ${f.completedByReportHash}`] : [])] })),
]
export const primaryDeadline = (c: CaseSummary) => deadlineItems(c).filter(d => d.status === 'Open')
  .sort((a, b) => new Date(a.dueAt).getTime() - new Date(b.dueAt).getTime())[0] ?? deadlineItems(c).at(-1)
export const hash = (value?: string) => value ? `${value.slice(0, 12)}…${value.slice(-6)}` : 'Noch nicht gebunden'
export const isLate = (c: CaseSummary) => deadlineItems(c).some(d => d.status === 'Open' && new Date(d.dueAt) < new Date())
export const isUrgent = (c: CaseSummary) => deadlineItems(c).some(d => d.status === 'Open' && (d.immediateAction || new Date(d.safetyDue) < new Date(Date.now() + 3 * 86400000)))
export function Badge({ children, tone = 'neutral' }: { children: ReactNode; tone?: string }) { return <span className={`badge ${tone}`}><span className="status-dot" />{children}</span> }
const evidenceKinds: Record<string, string> = {
  Complaint: 'Beschwerde', Specification: 'Technische Spezifikation', BenchTest: 'Technischer Prüfbericht',
  RiskFile: 'Risikodatei', CER: 'Klinische Bewertung', PMCF: 'PMCF-Dokument', SSCP: 'Kurzbericht zu Sicherheit und klinischer Leistung',
  ClinicalFollowUp: 'Klinischer Befundrücklauf', PublicMetadata: 'Öffentliche Quellenmetadaten',
  Governance: 'Zuständigkeiten / Governance', PMSPlan: 'PMS-Plan', IFU: 'Gebrauchsanweisung / Produktinformation',
  RiskQuantification: 'Risikoquantifizierung', PMCFPlan: 'PMCF-Plan', PMCFReport: 'PMCF-Ergebnisbericht',
  SSCPChecklist: 'SSCP-Prüfliste', TDIndex: 'Technischer Dokumentationsindex', SOP: 'Verfahrensanweisung',
  Certificate: 'Bescheinigung / Auflagen', Terminology: 'Terminologieprofil', Coding: 'Menschliche Kodierung',
  HistoricalCodingRecord: 'Historischer Kodierungsbeleg', Classification: 'Medizinische Reklassifizierung',
}
export const evidenceKindLabel = (kind: string) => evidenceKinds[kind] ?? `Dokumentenkategorie: ${kind}`
export function EvidenceCategory({ kind }: { kind: string }) {
  return <span className="evidence-category" aria-label={`Dokumentenkategorie: ${evidenceKindLabel(kind)}`}><FileText size={13} aria-hidden="true" />{evidenceKindLabel(kind)}</span>
}
export function Criticality({ seriousness, outcome, compact = false }: { seriousness: string; outcome?: CaseOutcome; compact?: boolean }) {
  const urgency: Record<string, string> = { PublicHealthThreat: 'Gefahr für die öffentliche Gesundheit',
    DeathOrSeriousDeterioration: 'Tod oder schwere Verschlechterung', OtherSerious: 'Sonstiges schweres Vorkommnis' }
  const statuses: Record<string, string> = { Documented: 'menschlich dokumentiert', MedicalReviewed: 'medizinisch freigegeben',
    Historical: 'historische Angabe, nicht aktuell bestätigt', Stale: 'Quellenbezug veraltet / nicht bestätigt',
    Unreviewed: 'Angabe nicht medizinisch bestätigt', Conflict: 'widersprüchliche Angaben – prüfen' }
  const consequence = (source: CaseOutcomeSource) => source.code === 'Death' ? 'Todesfall' : source.code === 'SeriousDeterioration'
    ? 'Schwere gesundheitliche Verschlechterung' : source.code === 'CodedOther' ? source.term : 'Patientenausgang unklar / nicht bestätigt'
  const sources = outcome ? outcome.sources.length ? outcome.sources : [outcome] : []
  return <div className={`criticality ${compact ? 'compact' : ''}`} aria-label="Kritikalität und Patientenausgang">
    <span className={`criticality-urgency ${seriousness === 'OtherSerious' ? 'amber' : 'danger'}`}><AlertTriangle size={14} aria-hidden="true" />
      <span>Fristenklasse: <b>{urgency[seriousness] ?? seriousness}</b></span></span>
    {!outcome && <span className="criticality-outcome"><HeartPulse size={14} aria-hidden="true" />Patientenausgang wird geprüft …</span>}
    {sources.map((source, index) => <span key={`${source.source}-${source.term}-${index}`}
      className={`criticality-outcome ${source.code === 'Death' && ['Documented', 'MedicalReviewed'].includes(source.status) ? 'danger' : ''}`}
      data-outcome-status={source.status}><HeartPulse size={14} aria-hidden="true" /><span><b>{consequence(source)}</b>
        {source.code !== 'Unknown' && ` · ${statuses[source.status] ?? 'Prüfstand unklar'}`}
        {!compact && <small>{source.source}</small>}</span></span>)}
    {!compact && sources.length > 1 && <small>Strukturierte Quellen mit getrenntem Prüfstand. Andere F-Kodes schließen einen Todesfall nicht aus; mehrere Folgen sind nicht automatisch widersprüchlich.</small>}
  </div>
}
export function Empty({ title, children }: { title: string; children?: ReactNode }) { return <div className="empty"><div className="empty-symbol"><Check size={25} /></div><h3>{title}</h3><p>{children}</p></div> }
export function ErrorBanner({ message, dismiss }: { message: string; dismiss?: () => void }) { return <div className="error-banner" role="alert"><AlertTriangle size={19} /><span>{message}</span>{dismiss && <button className="icon-button" onClick={dismiss} aria-label="Fehler schließen"><X size={18} /></button>}</div> }
export function Loading() { return <div className="loading" role="status"><LoaderCircle className="spin" size={22} />Wird geladen…</div> }
export function SectionHead({ eyebrow, title, children }: { eyebrow?: string; title: string; children?: ReactNode }) { return <div className="section-head"><div>{eyebrow && <span className="eyebrow">{eyebrow}</span>}<h2>{title}</h2></div>{children}</div> }
export function CaseTable({ cases, open }: { cases: CaseSummary[]; open: (id: string) => void }) {
  if (!cases.length) return <Empty title="Keine Fälle in dieser Ansicht">Die Liste enthält ausschließlich tatsächlich gespeicherte Fälle.</Empty>
  return <div className="table-scroll"><table className="case-table"><thead><tr><th>Fall / Produkt</th><th>Kritikalität / Ausgang</th><th>Bearbeitung</th><th>Jurisdiktion</th><th>Frist</th><th>Priorität</th><th><span className="sr-only">Öffnen</span></th></tr></thead><tbody>{cases.map(c => { const deadline = primaryDeadline(c); return <tr key={c.id}>
    <td><button className="case-title" onClick={() => open(c.id)}>{c.title}</button><span className="subline">{c.reference} <span>·</span> {c.productId === 'ortho' ? 'OrthoLink' : 'CardioFlow'} <span>·</span> v{c.version}</span></td>
    <td><Criticality seriousness={c.seriousness} outcome={c.outcome} compact /></td>
    <td><Badge tone={c.stage === 'AwaitingMedicalReview' ? 'amber' : 'neutral'}>{label(c.reportState ?? c.stage)}</Badge></td><td>{c.jurisdiction}</td>
    <td><span className={isLate(c) ? 'danger-text' : ''}>{date(deadline?.dueAt, false, deadline?.timeZone)}</span><small className="subline">{deadline?.title} · {deadline?.timeZone}</small></td>
    <td>{isLate(c) ? <Badge tone="danger">Überfällig</Badge> : isUrgent(c) ? <Badge tone="amber">Priorisiert</Badge> : <span className="muted">Regulär</span>}</td>
    <td><button className="icon-button" onClick={() => open(c.id)} aria-label={`${c.reference} öffnen`}><ChevronRight size={19} /></button></td>
  </tr> })}</tbody></table></div>
}
export type Field = { name: string; label: string; value?: string; type?: 'textarea' | 'date' | 'datetime-local' | 'select' | 'text'; options?: { value: string; label: string }[]; required?: boolean; minLength?: number; multiple?: boolean; values?: string[] }
export function PsurReferences({ values }: { values?: VersionReference[] | null }) {
  if (!values) return <p>Kein hinreichendes Quellenmanifest vorhanden.</p>
  if (!values.length) return <p>Keine ausgewiesenen Quellversionen in dieser Liste; Datenstatus und Geltungsbereich gesondert beachten.</p>
  return <div>{values.map((value, i) => <p key={`${value.id}-${value.version}-${i}`}><b>{value.id}@{value.version}</b><br /><small className="mono">{value.hash}</small></p>)}</div>
}
export type Dialog = { title: string; description?: string; body?: ReactNode; fields: Field[]; submitLabel?: string; onSubmit: (values: Record<string, string>) => Promise<void> }
export function ActionDialog({ dialog, close }: { dialog: Dialog; close: () => void }) {
  const ref = useRef<HTMLDialogElement>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  useEffect(() => { ref.current?.showModal() }, [])
  return <dialog className="dialog" ref={ref} onCancel={e => { if (busy) e.preventDefault(); else close() }}>
    <header><div><span className="eyebrow">KONTROLLIERTER SCHRITT</span><h2>{dialog.title}</h2></div><button type="button" className="icon-button" disabled={busy} onClick={close} aria-label="Dialog schließen"><X /></button></header>
    <p>{dialog.description ?? 'Die Änderung wird serverseitig geprüft und mit Identität, Version und Begründung protokolliert.'}</p>
    {dialog.body}
    {error && <ErrorBanner message={error} />}
    <form onSubmit={async e => {
      e.preventDefault(); const form = new FormData(e.currentTarget); setBusy(true); setError('')
      try {
        const values: Record<string, string> = {}
        for (const [name, value] of form) { if (typeof value !== 'string') throw new Error('Dieses Formular akzeptiert keine Dateifelder.'); values[name] = value }
        for (const field of dialog.fields.filter(f => f.multiple)) values[field.name] = JSON.stringify(form.getAll(field.name))
        await dialog.onSubmit(values); close()
      } catch (error) { setError(error instanceof Error ? error.message : 'Operation nicht bestätigt.') } finally { setBusy(false) }
    }}>
      {dialog.fields.map(f => <label key={f.name} className="field">{f.label}{f.type === 'textarea' ? <textarea name={f.name} defaultValue={f.value} required={f.required !== false} minLength={f.minLength} rows={4} /> : f.type === 'select' ? <select name={f.name} multiple={f.multiple} defaultValue={f.multiple ? f.values ?? [] : f.value} required={f.required !== false}>{f.options?.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}</select> : <input name={f.name} type={f.type ?? 'text'} defaultValue={f.value} required={f.required !== false} minLength={f.minLength} />}</label>)}
      <footer><button type="button" className="button secondary" disabled={busy} onClick={close}>Abbrechen</button><button className="button primary" disabled={busy}>{busy ? <LoaderCircle className="spin" size={17} /> : <ArrowUpRight size={17} />}{dialog.submitLabel ?? 'Begründet bestätigen'}</button></footer>
    </form>
  </dialog>
}
