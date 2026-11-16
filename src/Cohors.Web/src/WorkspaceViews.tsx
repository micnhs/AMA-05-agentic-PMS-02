import { useEffect, useRef, useState } from 'react'
import { ArrowUpRight, CalendarDays, ChevronLeft, ChevronRight, RefreshCw } from 'lucide-react'
import { api } from './api'
import type { CalendarEvent, CalendarSnapshot, CockpitSnapshot, Device, IntakePoint, WorkspaceCount } from './types'
import { Badge, Criticality, date, Empty, ErrorBanner, label, Loading, SectionHead } from './ui'

const zones = ['Europe/Berlin', 'Europe/Dublin', 'Europe/London', 'Europe/Paris', 'Europe/Rome', 'UTC']
const kindLabels: Record<string, string> = { LegalDue: 'Gesetzliche LegalDue', SafetyDue: 'Interne SafetyDue',
  FollowUp: 'Bestätigter Folgetermin', PsurSubmission: 'PSUR-Einreichungstag', NotifiedBodyReply: 'BS-Antworttermin' }
const reviewLabels: Record<string, string> = { Missing: 'Ohne Assessment', AiDraft: 'KI-Entwurf · medizinisch ungeprüft',
  HumanDraft: 'Menschlicher Entwurf · medizinisch ungeprüft', MedicalReviewed: 'Aktuell medizinisch geprüft',
  Stale: 'Analyse veraltet / neuer Lauf offen', UnknownMode: 'Entwurfsmodus unbekannt' }
const timingLabels: Record<string, string> = { Overdue: 'Überfällig', Upcoming: 'Bald fällig', Later: 'Später fällig',
  DueToday: 'Kalendertag heute', RecordedClosed: 'Keine offene Terminpflicht', Historical: 'Ältere Fassung / Runde' }
const sourceStatuses: Record<string, string> = { QuestionOpen: 'Rückfrage offen', ReplyDraft: 'Antwortentwurf',
  ReplyApproved: 'Antwort freigegeben', ReplySubmitted: 'Antwort simuliert quittiert', QuestionClosed: 'Frage simuliert geschlossen' }
const number = (value: number) => value.toLocaleString('de-DE')
export function civilDay(instant: string, timeZone: string) {
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(new Date(instant))
  const part = (type: string) => parts.find(p => p.type === type)!.value
  return `${part('year')}-${part('month')}-${part('day')}`
}
export function moveCivilDay(day: string, days: number) {
  const value = new Date(`${day}T00:00:00Z`)
  value.setUTCDate(value.getUTCDate() + days)
  return value.toISOString().slice(0, 10)
}
function moveMonth(month: string, months: number) {
  const value = new Date(`${month.slice(0, 7)}-01T00:00:00Z`)
  value.setUTCMonth(value.getUTCMonth() + months)
  return value.toISOString().slice(0, 10)
}
export function calendarWeeks(month: string): (string | null)[][] {
  const start = `${month.slice(0, 7)}-01`
  const offset = (new Date(`${start}T00:00:00Z`).getUTCDay() + 6) % 7
  const end = moveMonth(start, 1)
  const days: (string | null)[] = Array.from({ length: offset }, () => null)
  for (let d = start; d < end; d = moveCivilDay(d, 1)) days.push(d)
  while (days.length % 7) days.push(null)
  return Array.from({ length: days.length / 7 }, (_, i) => days.slice(i * 7, i * 7 + 7))
}
function useSnapshot<T>(path: string, revision: number) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState('')
  const [refresh, setRefresh] = useState(0)
  useEffect(() => {
    let active = true
    let timer: number | undefined
    setData(null); setError('')
    async function read() {
      try { const result = await api<T>(path); if (active) { setData(result); setError('') } }
      catch (error) { if (active) setError(error instanceof Error ? error.message : 'Lesesicht nicht geladen.') }
      finally { if (active) timer = window.setTimeout(read, 30_000) }
    }
    void read()
    return () => { active = false; window.clearTimeout(timer) }
  }, [path, revision, refresh])
  return { data, error, reload: () => setRefresh(r => r + 1) }
}
function ProductFilter({ products, value, change }: { products: Device[]; value: string; change: (value: string) => void }) {
  return <label className="field compact">Produktfamilie<select value={value} onChange={e => change(e.target.value)}>
    <option value="">Alle berechtigten Produkte</option>{products.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
  </select></label>
}
function ZoneFilter({ value, change }: { value: string; change: (value: string) => void }) {
  return <label className="field compact">Anzeigezeitzone<select value={value} onChange={e => change(e.target.value)}>
    {zones.map(z => <option key={z} value={z}>{z}</option>)}
  </select></label>
}
function Pagination({ offset, total, size, change }: { offset: number; total: number; size: number; change: (value: number) => void }) {
  return <div className="workspace-pagination"><span role="status">{total ? `${number(Math.min(offset + 1, total))}–${number(Math.min(offset + size, total))} von ${number(total)}` : '0 Einträge'}</span>
    <button className="button secondary" disabled={!offset} onClick={() => change(Math.max(0, offset - size))}>Vorherige Seite</button>
    <button className="button secondary" disabled={offset + size >= total} onClick={() => change(offset + size)}>Nächste Seite</button></div>
}
function Bars({ title, values, choose }: { title: string; values: WorkspaceCount[]; choose: (key: string) => void }) {
  const max = Math.max(1, ...values.map(v => v.count))
  return <div className="count-bars" role="group" aria-label={title}>{values.map(v => <button type="button" className="count-bar" key={v.key}
    onClick={() => choose(v.key)} aria-label={`${v.label}: ${number(v.count)} Fallakten anzeigen`}>
    <span>{v.label}</span><strong>{number(v.count)}</strong><span className="count-track" aria-hidden="true"><i style={{ width: `${100 * v.count / max}%` }} /></span>
  </button>)}</div>
}
function IntakeChart({ points, choose }: { points: IntakePoint[]; choose: (key: string) => void }) {
  const max = Math.max(1, ...points.map(p => p.count))
  const width = Math.max(540, points.length * 40 + 60)
  const x = (i: number) => points.length === 1 ? width / 2 : 35 + i * (width - 70) / (points.length - 1)
  const y = (count: number) => 170 - count / max * 125
  return <>
    <figure className="intake-figure"><div className="chart-scroll"><svg viewBox={`0 0 ${width} 225`} style={{ minWidth: width }}
      role="img" aria-label="Zeitreihe der Fallzugänge mit exakten Anzahlen; zugehörige Fallakten über die Wertetabelle öffnen">
      <title>Fallzugänge, keine Inzidenzrate</title><line className="chart-axis" x1="25" y1="170" x2={width - 25} y2="170" />
      <polyline className="intake-line" points={points.map((p, i) => `${x(i)},${y(p.count)}`).join(' ')} />
      {points.map((p, i) => <g key={p.from}><circle className="intake-dot" cx={x(i)} cy={y(p.count)} r="4"><title>{p.from}–{p.through}: {p.count}</title></circle>
        <text className="chart-value" x={x(i)} y={y(p.count) - 12} textAnchor="middle">{number(p.count)}</text>
        <text className="chart-label" x={x(i)} y="195" textAnchor="middle">{p.from.slice(5)}</text>
      </g>)}</svg></div><figcaption>Linie: Anzahl neu angelegter Fallakten je ausgewiesenem Tag bzw. Monatsabschnitt. Null bedeutet kein Fallzugang, nicht kein Ereignis.</figcaption></figure>
    <details><summary>Zeitreihe als Tabelle und Fallzugänge öffnen</summary><div className="workspace-table"><table className="case-table">
      <thead><tr><th>Zeitraum einschließlich</th><th>Fallzugänge</th><th>Fallakten</th></tr></thead><tbody>{points.map(p => <tr key={p.from}>
        <td>{date(p.from)}{p.through !== p.from && ` – ${date(p.through)}`}</td><td>{number(p.count)}</td>
        <td><button className="text-button" onClick={() => choose(p.from)}>{p.from}: {number(p.count)} Fälle öffnen</button></td>
      </tr>)}</tbody></table></div></details>
  </>
}

export function CockpitWorkspace({ products, revision, open }: { products: Device[]; revision: number; open: (id: string) => void }) {
  const [filters, setFilters] = useState(() => {
    const today = civilDay(new Date().toISOString(), 'Europe/Berlin')
    return { productId: '', from: moveCivilDay(today, -29), through: today, timeZone: 'Europe/Berlin', focus: 'all', offset: 0 }
  })
  const [range, setRange] = useState({ from: filters.from, through: filters.through })
  const [filterError, setFilterError] = useState('')
  const list = useRef<HTMLElement>(null)
  const query = new URLSearchParams({ from: filters.from, through: filters.through, timeZone: filters.timeZone,
    focus: filters.focus, offset: String(filters.offset), ...(filters.productId ? { productId: filters.productId } : {}) })
  const { data, error, reload } = useSnapshot<CockpitSnapshot>(`/cockpit?${query}`, revision)
  const focus = (value: string) => { setFilters(f => ({ ...f, focus: value, offset: 0 })); list.current?.scrollIntoView({ block: 'start' }) }
  return <div className="stack">
    <section className="panel"><form className="workspace-filters" onSubmit={e => {
      e.preventDefault()
      const days = (Date.parse(`${range.through}T00:00:00Z`) - Date.parse(`${range.from}T00:00:00Z`)) / 86_400_000
      if (!Number.isFinite(days) || days < 0 || days > 365) { setFilterError('Einen bis 366 Kalendertage auswählen.'); return }
      setFilterError(''); setFilters(f => ({ ...f, ...range, focus: 'all', offset: 0 }))
    }}>
      <ProductFilter products={products} value={filters.productId} change={productId => setFilters(f => ({ ...f, productId, focus: 'all', offset: 0 }))} />
      <label className="field compact">Fallzugänge ab<input type="date" required value={range.from} onChange={e => setRange(r => ({ ...r, from: e.target.value }))} /></label>
      <label className="field compact">Bis einschließlich<input type="date" required value={range.through} onChange={e => setRange(r => ({ ...r, through: e.target.value }))} /></label>
      <ZoneFilter value={filters.timeZone} change={timeZone => setFilters(f => ({ ...f, timeZone, focus: 'all', offset: 0 }))} />
      <button type="submit" className="button secondary">Zeitraum anwenden</button>
      <button type="button" className="icon-button" aria-label="Cockpit aktualisieren" onClick={reload}><RefreshCw size={18} /></button>
    </form><p className="muted">Alle Fallkennzahlen beziehen sich auf die ausgewählten Fallzugänge und deren heutigen Bearbeitungsstand, nicht auf einen historischen Stichtagsstatus. Maximal 366 Tage.</p>
      {(filterError || error) && <ErrorBanner message={filterError || error} />}
      {data && <p className="workspace-asof">{date(data.from)}–{date(data.through)} · Anzeigezeitzone {data.timeZone} · {number(data.totalCases)} Fallakten · SQL-Lesestand {date(data.asOf, true, data.timeZone)}{error ? ' · nicht aktuell bestätigt' : ''}.</p>}
    </section>
    {!data && !error && <Loading />}
    {data && <>
      <div className="metrics cockpit-metrics">
        {[
          ['open', 'Offene Fallakten', data.kpis.openCases, 'Nicht menschlich geschlossen.'],
          ['medical', 'Medical-Prüfung jetzt', data.kpis.pendingMedical, 'Aktueller Prozessschritt Medical Safety.'],
          ['regulatory', 'RA-Prüfung jetzt', data.kpis.pendingRegulatory, 'Aktueller Prozessschritt Regulatory Affairs.'],
          ['obligations', 'Offene Fallpflichten', data.kpis.openObligations, 'Einzelmeldung und bestätigtes Follow-up, jeweils einmal.'],
          ['upcoming', 'Bald fällige Fallpflichten', data.kpis.upcomingObligations, 'SafetyDue erreicht oder binnen 72 h; Haupttermin nicht überfällig.'],
          ['overdue', 'Überfällige Fallpflichten', data.kpis.overdueObligations, 'LegalDue oder bestätigter Haupttermin überschritten.'],
        ].map(([key, title, value, note]) => <button type="button" className={`metric metric-link ${key === 'overdue' ? 'accent' : ''}`} key={key}
          data-testid={`kpi-${key}`} onClick={() => focus(String(key))}><span>{title}</span><strong>{number(Number(value))}</strong><small>{note}</small></button>)}
      </div>
      <section className="panel"><SectionHead eyebrow="ZEITREIHE · KEINE RATE" title="Fallzugänge" /><p className="muted">{data.dateBasis}</p>
        <IntakeChart points={data.intake} choose={key => focus(`intake:${key}`)} /></section>
      <div className="workspace-charts"><section className="panel"><SectionHead title="Fallakten je Prozessschritt" />
        <p className="muted">Ein aktueller Schritt pro Fall aus derselben deterministischen Logik wie die Fallleiste. Nachverfolgung umfasst auch geschlossene Akten; offene Akten separat filtern.</p>
        <Bars title="Prozesszahlen" values={data.processSteps} choose={key => focus(`process:${key}`)} />
      </section><section className="panel"><SectionHead title="Analyse- und medizinischer Prüfstand" />
        <p className="muted">Ein menschlicher Entwurf ist noch keine getrennte medizinische Paketprüfung. Veraltete Quellen und offene neue Analyseläufe bleiben sichtbar.</p>
        <Bars title="Prüfstände" values={data.reviewStates.map(v => ({ ...v, label: reviewLabels[v.key] ?? v.label }))} choose={key => focus(`review:${key}`)} />
      </section><section className="panel"><SectionHead title="Risikogruppen" /><Badge tone="amber">Fachliche Gruppierung nicht verfügbar</Badge>
        <p className="muted">{data.riskBoundary}</p><Bars title="Verfügbarkeit der Risikobewertung" values={data.riskGroups} choose={key => focus(`risk:${key}`)} />
      </section><section className="panel"><SectionHead title="Regulatorischer Fristenanlass" />
        <p className="muted">Keine klinische Risikogruppe und kein Todesfallnachweis. Der dokumentierte Patientenausgang bleibt mit seinem eigenen Quellen-/Prüfstand in der Fallakte.</p>
        <Bars title="Fristenklassen" values={data.urgencyClasses} choose={key => focus(`urgency:${key}`)} />
      </section></div>
      <section className="panel cockpit-results" ref={list} aria-label="Zugehörige Fallakten"><SectionHead title="Zugehörige Fallakten">
        <button className="text-button" onClick={() => focus('all')}>Alle {number(data.totalCases)} Fallakten der Auswahl</button></SectionHead>
        <p className="muted">Drilldown: {data.focus} · {number(data.matchingCases)} Treffer. Servergefiltert, maximal {data.pageSize} Akten pro Seite. Risiko bleibt unbewertet/nicht gruppierbar.</p>
        {!data.cases.length ? <Empty title="Keine Fallakten für diesen Filter" /> : <div className="workspace-table"><table className="case-table">
          <thead><tr><th>Fallakte / Eingang</th><th>Tatsächlicher Prozessschritt</th><th>Analyse / Prüfstand</th><th>Fristenanlass / Patientenausgang</th><th>Öffnen</th></tr></thead>
          <tbody>{data.cases.map(c => <tr key={c.id} data-case-id={c.id}><td><button className="case-title" onClick={() => open(c.id)}>{c.title}</button>
            <span className="subline">{c.reference} · {c.productId} · v{c.version}</span><span className="subline">{date(c.systemReceivedAt, true, data.timeZone)}</span></td>
            <td><Badge>{c.stepLabel}</Badge><span className="subline">{label(c.stage)}</span></td><td>{reviewLabels[c.reviewState] ?? c.reviewState}<span className="subline">{c.assessmentMode ?? 'Kein Assessment'}</span></td>
            <td><Criticality seriousness={c.seriousness} outcome={c.outcome} compact /></td><td><button className="icon-button" aria-label={`${c.reference} öffnen`} onClick={() => open(c.id)}><ArrowUpRight size={18} /></button></td></tr>)}
          </tbody></table></div>}
        <Pagination offset={data.offset} total={data.matchingCases} size={data.pageSize} change={offset => setFilters(f => ({ ...f, offset }))} />
      </section>
    </>}
  </div>
}

export function DeadlineCalendarWorkspace({ products, revision, open }: { products: Device[]; revision: number; open: (event: CalendarEvent) => void }) {
  const [filters, setFilters] = useState(() => ({ productId: '', month: `${civilDay(new Date().toISOString(), 'Europe/Berlin').slice(0, 7)}-01`,
    timeZone: 'Europe/Berlin', kind: 'all', status: 'all', day: '', offset: 0 }))
  const query = new URLSearchParams({ month: filters.month, timeZone: filters.timeZone, kind: filters.kind, status: filters.status,
    offset: String(filters.offset), ...(filters.productId ? { productId: filters.productId } : {}), ...(filters.day ? { day: filters.day } : {}) })
  const { data, error, reload } = useSnapshot<CalendarSnapshot>(`/deadline-calendar?${query}`, revision)
  const month = (value: string) => setFilters(f => ({ ...f, month: value, day: '', offset: 0 }))
  return <div className="stack">
    <section className="panel"><div className="workspace-filters">
      <ProductFilter products={products} value={filters.productId} change={productId => setFilters(f => ({ ...f, productId, offset: 0 }))} />
      <ZoneFilter value={filters.timeZone} change={timeZone => setFilters(f => ({ ...f, timeZone, offset: 0 }))} />
      <label className="field compact">Fristtyp<select value={filters.kind} onChange={e => setFilters(f => ({ ...f, kind: e.target.value, offset: 0 }))}>
        <option value="all">Alle Fristtypen</option>{Object.entries(kindLabels).map(([key, label]) => <option key={key} value={key}>{label}</option>)}</select></label>
      <label className="field compact">Terminstatus<select value={filters.status} onChange={e => setFilters(f => ({ ...f, status: e.target.value, offset: 0 }))}>
        <option value="all">Alle einschließlich Historie</option><option value="open">Nur offene Terminpflichten</option></select></label>
      <button type="button" className="icon-button" aria-label="Kalender aktualisieren" onClick={reload}><RefreshCw size={18} /></button>
    </div><p className="muted">Nur gespeicherte Termine: keine Verschiebung, Neuberechnung oder automatische Erledigung. LegalDue und SafetyDue sind getrennte Marker derselben Pflicht. PSUR-Datumswerte bleiben reine Kalendertage.</p>
      {error && <ErrorBanner message={error} />}
    </section>
    <section className="panel"><div className="calendar-heading"><button className="icon-button" aria-label="Vorheriger Monat" onClick={() => month(moveMonth(filters.month, -1))}><ChevronLeft /></button>
      <label className="field compact">Kalendermonat<input type="month" value={filters.month.slice(0, 7)} onChange={e => { if (/^\d{4}-\d{2}$/.test(e.target.value)) month(`${e.target.value}-01`) }} /></label>
      <button className="icon-button" aria-label="Nächster Monat" onClick={() => month(moveMonth(filters.month, 1))}><ChevronRight /></button>
      <button className="button secondary" onClick={() => month(`${civilDay(new Date().toISOString(), filters.timeZone).slice(0, 7)}-01`)}>Aktueller Monat</button>
      <Badge><CalendarDays size={14} />{filters.timeZone}</Badge></div>
      {!data && !error && <Loading />}
      {data && <>
        <p className="workspace-asof">SQL-Lesestand {date(data.asOf, true, data.timeZone)} · {number(data.totalEvents)} Marker · {number(data.obligations)} unterschiedliche Pflichten · {number(data.openObligations)} offen · {number(data.overduePrimaryObligations)} überfällige Haupttermine im Monatsfilter{error ? ' · nicht aktuell bestätigt' : ''}.</p>
        {!!data.missingPsurDates && <p className="callout">{number(data.missingPsurDates)} berechtigte PSUR-Altberichte haben keinen gespeicherten Serien-/Einreichungstag. Termin nicht verfügbar, nicht als erledigt oder Nullbestand gewertet.</p>}
        <div className="calendar-legend">{Object.entries(kindLabels).map(([key, label]) => <span key={key} className={`calendar-kind kind-${key}`}>{label}</span>)}</div>
        <div className="calendar-scroll"><table className="month-calendar"><caption>{new Intl.DateTimeFormat('de-DE', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(new Date(`${data.month}T00:00:00Z`))} · Anzeigezeitzone {data.timeZone}</caption>
          <thead><tr>{['Montag', 'Dienstag', 'Mittwoch', 'Donnerstag', 'Freitag', 'Samstag', 'Sonntag'].map(d => <th key={d} scope="col">{d}</th>)}</tr></thead>
          <tbody>{calendarWeeks(data.month).map((week, i) => <tr key={i}>{week.map((d, j) => {
            const value = data.days.find(day => day.date === d)
            return <td key={d ?? `blank-${j}`} className={!d ? 'calendar-blank' : ''}>{d && value && <button type="button" aria-pressed={data.day === d}
              className={d === civilDay(data.asOf, data.timeZone) ? 'calendar-today' : ''}
              aria-label={`${d}: ${value.events} Termine, ${value.openEvents} offen`} onClick={() => setFilters(f => ({ ...f, day: f.day === d ? '' : d, offset: 0 }))}>
              <b>{Number(d.slice(-2))}</b><span>{number(value.events)} Termine</span>{value.overdueMarkers > 0 && <small className="danger-text">{value.overdueMarkers} überfällige Marker</small>}
              {value.kinds.map(k => <small key={k.key} className={`calendar-kind kind-${k.key}`}>{kindLabels[k.key]}: {k.count}</small>)}
            </button>}</td>
          })}</tr>)}</tbody></table></div>
      </>}
    </section>
    {data && <section className="panel" aria-label="Fristenagenda"><SectionHead title={data.day ? `Agenda · ${date(data.day)}` : 'Agenda · gesamter Monat'}>
      {data.day && <button className="text-button" onClick={() => setFilters(f => ({ ...f, day: '', offset: 0 }))}>Gesamten Monat anzeigen</button>}</SectionHead>
      <p className="muted">Datum/Uhrzeit zuerst in {data.timeZone}; Originalzeitzone und Quellfassung stehen an jedem Termin. Überfällige SafetyDue ist eine interne Vorwarnung, keine Überschreitung der LegalDue. Reine PSUR-Datumsvergleiche sind keine neue gesetzliche Fristberechnung.</p>
      {!data.events.length && <Empty title="Keine gespeicherten Termine im gewählten Filter" />}
      {data.events.map(event => <article className="calendar-event" key={event.id} data-testid="calendar-event" data-obligation-id={event.obligationId}>
        <div className="row-between"><span className={`calendar-kind kind-${event.kind}`}>{kindLabels[event.kind]}</span>
          <Badge tone={event.timing === 'Overdue' ? 'danger' : event.isOpen ? 'amber' : 'neutral'}>{timingLabels[event.timing]}</Badge></div>
        <h3><button className="text-button" onClick={() => open(event)}>{event.reference} · {event.title}<ArrowUpRight size={16} /></button></h3>
        <p><time dateTime={event.dueDate ?? event.dueAt ?? undefined}>{event.dueDate ? `${date(event.dueDate)} · ohne Uhrzeit` : date(event.dueAt, true, data.timeZone)}</time>
          {' · '}{sourceStatuses[event.status] ?? label(event.status)} · {event.products.join(', ')}</p>
        <p className="muted">{event.dueDate ? 'Original: reiner Kalendertag; keine Zeitzone gespeichert.' :
          `Originalzeitzone: ${event.originalTimeZone ?? 'nicht gespeichert'} · ${date(event.dueAt, true, event.originalTimeZone ?? 'UTC')}`}
          {!event.isCurrent && ' · Ältere Fassung/Runde, keine aktuelle offene Pflicht daraus abgeleitet.'}</p>
        <details><summary>Gespeicherte Fälligkeit, Quelle und Fristgrundlage</summary><p className="mono">{event.entityKind}:{event.entityId}@{event.sourceVersion} · {event.sourceField}</p>
          <p className="mono">{event.dueDate ?? event.dueAt}</p>{event.details.map((detail, i) => <p key={i}>{detail}</p>)}</details>
      </article>)}
      <Pagination offset={data.offset} total={data.matchingEvents} size={data.pageSize} change={offset => setFilters(f => ({ ...f, offset }))} />
    </section>}
  </div>
}
