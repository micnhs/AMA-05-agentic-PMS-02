import { useCallback, useEffect, useState } from 'react'
import { Activity, ArrowRight, ArrowUpRight, Bell, BookOpen, CalendarClock, CircleHelp, ClipboardCheck, FileClock, FileText, Inbox, LayoutDashboard, LogOut, Plus, Radio, Search, ShieldCheck, Sparkles, Waypoints } from 'lucide-react'
import { api, loginEntra, logout, session as getSession } from './api'
import type { CalendarEvent, CaseSummary, Device, PsurFocus, SafetyCase, Session, Status } from './types'
import { ActionDialog, Badge, CaseTable, date, ErrorBanner, Loading, roleNames, type Dialog } from './ui'
import Workbench from './Workbench'
import { AuditWorkspace, EvidenceWorkspace, FeedbackWorkspace, PsurWorkspace, RetentionWorkspace, SourcesWorkspace, SummaryWorkspace } from './Workspaces'
import { CockpitWorkspace, DeadlineCalendarWorkspace } from './WorkspaceViews'

const navigation = [
  { id: 'cockpit', title: 'PMS Cockpit', icon: LayoutDashboard },
  { id: 'inbox', title: 'Signal Inbox', icon: Inbox },
  { id: 'cases', title: 'Fallakten', icon: FileText },
  { id: 'reviews', title: 'Review Queue', icon: ClipboardCheck },
  { id: 'regulatory', title: 'Regulatory Desk', icon: ShieldCheck },
  { id: 'deadlines', title: 'Fristen', icon: CalendarClock },
  { id: 'feedback', title: 'Trends & CAPA', icon: Activity },
  { id: 'psur', title: 'PSUR Workspace', icon: BookOpen },
  { id: 'evidence', title: 'Evidenzbibliothek', icon: Waypoints },
  { id: 'retention', title: 'Aufbewahrung', icon: FileClock },
  { id: 'audit', title: 'Audit Explorer', icon: FileClock },
]
export default function App() {
  const [auth, setAuth] = useState<Session | null>(null)
  const [page, setPage] = useState('cockpit')
  const [cases, setCases] = useState<CaseSummary[]>([])
  const [products, setProducts] = useState<Device[]>([])
  const [status, setStatus] = useState<Status | null>(null)
  const [selected, setSelected] = useState<SafetyCase | null>(null)
  const [query, setQuery] = useState('')
  const [error, setError] = useState('')
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const [revision, setRevision] = useState(0)
  const [animation, setAnimation] = useState(false)
  const [working, setWorking] = useState(false)
  const [psurFocus, setPsurFocus] = useState<PsurFocus | null>(null)
  const needsCaseList = ['cases', 'reviews', 'regulatory', 'inbox', 'feedback', 'audit'].includes(page)
  useEffect(() => { getSession().then(setAuth).catch(e => setError(e.message)) }, [])
  const refresh = useCallback(async () => {
    if (!auth?.authenticated) return
    const [c, p, s] = await Promise.all([needsCaseList ? api<CaseSummary[]>('/cases') : Promise.resolve(null), api<Device[]>('/products'), api<Status>('/status')])
    if (c) setCases(c)
    setProducts(p); setStatus(s)
    if (page === 'workbench' && selected) setSelected(await api<SafetyCase>(`/cases/${selected.id}`))
  }, [auth?.authenticated, selected?.id, page, needsCaseList])
  useEffect(() => { if (!auth?.authenticated) return; refresh().catch(e => setError(e.message)); const id = window.setInterval(() => refresh().catch(e => setError(e.message)), 10000); return () => clearInterval(id) }, [auth?.authenticated, refresh])
  async function open(id: string) { try { setSelected(await api<SafetyCase>(`/cases/${id}`)); setPage('workbench') } catch (e) { setError(e instanceof Error ? e.message : 'Fall nicht geladen.') } }
  function openCalendarEvent(event: CalendarEvent) {
    if (event.entityKind === 'case') { void open(event.entityId); return }
    if (!event.psurId) { setError('Der gespeicherte PSUR-Bezug fehlt; keine andere Fassung geöffnet.'); return }
    setPsurFocus({ reportId: event.psurId, ...(event.entityKind === 'psurReview' ? { reviewId: event.entityId, round: event.round ?? undefined } : {}) })
    setPage('psur')
  }
  async function mutate(path: string, body: unknown) {
    setWorking(true); setError('')
    try { await api(path, body); await refresh(); setRevision(r => r + 1) }
    catch (e) { setError(e instanceof Error ? e.message : 'Änderung nicht bestätigt.'); throw e }
    finally { setWorking(false) }
  }
  const newComplaint = () => setDialog({ title: 'Synthetische Beschwerde aufnehmen', description: 'Keine echten Patientenangaben. Die erste Kenntnis eines Mitarbeiters oder Vertreters ist maßgeblich, nicht dieser Systemeingang.', submitLabel: 'Fall persistent aufnehmen',
    fields: [
      { name: 'incidentKey', label: 'Eindeutige synthetische Vorfallreferenz', value: `DEMO-${Date.now().toString().slice(-6)}` },
      { name: 'productId', label: 'Produktfamilie', type: 'select', options: products.map(p => ({ value: p.id, label: p.name })) },
      { name: 'title', label: 'Titel', value: 'Synthetischer Hinweis auf Funktionsverlust', minLength: 5 },
      { name: 'description', label: 'Synthetischer Originalhinweis', type: 'textarea', value: 'SYNTHETISCH: Eine vorzeitige Revision nach Funktionsverlust wurde berichtet. Der direkte Explantatbefund und die klinische Ursachenabklärung liegen noch nicht vor.', minLength: 20 },
      { name: 'udi', label: 'UDI, falls bekannt (leer = unbekannt)', required: false },
      { name: 'awarenessAt', label: 'Erste Organisationskenntnis (lokale Eingabezeit)', type: 'datetime-local', value: new Date(Date.now() - new Date().getTimezoneOffset() * 60000 - 86400000).toISOString().slice(0, 16) },
      { name: 'jurisdiction', label: 'Getrenntes Rechtsprofil', type: 'select', options: ['EU-IE', 'EU-DE', 'EU-FR', 'EU-IT', 'GB', 'NI'].map(x => ({ value: x, label: x })) },
      { name: 'seriousness', label: 'Frühe Triage / konservativer Fristenanlass', type: 'select', options: [{ value: 'DeathOrSeriousDeterioration', label: 'Tod / unvorhergesehene schwere Verschlechterung (10 Tage)' }, { value: 'PublicHealthThreat', label: 'Öffentliche Gesundheitsgefahr (2 Tage)' }, { value: 'OtherSerious', label: 'Sonstiges schwerwiegendes Vorkommnis (15 Tage)' }] },
    ], onSubmit: async values => { await mutate('/cases', { ...values, sourceId: crypto.randomUUID(), udi: values.udi || null, batch: null, country: values.jurisdiction === 'NI' ? 'GB' : values.jurisdiction.replace('EU-', ''), awarenessAt: new Date(values.awarenessAt).toISOString(), occurredAt: null, synthetic: true, scenario: 'user-synthetic' }); setPage('inbox') },
  })
  if (!auth?.authenticated) return <div className="login-shell"><section className="login-brand"><img src="/brand/cohors-logo.png" alt="Original Cohors Logo" /><span className="eyebrow">PRODUCT SAFETY, TOGETHER.</span><h1>Aus Informationen<br />wird Verantwortung.</h1><p>Ein durchgängiger PMS-Regelkreis.<br />KI begründet. Menschen entscheiden.</p><div className="login-principles"><span><ShieldCheck />Zwei menschliche Freigaben</span><span><Waypoints />Prüfbare Evidenz</span><span><CalendarClock />Unabhängige Fristen</span></div></section><section className="login-form"><Badge tone="amber">Synthetischer Pilot</Badge><h2>Willkommen bei Cohors</h2><p>Melden Sie sich mit Ihrem eigenen Konto an.<br />Rollen und Produktzugriffe prüft der Server.</p>{error && <ErrorBanner message={error} />}
    {!auth && !error ? <Loading /> : auth?.mode === 'Entra' ? <button className="button primary" onClick={() => loginEntra(auth.entra).then(setAuth).catch(e => setError(e.message))}>Mit Microsoft Entra anmelden <ArrowRight size={18} /></button> : <form onSubmit={async e => { e.preventDefault(); setError(''); const form = new FormData(e.currentTarget); try { await api('/auth/login', { username: form.get('username'), password: form.get('password') }); setAuth(await getSession()) } catch (e) { setError(e instanceof Error ? e.message : 'Anmeldung fehlgeschlagen.') } }}>
      <label className="field">Benutzername<input name="username" autoComplete="username" placeholder="pms.demo" required /></label><label className="field">Passwort<input name="password" type="password" autoComplete="current-password" required /></label><button className="button primary full">Sicher anmelden <ArrowRight size={18} /></button></form>}
    <div className="login-note"><CircleHelp size={18} /><p>Lokale Konten werden beim Start getrennt erzeugt. Zugangsdaten stehen in der lokalen Demo-Zugangsdatei. Kein Rollenumschalter, keine produktiven Identitäten.</p></div><div className="fine-print">Keine echten Patientenfälle. Keine echten Behördenmeldungen.<br />Keine regulatorische Betriebsfreigabe.</div></section></div>
  if (!status) return <div className="page-content">{error ? <ErrorBanner message={error} /> : <Loading />}</div>
  const actor = auth.actor!
  const filtered = cases.filter(c => `${c.title} ${c.reference} ${c.productId}`.toLowerCase().includes(query.toLowerCase()))
  const pageTitle = page === 'workbench' ? 'Case Workbench' : navigation.find(n => n.id === page)?.title ?? 'Cohors'
  return <div className="app-shell">
    <aside className="sidebar"><a className="brand" href="#cockpit" onClick={e => { e.preventDefault(); setPage('cockpit') }}><img src="/brand/cohors-logo.png" alt="Cohors Original-Logo" /><span><strong>Cohors</strong><small>PRODUCT SAFETY</small></span></a><div className="workspace-name"><span className="workspace-initial">C</span><div>Cohors Medical<small>Synthetischer Hersteller</small></div><Badge>III</Badge></div><span className="nav-label">ARBEITSBEREICH</span><nav>{navigation.map(n => <button key={n.id} className={`nav-item ${page === n.id || page === 'workbench' && n.id === 'cases' ? 'active' : ''}`} onClick={() => { setPage(n.id); setQuery(''); setPsurFocus(null) }}><n.icon size={19} />{n.title}</button>)}</nav>
      <div className="sidebar-bottom"><div className="pilot-card"><Radio size={18} /><div><b>Kontrollierter Pilot</b><small>Replay / Live getrennt<br />Meldungen nur simuliert</small></div></div><button className="profile" onClick={async () => { try { await logout(auth.mode); setSelected(null); setCases([]); setProducts([]); setStatus(null); setAuth(await getSession()) } catch (e) { setError(e instanceof Error ? e.message : 'Abmeldung nicht bestätigt.') } }} title="Abmelden und mit eigenem getrenntem Konto anmelden"><span className="avatar">{actor.name.split(' ').map(s => s[0]).slice(0, 2).join('').toUpperCase()}</span><span><b>{actor.name}</b><small>{roleNames[actor.roles[0]] ?? actor.roles[0]}</small></span><LogOut size={16} /></button></div></aside>
    <main className="main"><header className="topbar"><div className="breadcrumb">Workspace <span>/</span> <b>{pageTitle}</b></div><div className="topbar-right"><Badge tone="amber">PILOT · SYNTHETISCH</Badge><span className="top-date">{date(new Date().toISOString())}</span><button className="icon-button" onClick={() => setPage('deadlines')} aria-label="Fristen und Warnungen"><Bell size={19} /><i className="notification-dot" /></button></div></header>
      <div className="page-content">{error && <ErrorBanner message={error} dismiss={() => setError('')} />}
        {status?.heartbeatMissing && <ErrorBanner message="Kein gültiger Watchdog-Erfolg in den letzten drei Minuten. Auch ein nie gestarteter Host oder eine Zeitabweichung ist kein Nachweis sicherer Fristen." />}
        {page !== 'workbench' && <div className="page-heading"><div><span className="eyebrow">{page === 'cockpit' ? 'POST-MARKET SURVEILLANCE' : 'VERANTWORTUNG MIT EVIDENZ'}</span><h1>{page === 'cockpit' ? 'Sicherheit im Blick.' : pageTitle}</h1><p>{page === 'cockpit' ? 'Jeder Hinweis zählt. Jede Entscheidung ist nachvollziehbar.' : 'Persistierte Daten, nachvollziehbare Versionen und klare menschliche Verantwortung.'}</p></div>{actor.roles.includes('PmsSpecialist') && <button className="button primary" onClick={newComplaint}><Plus size={17} />Beschwerde aufnehmen</button>}</div>}
        {page === 'cockpit' && <>
          <CockpitWorkspace products={products} revision={revision} open={open} />
          <section className="panel loop-panel cockpit-loop"><span className="eyebrow">DER COHORS-REGELKREIS</span><h2>KI entwirft. Menschen entscheiden.</h2><div className="loop-steps">{[{ icon: Inbox, title: 'Aufnehmen', sub: 'Quellen und Kenntnis sichern' }, { icon: Sparkles, title: 'Analysieren', sub: 'Begründungen mit Fundstellen' }, { icon: ClipboardCheck, title: 'Prüfen & freigeben', sub: 'Medical Safety + Regulatory Affairs' }, { icon: Waypoints, title: 'Lernen & nachverfolgen', sub: 'Meldung, CAPA, Trends und PSUR' }].map((s, i) => <div key={s.title}><span><s.icon size={18} /></span><p><b>{s.title}</b><small>{s.sub}</small></p><small className="step-index">0{i + 1}</small></div>)}</div><button className="text-button" onClick={() => setAnimation(true)}>Original-Logoanimation ansehen <ArrowUpRight size={16} /></button></section>
          <div className="system-strip"><span><span className="status-dot" />{status?.persistence ?? 'SQL'} · führender Zustand</span><span>Foundry: {status?.foundryConfigured ? 'Endpoint gesetzt · Live-Abnahme offen' : 'nicht konfiguriert'}</span><span>Fristen-Watchdog: {status?.heartbeatMissing ? 'prüfen' : 'aktiv'}</span><span>Behörden: ausschließlich Simulator</span></div>
        </>}
        {['cases', 'reviews', 'regulatory', 'inbox'].includes(page) && <>
          <section className="panel"><div className="list-toolbar"><div className="search-field"><Search size={18} /><input aria-label="Fälle durchsuchen" placeholder="Fall, Produkt oder Referenz suchen…" value={query} onChange={e => setQuery(e.target.value)} /></div><Badge>{filtered.length} Fälle</Badge></div><CaseTable cases={page === 'reviews' ? filtered.filter(c => c.stage === 'AwaitingMedicalReview' || c.reportState === 'MedicalApproved') : page === 'regulatory' ? filtered.filter(c => c.reportState !== null) : filtered} open={open} /></section>
          {page === 'inbox' && <SourcesWorkspace revision={revision} actor={actor} mutate={mutate} openDialog={setDialog} />}
          {page === 'regulatory' && <SummaryWorkspace cases={cases} products={products} revision={revision} actor={actor} mutate={mutate} openDialog={setDialog} />}
        </>}
        {page === 'workbench' && selected && <Workbench key={selected.id} value={selected} actor={actor} openDialog={setDialog} mutate={mutate} busy={working} back={() => setPage('cases')} />}
        {page === 'deadlines' && <DeadlineCalendarWorkspace products={products} revision={revision} open={openCalendarEvent} />}
        {page === 'feedback' && <FeedbackWorkspace actor={actor} cases={cases} revision={revision} openDialog={setDialog} mutate={mutate} />}
        {page === 'psur' && <PsurWorkspace key={psurFocus ? `${psurFocus.reportId}:${psurFocus.reviewId ?? ''}:${psurFocus.round ?? ''}` : 'manual'} actor={actor} revision={revision} openDialog={setDialog} mutate={mutate} initialFocus={psurFocus ?? undefined} />}
        {page === 'evidence' && <EvidenceWorkspace actor={actor} revision={revision} mutate={mutate} openDialog={setDialog} />}
        {page === 'retention' && <RetentionWorkspace actor={actor} revision={revision} mutate={mutate} openDialog={setDialog} />}
        {page === 'audit' && <AuditWorkspace cases={cases} products={products} revision={revision} />}
        <footer className="page-footer"><span>Cohors · Synthetic pilot 2026.1</span><span>118 Anforderungen zugeordnet · Fach-/Betriebsfreigabe offen · Keine Azure-Bereitstellung</span></footer>
      </div>
    </main>
    {dialog && <ActionDialog key={dialog.title} dialog={dialog} close={() => setDialog(null)} />}
    {animation && <div className="video-overlay" role="dialog" aria-modal="true" aria-label="Cohors Original-Logoanimation"><div><button className="button secondary" onClick={() => setAnimation(false)}>Überspringen / schließen</button>{window.matchMedia('(prefers-reduced-motion: reduce)').matches ? <img src="/brand/cohors-logo.png" alt="Statisches Cohors Original-Logo statt Animation" /> : <video src="/brand/cohors-animation.mp4" controls autoPlay muted playsInline onEnded={() => setAnimation(false)} />}</div></div>}
  </div>
}
