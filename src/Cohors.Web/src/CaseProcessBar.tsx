import { useEffect, useState } from 'react'
import { ArrowUpRight, Check, CircleAlert } from 'lucide-react'
import { api } from './api'
import type { Actor, CaseProcessView, SafetyCase } from './types'
import { Criticality, ErrorBanner, roleNames } from './ui'

const stateNames: Record<string, string> = { completed: 'Erledigt', current: 'Aktuell', blocked: 'Blockiert', pending: 'Ausstehend', historical: 'Historischer Beleg', 'not-required': 'Nicht erforderlich' }
const tabNames: Record<string, string> = { analysis: 'Analyse & Evidenz', report: 'Human Review & Meldung', information: 'Informationsbedarf', history: 'Chronologie & Läufe' }

export default function CaseProcessBar({ c, actor, navigate, prepare }: {
  c: SafetyCase; actor: Actor; navigate: (tab: string) => void; prepare: () => void
}) {
  const [loaded, setProgress] = useState<CaseProcessView | null>(null)
  const [error, setError] = useState('')
  const progress = loaded?.caseId === c.id && loaded.caseVersion === c.version ? loaded : null
  useEffect(() => {
    let active = true
    setProgress(null); setError('')
    api<CaseProcessView>(`/cases/${encodeURIComponent(c.id)}/process`).then(result => {
      if (!active) return
      if (result.caseId !== c.id || result.caseVersion !== c.version) {
        setError('Der Fall wurde inzwischen geändert. Aktuelle Fallfassung neu laden; kein veralteter Prozessstatus wird angezeigt.')
        return
      }
      setProgress(result)
    }).catch(error => { if (active) setError(error instanceof Error ? error.message : 'Prozessstatus nicht bestätigt.') })
    return () => { active = false }
  }, [c.id, c.version])
  const canPrepare = actor.roles.some(role => ['PmsSpecialist', 'MedicalSafetyReviewer'].includes(role))
  const prepareNext = progress?.nextAction === 'prepare' && canPrepare
  const current = progress?.steps.find(step => step.id === progress.currentStep)
  return <section className="case-process" aria-label="Fallprozess">
    <div className="row-between"><b>{c.reference} · Fallversion {c.version}</b><small>{progress?.jurisdiction ?? c.jurisdiction} · Nur Pilot / Simulator</small></div>
    <Criticality seriousness={c.seriousness} outcome={progress?.outcome} />
    {error ? <ErrorBanner message={error} /> : !progress ? <p role="status">Versionsgebundener Prozessstatus wird geladen …</p> : <>
      <ol aria-label="Prozessschritte" tabIndex={0}>{progress.steps.map(step => <li key={step.id} data-step={step.id}
        data-state={step.state} aria-current={step.id === progress.currentStep ? 'step' : undefined}>
        <span className="process-mark" aria-hidden="true">{step.state === 'completed' ? <Check size={14} /> : step.state === 'blocked' ? <CircleAlert size={14} /> : null}</span>
        <span><b>{step.label}</b><small>{stateNames[step.state] ?? 'Ungeklärt'} · {roleNames[step.role] ?? step.role}</small></span></li>)}</ol>
      <div className="process-next"><div aria-live="polite"><b>Aktuell: {current?.label}</b><p>{current?.detail}</p>
        <small>Nächster Schritt: {roleNames[progress.nextRole] ?? progress.nextRole}. {progress.recipient && `Gebundener Pilotempfänger: ${progress.recipient}.`}</small></div>
        <button className="button secondary" onClick={() => { navigate(progress.targetTab); if (prepareNext) prepare() }}>
          <ArrowUpRight size={16} />{prepareNext ? 'Entwurf vorbereiten' : tabNames[progress.targetTab] ?? 'Fallansicht öffnen'}
        </button></div>
      {progress.blockers.length > 0 && <details className="process-blockers"><summary>{progress.blockers.length} konkrete Blocker / offene Prüfungen</summary>
        <ul>{progress.blockers.map(text => <li key={text}>{text}</li>)}</ul></details>}
    </>}
  </section>
}
