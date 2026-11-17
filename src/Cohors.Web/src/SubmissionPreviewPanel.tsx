import type { Evidence, SubmissionPreview } from './types'
import { Badge, date, EvidenceCategory, roleNames } from './ui'

export default function SubmissionPreviewPanel({ value: p, openEvidence }: { value: SubmissionPreview; openEvidence: (e: Evidence) => void }) {
  const r = p.report
  const target = r.preparation?.target
  return <section className="dispatch-preview" aria-label="Gebundene Versandübersicht">
    <Badge tone="amber">AUSSCHLIESSLICH SIMULIERT · KEINE ECHTE ÜBERMITTLUNG</Badge>
    <h3>{p.caseReference} · Fallversion {p.caseVersion} · Bericht v{r.version}</h3>
    <dl className="dispatch-metadata">
      <dt>Logischer Empfänger</dt><dd>{target?.authority ?? 'Nicht historisch gebunden'}</dd>
      <dt>Technischer Empfänger</dt><dd>{r.recipient}</dd>
      <dt>Jurisdiktion / Sprache</dt><dd>{r.jurisdiction} / {r.language}</dd>
      <dt>Kanal / Versandumgebung</dt><dd>{p.channel} / {p.environment} · keine Netzwerkübermittlung</dd>
      <dt>Meldeart / Untersuchungsstand</dt><dd>{r.kind} / {r.preliminary ? 'Vorläufig' : 'Nicht vorläufig'}</dd>
      <dt>Formular / Schema</dt><dd>{r.formVersion} / {r.schemaVersion}</dd>
      <dt>Gebundenes Zielprofil</dt><dd>{target?.version ?? 'Fehlt'}</dd>
      <dt>Herstellerprofil / Fallbasis des Entwurfs</dt><dd>{r.preparation?.governance ? `${r.preparation.governance.id}@${r.preparation.governance.version}` : 'Kein Profil gebunden'} / {r.preparation?.caseSource.id}@{r.preparation?.caseSource.version}</dd>
      <dt>Assessment / Evidenz</dt><dd>v{r.assessmentVersion} / <span className="mono">{r.evidenceHash}</span></dd>
      <dt>Freigegebener Paket-Scope · SHA-256</dt><dd className="mono">{r.scopeHash}</dd>
      <dt>Eingefrorene Berichtsbytes · SHA-256</dt><dd className="mono">{p.frozenJsonHash}</dd>
    </dl>
    <p>Nur die adressierten Pflichten: {r.obligationIds.join(', ')}. Folgepflichten: {r.followUpObligationIds?.join(', ') || 'Keine im Paket gebunden'}. Andere Länderpflichten bleiben getrennt.</p>
    <div className="callout"><b>Keine nationale Versandbereitschaft</b>{target?.externalRequirements.map((text, i) => <p key={i}>{text}</p>)}</div>
    <details open><summary>Tatsächlich eingefrorener Berichtstext</summary><pre>{r.narrative}</pre><h4>Offene Untersuchungsfragen</h4><pre>{r.openQuestions || 'Im Paket leer.'}</pre></details>
    {r.mir && <details><summary>Gebundene Formularangaben und unverändertes MIR-XML</summary>
      <pre>{JSON.stringify(r.mir.facts, null, 2)}</pre><p className="mono">XML SHA-256 {r.mir.xmlHash}</p><pre>{r.mir.xml}</pre>
    </details>}
    <h3>Anhänge und Originalbezüge</h3>
    <p>{r.attachmentHashes.length} gebundene Anhangshashes, einschließlich MIR-XML, soweit vorhanden. Die Links lesen die gespeicherten Evidenztexte bzw. Auszüge der ausgewählten Fallversion, nicht eine zusätzlich behauptete vollständige Original-PDF-/Word-Datei.</p>
    {p.evidence.map(e => <details key={`${e.id}-${e.version}`}><summary>{e.title} · {e.id}@{e.version}</summary>
      <EvidenceCategory kind={e.kind} /><p>{e.locator} · {e.origin}</p><p className="mono">SHA-256 {e.hash}</p>
      <button type="button" className="button secondary" onClick={() => openEvidence(e)}>Gebundenen Evidenztext öffnen</button>
    </details>)}
    <p>Originalbezüge aus der hashgebundenen Vorbereitungsfassung; diese Referenzen behaupten keine zusätzlichen binären Anhänge.</p>
    {p.originals.map(s => <div className="history-entry" key={s.id}><b>{s.title} · Original-ID {s.externalId}</b>
      <p>Quellbezug {s.id}; erste Kenntnis {date(s.awarenessAt, true)}; Systemeingang {date(s.receivedAt, true)}.</p><p className="mono">SHA-256 {s.hash}</p>
    </div>)}
    <details><summary>Freigaben genau dieses Pakets</summary>{p.approvals.map(a => <div className="history-entry" key={a.id}>
      <b>{roleNames[a.role]} · {a.actorName}</b><p>{date(a.at, true)} · {a.rationale}</p><p className="mono">{a.scopeHash}</p>
    </div>)}</details>
    <p className="callout">Abbrechen sendet nichts. Erst die folgende ausdrückliche Bestätigung löst die Simulation für diese Version aus. Geänderte Fassung, Empfänger- oder Profilbindung wird serverseitig abgewiesen. Unklarer Empfang erfordert Abgleich, keinen blinden neuen Versuch.</p>
  </section>
}
