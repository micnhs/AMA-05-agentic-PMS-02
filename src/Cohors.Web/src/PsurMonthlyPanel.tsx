import { useEffect, useState } from 'react'
import { api } from './api'
import type { Psur, PsurMonthlyView } from './types'
import { date, ErrorBanner, label, Loading, PsurReferences, SectionHead } from './ui'

const exclusions: Record<string, string> = { UnknownEventDate: 'Ereignisdatum unbekannt', CarryOver: 'Gebundene Carry-over-Fälle',
  PostCutoffAddendum: 'Spätere Kenntnis · Nachträge zu N', OutsidePeriod: 'Bekanntes Ereignis außerhalb N' }

export default function PsurMonthlyPanel({ report, productId }: { report: Psur; productId: string }) {
  const [view, setView] = useState<PsurMonthlyView | null>(null)
  const [error, setError] = useState('')
  const { id, version, snapshotHash } = report
  useEffect(() => {
    let active = true
    setView(null); setError('')
    api<PsurMonthlyView>(`/psurs/${encodeURIComponent(id)}/months?version=${version}&productId=${encodeURIComponent(productId)}`)
      .then(value => {
        if (!active) return
        if (value.contract !== 'cohors-psur-months-1' || value.reportId !== id || value.reportVersion !== version ||
          value.snapshotHash !== snapshotHash || value.productId !== productId)
          throw new Error('Monatsantwort passt nicht zur ausgewählten Berichtsfassung. Keine Ersatzberechnung aus Live-Fällen.')
        setView(value)
      }).catch(e => { if (active) setError(e instanceof Error ? e.message : 'Gebundene Monatssicht nicht geladen.') })
    return () => { active = false }
  }, [id, version, snapshotHash, productId])
  const width = Math.max(540, (view?.months.length ?? 0) * 52 + 60)
  const maximum = Math.max(1, ...view?.months.map(m => m.cases.length) ?? [])
  const x = (index: number) => !view || view.months.length < 2 ? width / 2 : 35 + index * (width - 70) / (view.months.length - 1)
  const y = (count: number) => 170 - count / maximum * 125
  return <section className="panel psur-months" aria-label="Gebundene PSUR-Monatssicht">
    <SectionHead eyebrow={`${productId} · EREIGNISMONATE · EINGEFRORENER DATENSTAND`} title="PSUR-Monatsfallzahlen" />
    {error && <ErrorBanner message={error} />}
    {!view && !error && <Loading />}
    {view && <>
      <p>Bericht {view.reportId} · SQL v{view.reportVersion} · Basic-UDI {view.basicUdi ?? 'historisch nicht gebunden'} · {view.region}.</p>
      <p>Berichtsperiode: {date(view.start)} – {date(view.end)} einschließlich. Kenntnisstichtag (exklusiv): {date(view.knowledgeCutoffExclusiveUtc, true, 'UTC')} UTC. Freeze: {date(view.frozenAt, true, 'UTC')} UTC.</p>
      <p className="mono">Snapshot SHA-256 {view.snapshotHash}<br />Mitglieds-Snapshot SHA-256 {view.memberSnapshotHash ?? 'nicht ableitbar'}</p>
      <p className="callout">{view.boundary}</p>
      {view.status !== 'Available' ? <ErrorBanner message={`Monatszahlen aus dieser historischen Quelle nicht ableitbar; keine Live-Rekonstruktion. ${view.issues.join(' ')}`} /> : <>
        <figure className="intake-figure"><div className="chart-scroll"><svg viewBox={`0 0 ${width} 225`} style={{ minWidth: width }}
          role="img" aria-label="Verlauf eindeutiger PSUR-Fälle nach Ereignismonat in UTC, keine Inzidenzrate">
          <title>Ereignisfallzahlen aus dem eingefrorenen N-Zähler</title><line className="chart-axis" x1="25" y1="170" x2={width - 25} y2="170" />
          <polyline className="intake-line" points={view.months.map((m, i) => `${x(i)},${y(m.cases.length)}`).join(' ')} />
          {view.months.map((m, i) => <g key={m.month}>
            <circle className="intake-dot" cx={x(i)} cy={y(m.cases.length)} r="4"><title>{m.from}–{m.through}: {m.cases.length} Fälle</title></circle>
            <text className="chart-value" x={x(i)} y={y(m.cases.length) - 12} textAnchor="middle">{m.cases.length}</text>
            <text className="chart-label" x={x(i)} y="195" textAnchor="middle">{m.month}</text>
          </g>)}
        </svg></div><figcaption>Eindeutige Fälle nach Ereignisdatum, nicht neu angelegte Akten. Randmonate enthalten nur den ausgewiesenen Periodenausschnitt; Nachträge sind nicht in dieser Linie enthalten.</figcaption></figure>
        <div className="table-scroll"><table className="case-table" aria-label="PSUR-Monatsfallzahlen"><thead><tr>
          <th>Kalendermonat / Ausschnitt (UTC)</th><th>Eindeutige Basisfälle</th><th>Davon am Stichtag nachweislich offen</th><th>Davon Stichtagsstatus unbekannt</th><th>Gebundene Fallversionen</th>
        </tr></thead><tbody>{view.months.map(m => <tr key={m.month}>
          <td>{m.month}<small className="subline">{date(m.from)} – {date(m.through)}</small></td><td>{m.cases.length}</td>
          <td>{m.openAtCutoff.length}</td><td>{m.unknownStatusAtCutoff.length}</td>
          <td><details><summary>{m.cases.length} Monatsquellen</summary><PsurReferences values={m.cases} /></details></td>
        </tr>)}</tbody></table></div>
        <h3>Nicht in Monatsbasiszahlen mischen</h3>
        {view.exclusions.map(e => <details key={e.kind}><summary>{exclusions[e.kind] ?? e.kind}: {e.cases.length}</summary>
          <p>{e.reason}</p><PsurReferences values={e.cases} />
        </details>)}
        <details><summary>Ereignis-, Kenntnis-, Systemeingangszeit und belegter Status aller gebundenen regionalen Quellen ({view.sources.length})</summary>
          <p>Diese Quellenliste enthält auch ausgeschlossene und frühere Ereignisse. Weder ihre Länge noch ihre heutige Version ist eine zusätzliche Monatsfallzahl.</p>
          <div className="table-scroll"><table className="case-table"><thead><tr><th>Fall / Freeze-Quellversion</th><th>Ereigniszeit UTC</th><th>Erste Kenntnis UTC</th><th>Systemeingang UTC</th><th>Belegter N-Stichtagsstatus</th></tr></thead>
            <tbody>{view.sources.map(s => <tr key={s.reference.id}><td><PsurReferences values={[s.reference]} /></td>
              <td>{date(s.occurredAt, true, 'UTC')}</td><td>{date(s.firstAwarenessAt, true, 'UTC')}</td><td>{date(s.systemReceivedAt, true, 'UTC')}</td>
              <td>{label(s.statusAtCutoff)}{s.cutoffVersion ? <PsurReferences values={[s.cutoffVersion]} /> : <p>Keine belegte historische Fallversion, nicht als offen oder geschlossen eingestuft.</p>}</td>
            </tr>)}</tbody></table></div>
        </details>
      </>}
    </>}
  </section>
}
