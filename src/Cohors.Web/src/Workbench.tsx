import { useEffect, useRef, useState } from 'react'
import { ArrowLeft, CheckCheck, ChevronRight, CircleAlert, ClipboardCheck, FileCheck2, FileText, Fingerprint, FlaskConical, Link2, LockKeyhole, PencilLine, Send, ShieldCheck, Sparkles, Stethoscope } from 'lucide-react'
import { api, downloadApi } from './api'
import type { Actor, Evidence, SafetyCase, SubmissionPreview } from './types'
import { Badge, date, Empty, ErrorBanner, EvidenceCategory, hash, label, primaryDeadline, roleNames, type Dialog, type Field } from './ui'
import MirPanel from './MirPanel'
import InformationPanel from './InformationPanel'
import DocumentDialog, { type DocumentRequest } from './DocumentDialog'
import CaseProcessBar from './CaseProcessBar'
import SubmissionPreviewPanel from './SubmissionPreviewPanel'

function ReviewReturnHistory({ reports, current, historicalOnly = false }: {
  reports: SafetyCase['reports']; current: SafetyCase['currentReport']; historicalOnly?: boolean
}) {
  const entries = reports.flatMap(report => report.reviewReturn &&
    (!historicalOnly || report.version !== current?.version || report.scopeHash !== current.scopeHash)
    ? [{ report, returned: report.reviewReturn }] : []).sort((a, b) => b.report.version - a.report.version)
  if (!entries.length) return null
  const title = historicalOnly ? 'Frühere Review-Rückgaben' : 'Dokumentierte Review-Rückgaben'
  return <section className="review-return-history" data-testid="review-return-history" aria-label={title}>
    <h3>{title}</h3>
    <p className="muted">Eine Folgerevision allein belegt keine Erledigung der Rückgabe. Historische Signaturen sind keine aktuelle Freigabe.</p>
    {entries.map(({ report, returned }) => {
      const sources = reports.filter(source => source.version < report.version && source.scopeHash === returned.previousScopeHash)
      const source = sources.length === 1 ? sources[0] : null
      return <article className="history-entry" key={`${report.version}-${report.scopeHash}`} data-return-version={report.version}>
        <div className="row-between"><h4>Rückgabe dokumentiert in Bericht v{report.version}</h4>
          <Badge tone={report.version === current?.version && report.scopeHash === current.scopeHash ? 'amber' : 'neutral'}>
            {report.version === current?.version && report.scopeHash === current.scopeHash ? 'Aktuelle Rückgabe' : 'Historische Rückgabe'}</Badge></div>
        <p><b>{Object.hasOwn(roleNames, returned.role) ? roleNames[returned.role] : returned.role}</b> · <time dateTime={returned.at}>{date(returned.at, true)}</time></p>
        <p className="review-return-reason">{returned.reason}</p>
        <p>{source ? `Zurückgegebenes Paket: Bericht v${source.version} · ${source.kind} · ${source.jurisdiction}` :
          'Version des zurückgegebenen Pakets in dieser Fallansicht nicht eindeutig belegt; der gespeicherte Paket-Hash bleibt zugänglich.'}</p>
        <details className="review-return-metadata"><summary>Technische Rückgabebindung</summary>
          <p>Aufgezeichnete Identität: <span className="mono">{returned.actorId}</span><br />Rolle: {returned.role}<br />
            Originalzeitpunkt: <span className="mono">{returned.at}</span></p>
          <p className="mono">Zurückgegebenes Paket: {returned.previousScopeHash}<br />
            Rückgabefassung v{report.version}: {report.scopeHash}<br />Gebundener Assessmentstand: v{report.assessmentVersion}</p>
        </details>
      </article>
    })}
  </section>
}

export default function Workbench({ value: c, actor, openDialog, mutate, busy, back }: {
  value: SafetyCase; actor: Actor; openDialog: (d: Dialog) => void; mutate: (path: string, body: unknown) => Promise<void>; busy: boolean; back: () => void
}) {
  const [tab, setTab] = useState('analysis')
  const [document, setDocument] = useState<DocumentRequest | null>(null)
  const [sourceId, setSourceId] = useState('')
  const [error, setError] = useState('')
  const [claimId, setClaimId] = useState('')
  const [previewLoading, setPreviewLoading] = useState(false)
  const previewRequest = useRef(0)
  const workbench = useRef<HTMLDivElement>(null)
  useEffect(() => { setDocument(null); setSourceId(''); setClaimId('') }, [c.id, c.version])
  useEffect(() => { setPreviewLoading(false); return () => { previewRequest.current++ } }, [c.id, c.version])
  const assessment = c.currentAssessment
  const report = c.currentReport
  const reopenedAssessmentStale = assessment && c.evidence.some(e => e.kind === 'Reopening' && !assessment.evidenceIds.includes(e.id))
  const currentApprovalScope = report && assessment && report.state !== 'Superseded' && !reopenedAssessmentStale &&
    report.assessmentVersion === assessment.version && report.evidenceHash === assessment.evidenceHash ? report.scopeHash : null
  const deadline = primaryDeadline(c)
  const medical = actor.roles.includes('MedicalSafetyReviewer')
  const regulatory = actor.roles.includes('RegulatoryAffairs')
  const editor = medical || actor.roles.includes('PmsSpecialist')
  const reason: Field = { name: 'reason', label: 'Fachliche Begründung', type: 'textarea', minLength: 20 }
  const action = (title: string, path: string, fields: Field[], extra: object = {}, submitLabel?: string) => openDialog({ title,
    description: `Sie bearbeiten ${c.reference}, Version ${c.version}. Eine veraltete Version wird abgewiesen. Keine Handlung übermittelt echte Informationen.`,
    fields, submitLabel, onSubmit: values => mutate(`/cases/${c.id}/${path}`, { expectedVersion: c.version, ...extra, ...values }) })
  function evidence(e: Evidence, claim?: DocumentRequest['claim']) {
    setSourceId(e.id); setError('')
    setDocument({ evidence: e, path: `/cases/${encodeURIComponent(c.id)}/evidence/${encodeURIComponent(e.id)}?caseVersion=${c.version}`,
      context: `${c.reference}, ausgewählte Fallfassung ${c.version}`, claim })
  }
  function navigateTab(value: string) {
    setTab(value)
    workbench.current?.scrollIntoView({ block: 'start' })
  }
  function showClaim(id: string) {
    setClaimId(id)
    const citation = assessment?.content.claims.find(claim => claim.id === id)?.citations[0]
    const document = c.evidence.find(e => e.id === citation?.evidenceId && e.hash === citation.hash)
    if (document) evidence(document, assessment?.content.claims.find(claim => claim.id === id))
    else { setDocument(null); setSourceId(''); setError('Dieser Claim-Bezug ist im aktuellen Evidenzstand nicht auflösbar. Assessment neu prüfen.') }
  }
  async function linkEvidence() {
    try {
      const available = (await api<Evidence[]>('/evidence')).filter(e => e.productId === c.productId && e.approved && !c.evidence.some(current => current.id === e.id && current.version === e.version))
      if (!available.length) throw new Error('Keine weitere freigegebene Evidenzversion verfügbar. Öffentliche Metadaten zuerst medizinisch einordnen.')
      openDialog({ title: 'Konkrete Evidenzversion verknüpfen', description: 'Eine neue Verknüpfung ersetzt die aktuelle Revision dieses Dokuments, nicht seine Historie. Das Assessment wird erneut geprüft; erledigte Fristen werden nicht neu geöffnet.', fields: [
        { name: 'evidenceId', label: 'Freigegebene Produktversion', type: 'select', options: available.map(e => ({ value: e.id, label: `${e.title} · v${e.version}` })) }, reason
      ], onSubmit: values => mutate(`/cases/${c.id}/evidence`, { ...values, expectedVersion: c.version, evidenceVersion: available.find(e => e.id === values.evidenceId)?.version }) })
    } catch (error) { setError(error instanceof Error ? error.message : 'Evidenzliste nicht geladen.') }
  }
  async function previewSubmission() {
    const request = ++previewRequest.current
    setPreviewLoading(true); setError('')
    try {
      const preview = await api<SubmissionPreview>(`/cases/${encodeURIComponent(c.id)}/submission-preview?version=${c.version}`)
      if (request !== previewRequest.current) return
      openDialog({ title: 'Ausschließlich simuliert übermitteln',
        description: `Abschließende Prüfung von ${preview.caseReference}, Fallversion ${preview.caseVersion}. Die Ansicht liest ausschließlich das bereits getrennt freigegebene, eingefrorene Paket und löst noch keinen Versand aus.`,
        body: <SubmissionPreviewPanel value={preview} openEvidence={evidence} />,
        fields: [{ name: 'scenario', label: 'Simulatorantwort', type: 'select', options: [
          { value: 'Unknown', label: 'Unklar nach möglichem Empfang → Abgleich erforderlich' },
          { value: 'Accept', label: 'Simulierte fachliche Annahme' }, { value: 'Reject', label: 'Simulierte Ablehnung' }
        ] }, reason], submitLabel: 'An Simulator übermitteln',
        onSubmit: values => mutate(`/cases/${preview.caseId}/submit`, { ...values, expectedVersion: preview.caseVersion }) })
    } catch (error) {
      if (request === previewRequest.current) setError(error instanceof Error ? error.message : 'Versandübersicht nicht bestätigt. Kein Versand ausgelöst.')
    } finally { if (request === previewRequest.current) setPreviewLoading(false) }
  }
  const prepare = () => openDialog({ title: 'Konkretes Meldungspaket entwerfen', fields: [
    { name: 'jurisdiction', label: 'Dokumentierte Zieljurisdiktion · neuer Entwurf ersetzt den bisherigen Freigabestand',
      type: 'select', value: report?.jurisdiction ?? c.jurisdiction, options: [...new Set(c.deadlines.map(d => d.jurisdiction))].map(value => ({ value, label: value })) },
    { name: 'kind', label: 'Meldeart', type: 'select', options: [{ value: 'Initial', label: 'Erstmeldung' }, { value: 'FollowUp', label: 'Folgemeldung (nach bestätigtem simulierten Empfang)' }, { value: 'Final', label: 'Abschlussmeldung' }] },
    { name: 'language', label: 'Explizite Berichtssprache', type: 'select', options: [{ value: 'de', label: 'Deutsch' }, { value: 'en', label: 'Englisch' }] },
    { name: 'preliminary', label: 'Untersuchungsstand', type: 'select', options: [{ value: 'true', label: 'Vorläufig: Datenlücken ausdrücklich offenlegen' }, { value: 'false', label: 'Vollständig untersucht' }] },
    { name: 'openQuestions', label: 'Verbleibende offene Fragen', type: 'textarea', value: assessment?.content.uncertainties.join('\n') ?? 'Ursachenuntersuchung steht aus.' }, reason,
  ], description: 'Der vorhandene Autorenagent befüllt Fall-/Produktangaben und begründete Meldungstexte. Unbelegte Pflichtdaten bleiben sichtbar offen. Kein automatisches Freigeben oder Übermitteln; keine parallele Mehrländerbearbeitung.',
  submitLabel: 'Versionierten Entwurf erstellen', onSubmit: values => mutate(`/cases/${c.id}/report`, { ...values, preliminary: values.preliminary === 'true', expectedVersion: c.version }) })
  return <div className="workbench" ref={workbench}>
    <CaseProcessBar c={c} actor={actor} navigate={navigateTab} prepare={prepare} />
    {reopenedAssessmentStale && <div className="callout" role="status"><b>Frühere Bewertung durch Wiederaufnahme überholt.</b><p>Die neue Wiederaufnahmebegründung ist noch nicht bewertet. Medical Safety kann ohne KI einen aktuellen menschlichen Entwurf erstellen. Historische Signaturen sind keine aktuelle Freigabe.</p></div>}
    {document && <DocumentDialog request={document} close={() => setDocument(null)} />}
    <button className="text-button back-link" onClick={back}><ArrowLeft size={16} />Zurück zu den Fallakten</button>
    <div className="workbench-heading"><div><div className="row"><span className="eyebrow">{c.reference}</span><Badge tone="amber">SYNTHETISCH</Badge><Badge>{label(c.stage)}</Badge></div><h1>{c.title}</h1><p>{c.productId === 'ortho' ? 'OrthoLink Modular Hip / OL-M3' : 'CardioFlow Valve / CF-V2'} <span>·</span> {c.jurisdiction} <span>·</span> Fallversion {c.version}</p></div><div className="row">{editor && <button disabled={busy || c.agentRuns.some(r => r.status === 'Queued' || r.status === 'Running')} className="button primary" onClick={() => action('Begründete Analyse beauftragen', 'assessment', [{ name: 'mode', label: 'Expliziter Ausführungsmodus', type: 'select', options: [{ value: 'Replay', label: 'Replay · reproduzierbare Demo, kein Live-Modell' }, { value: 'Live', label: 'Live · konfiguriertes Foundry-Modell, kein Fallback' }, { value: 'Failure', label: 'Fehlerdemo · sichtbarer KI-Ausfall' }] }, { ...reason, value: 'Medizinisch-technische Bewertung mit Quellen, Gegenhypothesen und verbleibender Unsicherheit anfordern.' }], {}, 'Analyse starten')}><Sparkles size={17} />Analyse beauftragen</button>}</div></div>
    <div className="case-context"><span><small>ERSTES WISSEN · {deadline?.timeZone}</small><b>{date(c.firstAwarenessAt, true, deadline?.timeZone)}</b></span><span><small>SYSTEMEINGANG · {deadline?.timeZone}</small><b>{date(c.systemReceivedAt, true, deadline?.timeZone)}</b></span><span><small>{deadline?.title.toUpperCase()} · {deadline?.timeZone}</small><b>{date(deadline?.dueAt, true, deadline?.timeZone)}</b></span><span><small>PRODUKTIDENTITÄT</small><b>{c.udi ?? 'UDI unbekannt'}</b></span></div>
    <div className="tabs" role="tablist" aria-label="Fallansichten">{[{ id: 'analysis', text: 'Analyse & Evidenz' }, { id: 'report', text: 'Human Review & Meldung' }, { id: 'information', text: 'Informationsbedarf' }, { id: 'history', text: 'Chronologie & Läufe' }].map(t => <button role="tab" aria-selected={tab === t.id} className={tab === t.id ? 'selected' : ''} onClick={() => navigateTab(t.id)} key={t.id}>{t.text}{t.id === 'information' && c.informationRequests.length > 0 && <span>{c.informationRequests.length}</span>}</button>)}</div>
    {tab === 'analysis' && <div className="analysis-grid"><aside className="case-timeline panel"><span className="eyebrow">FALLVERLAUF</span><div className="timeline-item"><span><FileText size={15} /></span><div><b>Originalhinweis</b><small>{date(c.firstAwarenessAt, true)}</small><p>{c.sources.length} Quelle(n), ein Vorfall</p></div></div><div className="timeline-item"><span><Sparkles size={15} /></span><div><b>Assessment</b><small>{assessment ? `v${assessment.version} · ${assessment.mode}` : 'Ausstehend'}</small><p>{assessment ? 'Begründeter Entwurf' : 'Analyse oder menschliche Erstbewertung'}</p></div></div><div className="timeline-item"><span><ClipboardCheck size={15} /></span><div><b>Evidence Assurance</b><small>{assessment?.quality.findings.length ?? '–'} offene Befunde</small><p>QA ist keine Freigabe</p></div></div><div className="timeline-item"><span><Stethoscope size={15} /></span><div><b>Medical Safety</b><small>{c.approvals.some(a => a.scopeHash === currentApprovalScope && a.role === 'MedicalSafetyReviewer') ? 'Freigegeben' : 'Menschliche Entscheidung offen'}</small></div></div><div className="timeline-item"><span><ShieldCheck size={15} /></span><div><b>Regulatory Affairs</b><small>{c.approvals.some(a => a.scopeHash === currentApprovalScope && a.role === 'RegulatoryAffairs') ? 'Freigegeben' : 'Getrenntes Konto erforderlich'}</small></div></div><div className="timeline-foot"><LockKeyhole size={16} />Freigaben binden Inhalt und Hash.</div></aside>
      <article className="panel assessment-panel"><header className="assessment-head"><span className="eyebrow">MEDIZINISCH-TECHNISCHE BEWERTUNG</span><div className="row-between"><h2>{assessment ? `Assessment v${assessment.version}` : 'Bewertung vorbereiten'}</h2>{assessment && <Badge tone="blue">{assessment.mode === 'Replay' ? 'REPLAY-ENTWURF' : assessment.mode === 'Human' ? 'MENSCHLICHER ENTWURF' : 'LIVE-ENTWURF'}</Badge>}</div>{assessment && <p>Modell: {assessment.model} · {date(assessment.createdAt, true)}<br /><span className="mono">Evidenzsnapshot {hash(assessment.evidenceHash)}</span></p>}</header>
        {!assessment ? <Empty title="Noch keine Sicherheitsbewertung">{c.agentRuns.some(r => r.status === 'Running' || r.status === 'Queued') ? 'Der Auftrag ist persistent gespeichert und wird bearbeitet. Die Frist läuft unabhängig weiter.' : 'Eine KI-Analyse kann angefordert werden. Bei Ausfall steht Medical Safety ein unabhängiger menschlicher Entwurf offen.'}</Empty> : <>
          <div className="assessment-notice"><CircleAlert size={17} /><p>Vorschlag, keine endgültige Sicherheitsentscheidung. Kausalität, Datenlücken und Gegenhypothesen bleiben ausdrücklich sichtbar.</p></div>
          {assessment.content.sections.map(section => <section className="analysis-section" key={section.key}><h3>{section.title}</h3><p>{section.text}</p><div className="citation-row">{section.claimIds.map(id => <button key={id} className="citation" onClick={() => showClaim(id)}><Link2 size={11} />{id}</button>)}</div></section>)}
          <section className="recommendations"><h3><CheckCheck size={20} />Begründete Handlungsempfehlungen</h3>{assessment.content.recommendations.map((r, i) => <div key={r.title} className="recommendation"><span>{String(i + 1).padStart(2, '0')}</span><div><h4>{r.title}</h4><p>{r.rationale}</p><p className="muted"><b>Grenze:</b> {r.limitation}</p><div className="row"><Badge>{r.owner}</Badge><Badge tone="amber">{r.urgency}</Badge>{r.claimIds.map(id => <button key={id} className="citation" onClick={() => showClaim(id)}><Link2 size={11} />{id}</button>)}</div></div></div>)}</section>
          <div className="callout"><b>Wahrscheinlichkeit: {assessment.content.probability === null ? 'nicht quantifizierbar, kein Nullrisiko' : assessment.content.probability}</b><p>{assessment.content.probabilityBasis}</p></div>
        </>}
        {medical && <div className="assessment-actions"><button className="button secondary" onClick={() => {
          if (!assessment) {
            action('Menschlichen Mindestbericht vorbereiten', 'manual', [{ name: 'facts', label: 'Gesicherte Fakten (Originalhinweis prüfen)', type: 'textarea', value: c.description, minLength: 30 }, { name: 'analysis', label: 'Eigene medizinisch-technische Erstbewertung', type: 'textarea', minLength: 40 }, { name: 'conclusion', label: 'Begründete vorläufige Empfehlung und Grenzen', type: 'textarea', minLength: 40 }, reason])
          } else {
            openDialog({ title: 'Assessment begründet überarbeiten', fields: [{ name: 'conclusion', label: 'Überarbeitete medizinisch-technische Schlussfolgerung', type: 'textarea', value: assessment.content.sections.find(s => s.key === 'conclusion')?.text, minLength: 40 }, reason], onSubmit: values => mutate(`/cases/${c.id}/assessment/revise`, { expectedVersion: c.version, reason: values.reason, content: { ...assessment.content, sections: assessment.content.sections.map(s => s.key === 'conclusion' ? { ...s, text: values.conclusion } : s) } }) })
          }
        }}><PencilLine size={16} />{assessment ? 'Menschlich überarbeiten' : 'Ohne KI: eigener Mindestbericht'}</button></div>}
      </article>
      <aside className="evidence-column"><section className="panel"><div className="row-between"><h3>Quellen & Fundstellen</h3><Badge>{c.evidence.length}</Badge></div><p className="muted">Originale prüfen. Nicht nur Referenzen lesen.</p>{error && <ErrorBanner message={error} />}{editor && <button className="button secondary" onClick={() => void linkEvidence()}><Link2 size={15} />Evidenzversion verknüpfen</button>}
        {assessment?.content.claims.filter(claim => claim.id === claimId).map(claim => <div className="callout" key={claim.id}><Badge>{claim.id} · {claim.kind}</Badge><p>{claim.text}</p>{claim.citations.map(citation => <div key={`${citation.evidenceId}-${citation.hash}`}><b>{citation.locator}</b><blockquote>{citation.quote}</blockquote><small className="mono">{citation.evidenceId} · {hash(citation.hash)}</small></div>)}</div>)}
        {c.evidence.map(e => <button key={e.id} className={`evidence-item ${sourceId === e.id ? 'selected' : ''}`} onClick={() => evidence(e)}><FileCheck2 size={17} /><span><b>{e.title}</b><EvidenceCategory kind={e.kind} /><small>v{e.version} · {e.synthetic ? 'Synthetisch' : 'Öffentlich'} · Dokument öffnen</small></span><ChevronRight size={15} /></button>)}
        </section>
        <section className="panel qa-panel"><h3><FlaskConical size={18} />Evidence Assurance</h3>{assessment ? <><p className="muted">{assessment.quality.summary}</p>{assessment.quality.findings.map((f, i) => <div className="qa-finding" key={`${f.code}-${i}`}><Badge tone={f.severity === 'Critical' ? 'danger' : 'amber'}>{f.severity}</Badge><b>{f.description}</b><p>{f.remedy}</p></div>)}{assessment.quality.findings.length === 0 && <p>Keine strukturellen Befunde. Human Review bleibt verpflichtend.</p>}</> : <p className="muted">Wird getrennt vom Assessment ausgeführt.</p>}</section>
      </aside></div>}
    {tab === 'report' && <div className="review-grid"><section className="panel"><div className="row-between"><h2>Das konkrete Freigabepaket</h2>{report && <Badge tone={report.reviewReturn ? 'amber' : 'blue'}>{report.reviewReturn ? 'Zur Korrektur' : label(report.state)}</Badge>}</div><p className="muted">Assessment, Evidenz, Bericht, Sprache, Formular, Jurisdiktion und Empfänger sind gebunden. Neue Inhalte brauchen neue Freigaben.</p>
      {report ? <><div className="report-metadata"><span><small>BERICHT</small><b>v{report.version} · {report.kind}</b></span><span><small>PILOTEMPFÄNGER</small><b>{report.recipient}</b></span><span><small>SPRACHE / FORMULAR</small><b>{report.language} · {report.formVersion}</b></span><span><small>PAKET-HASH</small><b className="mono">{hash(report.scopeHash)}</b></span></div>
        {report.reviewReturn && <div className="callout" role="status" data-testid="current-review-return"><b>Zur Korrektur zurückgegeben · {roleNames[report.reviewReturn.role]}</b><p className="review-return-reason">{report.reviewReturn.reason}</p><small>{date(report.reviewReturn.at, true)} · bisherige Freigaben gelten nicht für die Korrektur.</small></div>}
        <ReviewReturnHistory reports={c.reports} current={report} historicalOnly />
        <MirPanel c={c} report={report} canEdit={editor} busy={busy} openDialog={openDialog} mutate={mutate} showClaim={showClaim} navigate={navigateTab} />
        <details className="report-preview"><summary>Vollständiger Autorenbericht und offene Untersuchungsfragen</summary><Badge tone="amber">NUR SIMULIERTER VERSAND</Badge><pre>{report.narrative}</pre><h4>Offene Untersuchungsfragen</h4><p>{report.openQuestions}</p></details>
        <div className="row"><button className="button secondary" onClick={() => downloadApi(`/cases/${c.id}/manifest`, `${c.reference}-audit.json`).catch(e => setError(e instanceof Error ? e.message : 'Audit-Export nicht bestätigt.'))}><Fingerprint size={16} />Audit-Manifest herunterladen</button></div>{error && <ErrorBanner message={error} />}</> : <Empty title="Noch kein Berichtsentwurf">Zuerst Assessment prüfen und ein konkretes Paket vorbereiten.</Empty>}
      {(editor || regulatory) && assessment && <button className="button secondary" onClick={prepare}><FileText size={17} />{report ? 'Neue Berichtsversion vorbereiten' : 'Entwurf vorbereiten'}</button>}
    </section><aside className="panel"><span className="eyebrow">VIER-AUGEN-PRINZIP</span><h2>Zwei Rollen.<br />Zwei Identitäten.</h2>{['MedicalSafetyReviewer', 'RegulatoryAffairs'].map((role, i) => { const a = [...c.approvals].reverse().find(a => a.role === role && a.scopeHash === currentApprovalScope); return <div className="approval-step" key={role}><span>{i + 1}</span><div><b>{roleNames[role]}</b><p>{a ? `${a.actorName} · ${date(a.at, true)}` : 'Freigabe ausstehend'}</p>{a && <small>{a.rationale}</small>}</div>{a && <ShieldCheck size={19} />}</div> })}
      {report && report.jurisdiction !== 'GB' && !report.mir?.valid && <p className="callout">Freigabe gesperrt: belegbare Pflichtangaben oder Formularprüfung fehlen. PMS kann ergänzen; Medical Safety und RA können gezielt zurückgeben.</p>}
      {medical && report?.state === 'Draft' && <button disabled={busy || !!report.reviewReturn || report.jurisdiction !== 'GB' && !report.mir?.valid} className="button primary full" onClick={() => action('Medizinische Freigabe', 'approve/medical', [reason, { name: 'findingDisposition', label: 'Begründete Disposition offener Fragen (vorläufig)', type: 'textarea', value: 'Die offenen Befunde verhindern keine notwendige vorläufige Meldung. Kausalität bleibt offen; die gezielte Untersuchung wird parallel fortgesetzt.' }], {}, 'Medizinisch freigeben')}><Stethoscope size={18} />Medizinisch freigeben</button>}
      {regulatory && report?.state === 'MedicalApproved' && <button disabled={busy || report.jurisdiction !== 'GB' && !report.mir?.valid} className="button primary full" onClick={() => action('Regulatorische Freigabe & Einfrieren', 'approve/regulatory', [reason], {}, 'Regulatorisch freigeben')}><LockKeyhole size={18} />Regulatorisch freigeben</button>}
      {(medical || regulatory) && report && !report.reviewReturn && ['Draft', 'MedicalApproved', 'Frozen'].includes(report.state) && <button disabled={busy} className="button secondary full" onClick={() => action('Meldung zur Korrektur zurückgeben', 'report/return',
        [{ ...reason, label: 'Konkrete Lücken, Korrekturen und benötigte Belege für PMS', minLength: 40 }], {}, 'Zur Korrektur zurückgeben')}><ArrowLeft size={16} />Zur Korrektur zurückgeben</button>}
      {regulatory && report?.state === 'Frozen' && <button disabled={busy || previewLoading} className="button primary full" onClick={() => void previewSubmission()}><Send size={17} />{previewLoading ? 'Versandübersicht laden…' : 'Simuliert übermitteln'}</button>}
      {regulatory && c.submissions.some(s => s.status === 'TransmissionUnknown') && <button className="button primary full" onClick={() => action('Unklaren Empfang abgleichen', 'reconcile', [reason], {}, 'Korrelations-ID abgleichen')}><CheckCheck size={17} />Empfang abgleichen</button>}
      <div className="guardrail-note"><ShieldCheck size={20} /><p>Dieses Konto: <b>{roleNames[actor.roles[0]]}</b>. Für die zweite Freigabe abmelden und ein anderes berechtigtes Konto verwenden.</p></div>
      {c.submissions.map(s => <div className="receipt" key={s.id}><Badge tone={s.status === 'TransmissionUnknown' ? 'amber' : 'blue'}>{label(s.status)}</Badge><b>{s.receiptReference ?? 'Noch kein bestätigter Empfang'}</b><span className="mono">{s.correlationId}</span><small>SIMULIERT · {s.attempts} Sendeversuch · {date(s.attemptedAt, true)}</small></div>)}
    </aside></div>}
    {tab === 'information' && <InformationPanel value={c} actor={actor} openDialog={openDialog} mutate={mutate} busy={busy} />}
    {tab === 'history' && <div className="review-grid"><section className="panel"><h2>Kenntnis & Versionsgeschichte</h2>{c.awareness.map(a => <article className="history-entry" key={a.id}><b>{a.kind} · {date(a.at, true)}</b><p>{a.reason}</p><small>Systemaufzeichnung {date(a.recordedAt, true)} · Quelle {a.source}</small></article>)}<ReviewReturnHistory reports={c.reports} current={report} />{c.approvals.map(a => <article className="history-entry" key={a.id}><b>{a.actorName} · {roleNames[a.role]}</b><p>{a.rationale}</p><small>{date(a.at, true)} · Hash {hash(a.scopeHash)}</small></article>)}</section><section className="panel"><h2>Agentenläufe</h2><p className="muted">Sechs logische Rollen, keine sechs unnötigen Microservices. Fehlgeschlagene Live-Läufe werden nicht durch Replay ersetzt.</p>{c.agentRuns.map(r => <article className="history-entry" key={r.id}><div className="row-between"><b>{r.role}</b><Badge tone={r.status === 'Failed' ? 'danger' : 'blue'}>{label(r.status)}</Badge></div><p>{r.mode} · {r.model}</p><small>{date(r.startedAt, true)}{r.errorCode && ` · ${r.errorCode}`}</small></article>)}{!c.agentRuns.length && <Empty title="Noch kein Lauf">Menschliche Bearbeitung ist auch ohne KI möglich.</Empty>}</section></div>}
  </div>
}
