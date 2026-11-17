import { useEffect, useId, useState } from 'react'
import { api } from './api'
import type { Psur, VersionReference } from './types'
import { Badge, date, ErrorBanner, label, Loading, SectionHead } from './ui'

interface Month { month: string; from: string; through: string; cases: VersionReference[]; postCutoffCases: VersionReference[]; openAtCutoff: number; unknownStateAtCutoff: number }
interface MonthlyCase {
  source: VersionReference; occurredAt: string | null; firstAwarenessAt: string; systemReceivedAt: string
  selection: string; knowledge: string; statusAtCutoff: string; stageAtFreeze: string | null; inCarryOverNarrative: boolean
}
interface MonthlyView {
  reportId: string; reportVersion: number; snapshotHash: string; productId: string; frozenMembers: string[]
  groupSource: VersionReference | null; start: string; end: string; region: string; dateBasis: string; timeZone: string
  frozenAt: string; knowledgeCutoffExclusiveUtc: string | null; status: string; limitations: string[]
  months: Month[] | null; cases: MonthlyCase[]
}
const selections: Record<string, string> = { Period: 'Basiszähler', PostCutoff: 'Nach Kenntnisstichtag bekannt',
  UnknownEventDate: 'Ereignisdatum unbekannt – kein Monatszähler', BeforePeriod: 'Vorperiode / kein erneuter Monatszähler',
  AfterPeriod: 'Nach Berichtszeitraum – ausgeschlossen' }
const state = (value: string) => value === 'NotKnownAtCutoff' ? 'Am Stichtag nicht bekannt' :
  value === 'NotRecordedAtCutoff' ? 'Historischer Zustand nicht belegt' : label(value)

export default function PsurMonthlyPanel({ report, productId }: { report: Psur; productId: string }) {
  const [view, setView] = useState<MonthlyView | null>(null)
  const [error, setError] = useState('')
  const { id, version, snapshotHash } = report
  useEffect(() => {
    let active = true
    setView(null); setError('')
    api<MonthlyView>(`/psurs/${encodeURIComponent(id)}/months?version=${version}&productId=${encodeURIComponent(productId)}`).then(value => {
      if (!active) return
      if (value.reportId !== id || value.reportVersion !== version || value.snapshotHash !== snapshotHash || value.productId !== productId)
        throw new Error('Monatsansicht gehört nicht zur ausgewählten eingefrorenen Fassung. Kein Ersatz aus Live-Daten.')
      setView(value)
    }).catch(e => { if (active) setError(e instanceof Error ? e.message : 'Monatsgrundlage konnte nicht gelesen werden.') })
    return () => { active = false }
  }, [id, version, snapshotHash, productId])
  return <section className="panel" aria-label="PSUR-Monatsauswertung">
    <SectionHead eyebrow="EINGEFRORENER EREIGNISZEITVERLAUF · KEINE INZIDENZ" title="Fallzahlen je Monat" />
    {error && <ErrorBanner message={error} />}
    {!view && !error && <Loading />}
    {view && <>
      <p>{view.productId} · {view.region} · {date(view.start)} bis einschließlich {date(view.end)}</p>
      <p><b>Fachliches Datum:</b> {view.dateBasis} · <b>Zeitzone:</b> {view.timeZone}. Keine Auswertung nach Fall-Erstellungszeit.</p>
      <p>Kenntnisgrenze (exklusiv): {date(view.knowledgeCutoffExclusiveUtc, true, 'UTC')} UTC.
        Freeze: {date(view.frozenAt, true, 'UTC')} UTC. Berichtsperiode, Kenntnis und Freeze sind verschiedene Grenzen.</p>
      <p className="mono document-hash">Snapshot SHA256: {view.snapshotHash}</p>
      <p>Eingefrorene Mitglieder: {view.frozenMembers.join(', ')}. {view.groupSource ? `Gruppendefinition ${view.groupSource.id}@${view.groupSource.version}.` : 'Einzelfamilie.'} Gezeigt wird nur {view.productId}; aktuelle Gruppierungen ändern diese Zahlen nicht.</p>
      {view.status !== 'Available' || !view.months ? <ErrorBanner message="Fehlende reproduzierbare Monatsgrundlage. Keine Zahlen oder Kurve; fehlend bedeutet nicht null Fälle." /> : <>
        <MonthChart months={view.months} />
        <div className="table-scroll"><table className="case-table"><caption>Einmalige Fälle nach Ereignismonat im gebundenen Snapshot</caption>
          <thead><tr><th>Monat / Teilzeitraum</th><th>Basisfälle</th><th>Späte Kenntnis (separat)</th><th>Basisfälle am Stichtag offen / Status unbekannt</th><th>Quellrevisionen</th></tr></thead>
          <tbody>{view.months.map(m => <tr key={m.month}><td>{m.month.slice(0, 7)}<small className="subline">{date(m.from)} – {date(m.through)}</small></td>
            <td>{m.cases.length}</td><td>{m.postCutoffCases.length}</td><td>{m.openAtCutoff} / {m.unknownStateAtCutoff}</td>
            <td><details><summary>Gebundene Basis- und Nachtragsfälle</summary><References title="Basis" values={m.cases} /><References title="Nachtrag" values={m.postCutoffCases} /></details></td></tr>)}</tbody>
          <tfoot><tr><th>Summe innerhalb der Periode</th><td>{view.months.reduce((n, m) => n + m.cases.length, 0)}</td>
            <td>{view.months.reduce((n, m) => n + m.postCutoffCases.length, 0)}</td><td colSpan={2}>Keine Addition zu einer Inzidenz oder Vermischung mit Live-Daten.</td></tr></tfoot>
        </table></div>
        <details><summary>Unbekannte Daten, Überhänge, offene Fälle und alle Ein-/Ausschlüsse ({view.cases.length} eindeutige Fälle)</summary>
          <div className="table-scroll"><table className="case-table"><thead><tr><th>Quellfall / Zuordnung</th><th>Ereignis / Kenntnis / Eingang (UTC)</th><th>Am Stichtag / beim Freeze</th></tr></thead>
            <tbody>{view.cases.map(c => <tr key={c.source.id}><td><b>{c.source.id}@{c.source.version}</b><p>{selections[c.selection] ?? c.selection}</p>
              <Badge tone={c.knowledge === 'PostCutoff' ? 'amber' : 'neutral'}>{c.knowledge === 'PostCutoff' ? 'Späte Kenntnis' : 'Vor Kenntnisgrenze bekannt'}</Badge>
              <small className="subline mono document-hash">{c.source.hash}</small></td><td>{date(c.occurredAt, true, 'UTC')}<br />{date(c.firstAwarenessAt, true, 'UTC')}<br />{date(c.systemReceivedAt, true, 'UTC')}</td>
              <td>{state(c.statusAtCutoff)}<br />{c.stageAtFreeze ? label(c.stageAtFreeze) : 'Kein Freeze-Status in der Narrativliste gebunden'}
                {c.inCarryOverNarrative && <p>Als Carry-over im N-Narrativ gebunden; nicht zusätzlich als Monatsereignis gezählt.</p>}</td></tr>)}</tbody>
          </table></div>
        </details>
      </>}
      <details open={view.status !== 'Available'}><summary>Zählregeln und Grenzen</summary>{view.limitations.map(text => <p key={text}>{text}</p>)}</details>
    </>}
  </section>
}

function References({ title, values }: { title: string; values: VersionReference[] }) {
  return <div><b>{title}</b>{values.length ? values.map(v => <p key={v.id}>{v.id}@{v.version}<br /><small className="mono document-hash">{v.hash}</small></p>) : <p>Keine Fälle in diesem gebundenen Monatszähler.</p>}</div>
}

function MonthChart({ months }: { months: Month[] }) {
  const titleId = useId()
  const descId = useId()
  const width = Math.max(720, months.length * 65)
  const max = Math.max(1, ...months.flatMap(m => [m.cases.length, m.postCutoffCases.length]))
  const x = (index: number) => 48 + index * (width - 96) / Math.max(1, months.length - 1)
  const y = (count: number) => 175 - count * 130 / max
  return <figure className="psur-month-figure">
    <div className="chart-scroll"><svg viewBox={`0 0 ${width} 225`} style={{ minWidth: width }} role="img" aria-labelledby={`${titleId} ${descId}`}>
      <title id={titleId}>PSUR-Fallzahlen je UTC-Ereignismonat</title>
      <desc id={descId}>Durchgezogen: vor dem Kenntnisstichtag bekannte Basisfälle. Gestrichelt: erst nach dem Stichtag bekannte Fälle, nicht addiert. Exakte Werte und Quellbelege stehen in der folgenden Tabelle.</desc>
      <line className="chart-axis" x1={48} x2={width - 40} y1={175} y2={175} />
      <text className="chart-label" x={16} y={179}>0</text><text className="chart-label" x={16} y={49}>{max}</text>
      <polyline className="psur-month-line" points={months.map((m, i) => `${x(i)},${y(m.cases.length)}`).join(' ')} />
      <polyline className="psur-month-line psur-month-late" points={months.map((m, i) => `${x(i)},${y(m.postCutoffCases.length)}`).join(' ')} />
      {months.map((m, i) => <g key={m.month}><circle className="intake-dot" cx={x(i)} cy={y(m.cases.length)} r={4} />
        <text className="chart-value" x={x(i)} y={y(m.cases.length) - 10} textAnchor="middle">{m.cases.length}</text>
        <text className="chart-label" x={x(i)} y={202} textAnchor="middle">{m.month.slice(0, 7)}</text></g>)}
    </svg></div>
    <figcaption>Durchgezogen: Basisfälle. Gestrichelt: separate Fälle mit Kenntnis nach Cutoff. Ereignisdatum in UTC, nicht Systemeingang; Randmonate nur anteilig innerhalb der Berichtsperiode. Null gilt nur für den gebundenen Datenstand, nicht als Nachweis fehlender Risiken.</figcaption>
  </figure>
}
