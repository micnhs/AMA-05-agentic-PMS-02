import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from './api'
import type { Actor, Device, ProductGovernance, VersionReference } from './types'
import { ActionDialog, Badge, date, ErrorBanner, hash, Loading, SectionHead, type Dialog, type Field } from './ui'
import EudamedDossiers, { type DossierSummary } from './EudamedDossiers'

interface ActorDetails {
  organization: string; role: string; country: string; address: string; synthetic: boolean
  contact: { firstName: string; familyName: string; email: string; telephone: string }
}
export interface ActorApplication {
  id: string; version: number; productId: string; details: ActorDetails; status: string; simulatedIdentity: string | null
  origin: string; createdAt: string
  rounds: { revision: number; status: string; scenario: string; issuer: string; reason: string; at: string; applicationHash: string }[]
}
interface ReferenceMapping {
  id: string; version: number; productId: string; actorApplication: VersionReference; actorIdentity: string
  governanceSource: VersionReference; regulatoryDevice: ProductGovernance['device']; udiIssuingAgency: string
  evaluatingNotifiedBody: string; mappingRationale: string; confirmedBy: string; confirmedAt: string
  certificate: { number: string; revision: string; validFrom: string; validThrough: string; originalHash: string; verificationStatus: string }
}
interface ReferenceChoice { id: string; version: string }
interface DomibusProfile {
  endpointReference: string; secretReference: string; secretVersion: string; moduleKeyReference: string; moduleKeyVersion: string
  domain: string; accessPointPartyId: string; receiverPartyId: string; partyIdType: string; fromRole: string; toRole: string
  service: string; serviceType: string; action: string; originalSender: string; finalRecipient: string; onboardingEvidenceReference: string
}
interface Profile {
  mode: 'Simulator' | 'Domibus'; environment: 'Simulation' | 'Playground' | 'Production'
  actorId: string; actorVersion: number; referencesId: string; referencesVersion: number; recipient: string
  businessContract: string; gatewayContract: string; domibus: DomibusProfile | null
}
interface Readiness {
  configurationStatus: string; connectionStatus: string; businessStatus: string; canActivate: boolean; canDispatch: boolean
  blockers: string[]; evidenceLevel: string
}
export interface IntegrationWorkspace {
  effectiveMode: 'Simulator' | 'Domibus'; persistedConfiguration: boolean
  activeChannel: { id: string; version: number; dispatchGeneration: number; profile: Profile; enabled: boolean } | null
  draft: { id: string; version: number; profile: Profile; changedBy: string; changedAt: string } | null
  activeReadiness: Readiness; draftReadiness: Readiness | null
  actors: ActorApplication[]; references: ReferenceMapping[]
  dossiers: DossierSummary[]
  referenceCatalog: { endpoints: string[]; credentials: ReferenceChoice[]; moduleKeys: ReferenceChoice[]; onboardingEvidence: string[] }
  contract: { business: string; gateway: string; wsdlSha256: string; schemaRuntime: string }
  boundary: string
}

const actorState: Record<string, string> = {
  Draft: 'Entwurf', Submitted: 'Simulatorantwort ausstehend', CorrectionRequired: 'Korrektur erforderlich',
  Rejected: 'Antrag abgelehnt', SimulatedApproved: 'Nur Simulator freigeschaltet',
}
const reasonField: Field = { name: 'reason', label: 'Nachvollziehbare Begruendung', type: 'textarea', minLength: 50 }
const domibusTextFields: { name: keyof DomibusProfile; label: string }[] = [
  { name: 'domain', label: 'Domibus-Domaene (optional)' },
  { name: 'accessPointPartyId', label: 'Eigene AP-Party-ID' }, { name: 'receiverPartyId', label: 'Empfaenger-AP-Party-ID' },
  { name: 'partyIdType', label: 'Party-ID-Typ' }, { name: 'fromRole', label: 'Absenderrolle im PMode' },
  { name: 'toRole', label: 'Empfaengerrolle im PMode' }, { name: 'service', label: 'Vertraglicher Service' },
  { name: 'serviceType', label: 'Service-Typ' }, { name: 'action', label: 'Vertragliche Action' },
  { name: 'originalSender', label: 'Original Sender (keine Test-SRN uebernehmen)' }, { name: 'finalRecipient', label: 'Final Recipient' },
]

export default function EudamedWorkspace({ actor, products }: { actor: Actor; products: Device[] }) {
  const allowedProducts = products.filter(value => actor.products.includes(value.id))
  const [product, setProduct] = useState(allowedProducts[0]?.id ?? '')
  const selected = allowedProducts.find(p => p.id === product) ?? allowedProducts[0]
  return <div className="eudamed-workspace">
    <label className="field">Integrations-Produktscope<select value={selected?.id ?? ''} onChange={event => setProduct(event.target.value)}>
      {allowedProducts.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
    </select></label>
    {selected ? <ProductIntegration key={selected.id} actor={actor} product={selected} /> : <p>Kein freigegebener Produktscope verfuegbar.</p>}
  </div>
}

function ProductIntegration({ actor, product }: { actor: Actor; product: Device }) {
  const [workspace, setWorkspace] = useState<IntegrationWorkspace | null>(null)
  const [governance, setGovernance] = useState<ProductGovernance | null>(null)
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const [audit, setAudit] = useState<{ title: string; value: unknown[] } | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const live = useRef(true)
  const generation = useRef(0)
  const loadsInFlight = useRef(0)
  const auditGeneration = useRef(0)
  const administrative = actor.isHuman && actor.roles.includes('RegulatoryAffairs')
  const author = actor.isHuman && (administrative || actor.roles.includes('PmsSpecialist'))
  const load = useCallback(async () => {
    const request = ++generation.current
    loadsInFlight.current++
    try {
      const [data, profiles] = await Promise.all([
        api<IntegrationWorkspace>(`/eudamed/${encodeURIComponent(product.id)}/workspace`),
        api<ProductGovernance[]>('/products/profiles'),
      ])
      if (!live.current || request !== generation.current) return
      setWorkspace(data); setGovernance(profiles.find(p => p.productId === product.id) ?? null); setError('')
    } catch (failure) {
      if (live.current && request === generation.current) setError(failure instanceof Error ? failure.message : 'Integrationsstand nicht geladen.')
    } finally { loadsInFlight.current-- }
  }, [product.id])
  useEffect(() => {
    live.current = true
    void load()
    const timer = window.setInterval(() => { if (loadsInFlight.current === 0) void load() }, 5000)
    return () => { live.current = false; generation.current++; auditGeneration.current++; window.clearInterval(timer) }
  }, [load])
  async function change(path: string, body: unknown) {
    generation.current++; auditGeneration.current++; setAudit(null); setBusy(true); setError('')
    try { await api(path, body); if (live.current) await load() }
    finally { if (live.current) setBusy(false) }
  }
  async function showAudit(kind: string, id: string, title: string) {
    const request = ++auditGeneration.current
    try {
      const value = await api<unknown[]>(`/eudamed/audit/${kind}/${encodeURIComponent(id)}`)
      if (live.current && request === auditGeneration.current) setAudit({ title, value })
    } catch (failure) {
      if (live.current && request === auditGeneration.current) setError(failure instanceof Error ? failure.message : 'Audit nicht geladen.')
    }
  }
  function editActor(value?: ActorApplication) {
    const details = value?.details
    setDialog({ title: value ? 'Actor-Antrag korrigieren' : 'Synthetischen Actor beantragen',
      description: 'Nur lokale Testregistrierung. Keine EU-Login-Anmeldung, amtliche SRN oder reale CA-/NB-Befugnis. Eine Korrektur entwertet die bisherige Testfreischaltung und davon abhaengige Referenzen.',
      submitLabel: 'Antragsfassung speichern', fields: [
        { name: 'organization', label: 'Synthetische Organisation (DEMO oder SYNTHETISCH)', value: details?.organization ?? 'DEMO Cohors Medical', minLength: 5 },
        { name: 'role', label: 'Actorrolle', type: 'select', value: details?.role ?? 'MF', options: [{ value: 'MF', label: 'MF - Hersteller' }, { value: 'AR', label: 'AR - Test-Bevollmaechtigter, kein Herstellerersatz' }] },
        { name: 'country', label: 'Zweistelliger Laendercode', value: details?.country ?? 'IE' },
        { name: 'address', label: 'Synthetische Anschrift', value: details?.address ?? 'SYNTHETISCH: Teststrasse 0, Testort', minLength: 10 },
        { name: 'firstName', label: 'Synthetischer Kontakt - Vorname', value: details?.contact.firstName ?? 'Demo', minLength: 2 },
        { name: 'familyName', label: 'Synthetischer Kontakt - Nachname', value: details?.contact.familyName ?? 'Safety', minLength: 2 },
        { name: 'email', label: 'Kontaktadresse unter .invalid', value: details?.contact.email ?? 'safety@example.invalid' },
        { name: 'telephone', label: 'Synthetische Telefonnummer', value: details?.contact.telephone ?? '000000', minLength: 5 },
        reasonField,
      ], onSubmit: async values => change(value ? `/eudamed/actors/${value.id}` : '/eudamed/actors', {
        expectedVersion: value?.version ?? 0, productId: value?.productId ?? product.id, reason: values.reason,
        details: { organization: values.organization, role: values.role, country: values.country, address: values.address, synthetic: true,
          contact: { firstName: values.firstName, familyName: values.familyName, email: values.email, telephone: values.telephone } },
      }) })
  }
  function submitActor(value: ActorApplication) {
    setDialog({ title: 'Actor-Antrag lokal einreichen', description: 'Die persistierte Outbox verarbeitet genau diese Antragsrevision. Das bewusst gewaehlte Testszenario ersetzt keine reale Actorvalidierung.',
      submitLabel: 'Antrag dem Simulator uebergeben', fields: [
        { name: 'scenario', label: 'Explizites Registrierungsszenario', type: 'select', options: [
          { value: 'Correction', label: 'Korrektur verlangen' }, { value: 'Reject', label: 'Antrag ablehnen' }, { value: 'Approve', label: 'Testidentitaet freischalten' },
        ] }, reasonField,
      ], onSubmit: async values => change(`/eudamed/actors/${value.id}/submit`, { expectedVersion: value.version, ...values }) })
  }
  function mapReferences() {
    if (!workspace || !governance) return
    const approved = workspace.actors.filter(a => a.status === 'SimulatedApproved' && a.details.role === 'MF')
    const current = workspace.references.find(r => r.productId === product.id)
    const from = new Date().toISOString().slice(0, 10)
    const through = new Date(new Date().getFullYear() + 1, 11, 31, 12).toISOString().slice(0, 10)
    setDialog({ title: 'Synthetische Referenzen zuordnen', description: `Bindet Produktprofil v${governance.version}, Basic-UDI ${governance.device.basicUdi} und Zertifikat ${governance.device.certificateId}. Kein offizieller Zertifikatsabruf und keine echte NB-Verifikation.`,
      submitLabel: 'Versionierte Zuordnung bestaetigen', fields: [
        { name: 'actorId', label: 'Freigeschalteter MF-Testactor', type: 'select', value: current?.actorApplication.id, options: approved.map(a => ({ value: a.id, label: `${a.details.organization} - v${a.version} - ${a.simulatedIdentity}` })) },
        { name: 'certificateRevision', label: 'Synthetische Zertifikatsrevision', value: current?.certificate.revision ?? '1' },
        { name: 'validFrom', label: 'Synthetisch gueltig ab', type: 'date', value: current?.certificate.validFrom ?? from },
        { name: 'validThrough', label: 'Synthetisch gueltig bis', type: 'date', value: current?.certificate.validThrough ?? through },
        { name: 'certificateText', label: 'Ausdruecklicher synthetischer Textbeleg', type: 'textarea', minLength: 80,
          value: `SYNTHETISCH: Lokaler Testnachweis fuer ${governance.device.certificateId}, Basic-UDI ${governance.device.basicUdi}. Keine amtliche Zertifikatspruefung, reale Marktfreigabe oder NB-Bestaetigung.` },
        { name: 'evaluatingNotifiedBody', label: 'Explizite evaluierende Test-NB', value: current?.evaluatingNotifiedBody ?? `DEMO-NB-${governance.device.notifiedBodyNumber}`, minLength: 8 },
        { name: 'udiIssuingAgency', label: 'Explizite UDI Issuing Agency', type: 'select', value: current?.udiIssuingAgency ?? 'GS1', options: ['GS1', 'HIBCC', 'ICCBBA', 'IFA', 'EUDAMED'].map(value => ({ value, label: value })) },
        reasonField,
      ], onSubmit: async values => {
        const selected = approved.find(a => a.id === values.actorId)
        if (!selected) throw new Error('Eine konkrete freigeschaltete MF-Testidentitaet auswaehlen.')
        await change('/eudamed/references', { ...values, productId: product.id, expectedVersion: current?.version ?? 0,
          actorVersion: selected.version, governanceVersion: governance.version })
      } })
  }
  function configure(mode: Profile['mode']) {
    if (!workspace) return
    const previous = workspace.draft?.profile.mode === mode ? workspace.draft.profile : null
    const mappings = workspace.references.filter(r => r.productId === product.id &&
      workspace.actors.some(a => a.id === r.actorApplication.id && a.version === r.actorApplication.version && a.status === 'SimulatedApproved'))
    const catalog = workspace.referenceCatalog
    const referenceOptions = (items: ReferenceChoice[]) => [{ value: '', label: 'Nicht konfiguriert' },
      ...items.map(item => ({ value: `${item.id}@${item.version}`, label: `${item.id} - Version ${item.version}` }))]
    const fields: Field[] = mode === 'Simulator' ? [
      { name: 'referencesId', label: 'Bestaetigte Actor-/Geraete-/Zertifikatszuordnung', type: 'select', value: previous?.referencesId,
        options: mappings.map(r => ({ value: r.id, label: `${r.actorIdentity} - Zuordnung v${r.version} - ${r.regulatoryDevice.basicUdi}` })) },
      { name: 'recipient', label: 'Expliziter synthetischer Empfaenger', value: previous?.recipient ?? 'DEMO-CA-IE' },
    ] : [
      { name: 'environment', label: 'Getrennte Zielumgebung', type: 'select', value: previous?.environment ?? 'Playground',
        options: [{ value: 'Playground', label: 'Playground - externe Aktivierung offen' }, { value: 'Production', label: 'Production - Vigilanz gesperrt' }] },
      { name: 'recipient', label: 'Vertraglicher Empfaenger (kein Test-Actor)', value: previous?.recipient ?? '', required: false },
      { name: 'endpointReference', label: 'Betreiberseitige Backend-Referenz', type: 'select', required: false, value: previous?.domibus?.endpointReference ?? '',
        options: [{ value: '', label: 'Nicht konfiguriert' }, ...catalog.endpoints.map(value => ({ value, label: value }))] },
      { name: 'credential', label: 'Versionierte Plugin-Zugangsreferenz - niemals Passwort', type: 'select', required: false,
        value: previous?.domibus?.secretReference ? `${previous.domibus.secretReference}@${previous.domibus.secretVersion}` : '', options: referenceOptions(catalog.credentials) },
      { name: 'moduleKey', label: 'Versionierte Modul-Key-Referenz - niemals Keywert', type: 'select', required: false,
        value: previous?.domibus?.moduleKeyReference ? `${previous.domibus.moduleKeyReference}@${previous.domibus.moduleKeyVersion}` : '', options: referenceOptions(catalog.moduleKeys) },
      { name: 'onboardingEvidenceReference', label: 'Betreiberseitige Onboarding-Nachweisreferenz', type: 'select', required: false, value: previous?.domibus?.onboardingEvidenceReference ?? '',
        options: [{ value: '', label: 'Nicht konfiguriert' }, ...catalog.onboardingEvidence.map(value => ({ value, label: value }))] },
      ...domibusTextFields.map(field => ({ ...field, value: previous?.domibus?.[field.name] ?? '', required: false })),
    ]
    setDialog({ title: `${mode}-Konfigurationsentwurf`, description: mode === 'Simulator'
      ? 'Speichern ist keine Aktivierung. Der Simulator verwendet keine externe Netzwerkdiagnose und keinen Domibus-Fallback.'
      : 'Unvollstaendige Entwuerfe bleiben sichtbar und inaktiv. Keine Uebernahme synthetischer Actor-/Zertifikatsidentitaeten. Betreiberreferenzen enthalten keine Geheimnisse; reale Registrierung und Serviceabnahme erfolgen nicht hier.',
    submitLabel: 'Nur Konfigurationsentwurf speichern', fields: [...fields, reasonField],
    onSubmit: async values => {
      const mapping = mappings.find(r => r.id === values.referencesId)
      if (mode === 'Simulator' && !mapping) throw new Error('Eine aktuelle bestaetigte Zuordnung auswaehlen.')
      const credential = catalog.credentials.find(r => `${r.id}@${r.version}` === values.credential)
      const moduleKey = catalog.moduleKeys.find(r => `${r.id}@${r.version}` === values.moduleKey)
      const domibus: DomibusProfile | null = mode === 'Simulator' ? null : {
        endpointReference: values.endpointReference, secretReference: credential?.id ?? '', secretVersion: credential?.version ?? '',
        moduleKeyReference: moduleKey?.id ?? '', moduleKeyVersion: moduleKey?.version ?? '', onboardingEvidenceReference: values.onboardingEvidenceReference,
        domain: values.domain, accessPointPartyId: values.accessPointPartyId, receiverPartyId: values.receiverPartyId,
        partyIdType: values.partyIdType, fromRole: values.fromRole, toRole: values.toRole, service: values.service,
        serviceType: values.serviceType, action: values.action, originalSender: values.originalSender, finalRecipient: values.finalRecipient,
      }
      await change('/eudamed/configuration', { expectedVersion: workspace.draft?.version ?? 0, productId: product.id, reason: values.reason,
        profile: { mode, environment: mode === 'Simulator' ? 'Simulation' : values.environment,
          actorId: mode === 'Simulator' ? mapping!.actorApplication.id : '', actorVersion: mode === 'Simulator' ? mapping!.actorApplication.version : 0,
          referencesId: mode === 'Simulator' ? mapping!.id : '', referencesVersion: mode === 'Simulator' ? mapping!.version : 0,
          recipient: values.recipient, businessContract: workspace.contract.business, gatewayContract: workspace.contract.gateway, domibus } })
    } })
  }
  function activate() {
    if (!workspace?.draft) return
    setDialog({ title: 'Konfigurationsrevision aktivieren',
      description: `Aktiviert ausschliesslich Entwurf v${workspace.draft.version}. Neue Dispatch-Generation: alte Auftraege werden nicht umgeleitet; bereits gestartete Versuche behalten ihr Profil. Fehlende Voraussetzungen blockieren den Serveraufruf.`,
      submitLabel: 'Profilwechsel bewusst bestaetigen', fields: [
        { name: 'impact', label: 'Auswirkung auf gebundene Auftraege', type: 'select', options: [
          { value: '', label: 'Bitte bewusst bestaetigen' }, { value: 'confirm', label: 'Neue Generation, kein Umleiten und kein automatischer Nachversand' },
        ] }, reasonField,
      ], onSubmit: async values => change(`/eudamed/${product.id}/activate`, { expectedVersion: workspace.activeChannel?.version ?? 0,
        draftVersion: workspace.draft!.version, confirmImpact: values.impact === 'confirm', reason: values.reason }) })
  }
  function deactivate() {
    if (!workspace?.activeChannel) return
    setDialog({ title: 'Kanal deaktivieren', description: 'Sperrt neue Claims mit einer neuen Dispatch-Generation. Originalprofile, alte Meldungen und deren Historie bleiben bestehen.',
      submitLabel: 'Kanal begruendet deaktivieren', fields: [reasonField],
      onSubmit: async values => change(`/eudamed/${product.id}/deactivate`, { expectedVersion: workspace.activeChannel!.version, reason: values.reason }) })
  }
  return <>
    {error && <ErrorBanner message={error} />}
    {!workspace ? <Loading /> : <>
      <div className="callout">Persistenter lokaler EUDAMED-Simulator: keine reale Registrierung, keine AS4- oder Behoerdenmeldung. Dossiers werden nur nach getrennten menschlichen Freigaben originalgebunden simuliert verarbeitet.</div>
      <div className="eudamed-status-grid">
        <section className="panel" aria-label="Tatsaechlich aktiver Modus"><span className="eyebrow">AKTIVER STAND</span>
          <h2>{workspace.effectiveMode}</h2><Badge tone="amber">{workspace.activeChannel?.enabled ? 'Ausdruecklich aktiviert' : workspace.activeChannel ? 'Deaktiviert' : 'Sichere Voreinstellung, nicht aktiviert'}</Badge>
          <p>Revision {workspace.activeChannel?.version ?? 0} · Dispatch-Generation {workspace.activeChannel?.dispatchGeneration ?? 0}</p>
          {administrative && workspace.activeChannel?.enabled && <button className="button secondary" disabled={busy} onClick={deactivate}>Kanal deaktivieren</button>}
        </section>
        <section className="panel" aria-label="Gewuenschter Konfigurationsentwurf"><span className="eyebrow">KONFIGURATIONSENTWURF</span>
          <h2>{workspace.draft?.profile.mode ?? 'Noch kein Entwurf'}</h2><p>{workspace.draft ? `Revision ${workspace.draft.version} · ${workspace.draft.profile.environment}` : 'Speichern und Aktivieren sind getrennte Schritte.'}</p>
          <Badge tone={workspace.draftReadiness?.canActivate ? 'neutral' : 'amber'}>{workspace.draftReadiness?.canActivate ? 'Im Simulator aktivierbar' : 'Nicht aktivierbar'}</Badge>
          {administrative && workspace.draft && <button className="button secondary" disabled={busy || !workspace.draftReadiness?.canActivate} onClick={activate}>Entwurf aktivieren</button>}
        </section>
        <section className="panel" aria-label="Versandbereitschaft"><span className="eyebrow">BEREITSCHAFT, KEIN SENDEBELEG</span>
          <h2>{workspace.activeReadiness.canDispatch ? 'Simulatorprofil vollstaendig' : 'Gesperrt'}</h2>
          <p>Backend: {workspace.activeReadiness.connectionStatus} · Fachlich: {workspace.activeReadiness.businessStatus}</p>
          <small>Nachweisebene: {workspace.activeReadiness.evidenceLevel}. Transport-ACK ist keine Geschaeftsannahme.</small>
        </section>
      </div>
      {[['Aktiver Stand', workspace.activeReadiness], ['Entwurf', workspace.draftReadiness]].map(([title, readiness]) => {
        if (typeof readiness === 'string' || !readiness?.blockers.length) return null
        return <section className="panel" key={String(title)}><h3>Blockaden - {String(title)}</h3><ul>{readiness.blockers.map(blocker => <li key={blocker}>{blocker}</li>)}</ul></section>
      })}
      <section className="panel"><SectionHead eyebrow="1 - SYNTHETISCHE REGISTRIERUNG" title="Actor-Antraege">
        {author && <button className="button primary" disabled={busy} onClick={() => editActor()}>Neuer Actor-Antrag</button>}
      </SectionHead>
        {!workspace.actors.length && <p>Noch kein persistierter Testantrag. Ein Browser-Rollenwechsel ersetzt keine Anmeldung mit dem berechtigten Konto.</p>}
        {workspace.actors.map(value => <article className="eudamed-record" key={value.id} data-actor-id={value.id}>
          <h3>{value.details.organization}</h3><Badge tone={value.status === 'SimulatedApproved' ? 'neutral' : 'amber'}>{actorState[value.status] ?? value.status}</Badge>
          <p>{value.details.role} · {value.details.country} · Ursprung {value.productId} · v{value.version} · {date(value.createdAt, true)}</p>
          {value.simulatedIdentity && <p><b>{value.simulatedIdentity}</b> - keine amtliche SRN</p>}
          <div className="eudamed-actions">
            {author && value.status !== 'Submitted' && <button className="button secondary" disabled={busy} onClick={() => editActor(value)}>Antrag korrigieren</button>}
            {administrative && value.status === 'Draft' && <button className="button primary" disabled={busy} onClick={() => submitActor(value)}>Lokal einreichen</button>}
            <button className="button secondary" onClick={() => void showAudit('eudamedActor', value.id, value.details.organization)}>Actor-Audit</button>
          </div>
          <details><summary>Antrags- und Antwortverlauf ({value.rounds.length})</summary>
            {value.rounds.map((round, index) => <div key={`${round.revision}:${index}`} className="eudamed-round"><b>Revision {round.revision} - {actorState[round.status] ?? round.status}</b>
              <p>{round.reason}</p><small>{date(round.at, true)} · {round.issuer} · Original {hash(round.applicationHash)}</small></div>)}
          </details>
        </article>)}
      </section>
      <section className="panel"><SectionHead eyebrow="2 - EXPLIZITE REFERENZEN" title="Hersteller, Geraet und Zertifikat">
        {administrative && <button className="button secondary" disabled={busy || !governance || !workspace.actors.some(a => a.status === 'SimulatedApproved' && a.details.role === 'MF')} onClick={mapReferences}>Referenzen zuordnen</button>}
      </SectionHead>
        {!governance && <ErrorBanner message="Kontrolliertes Produkt-/Herstellerprofil fehlt; keine Zuordnung aus Annahmen." />}
        {governance && <p>Produktprofil v{governance.version}: {governance.device.basicUdi} · {governance.device.certificateId} · {governance.device.notifiedBodyName}</p>}
        {workspace.references.map(value => <article key={value.id} className="eudamed-record">
          <h3>Zuordnung v{value.version}</h3><p>{value.actorIdentity} · UDI Issuer {value.udiIssuingAgency} · Test-NB {value.evaluatingNotifiedBody}</p>
          <p>Textbeleg {hash(value.certificate.originalHash)} · {date(value.certificate.validFrom)} bis {date(value.certificate.validThrough)}</p>
          <p>{value.mappingRationale}</p><small>{value.confirmedBy} · {date(value.confirmedAt, true)} · {value.certificate.verificationStatus}</small>
          <button className="button secondary" onClick={() => void showAudit('eudamedReferences', value.id, 'Referenzzuordnung')}>Zuordnungs-Audit</button>
        </article>)}
      </section>
      <section className="panel"><SectionHead eyebrow="3 - KANAL MIT GENERATIONSSPERRE" title="Konfiguration">
        {administrative && <div className="eudamed-actions">
          <button className="button secondary" disabled={busy || !workspace.references.length} onClick={() => configure('Simulator')}>Simulator konfigurieren</button>
          <button className="button secondary" disabled={busy} onClick={() => configure('Domibus')}>Domibus-Entwurf erfassen</button>
        </div>}
      </SectionHead><p>{workspace.boundary}</p>
        <p>Vertrag: {workspace.contract.business} · Gateway: {workspace.contract.gateway}</p><small>Release-WSDL SHA-256: {workspace.contract.wsdlSha256}</small>
        <div className="eudamed-actions">
          {workspace.draft && <button className="button secondary" onClick={() => void showAudit('eudamedConfiguration', workspace.draft!.id, 'Konfigurationsentwurf')}>Entwurfs-Audit</button>}
          {workspace.activeChannel && <button className="button secondary" onClick={() => void showAudit('eudamedChannel', workspace.activeChannel!.id, 'Aktiver Kanal')}>Kanal-Audit</button>}
          <button className="button secondary" disabled={busy} onClick={() => void load()}>Stand neu lesen</button>
        </div>
      </section>
      <EudamedDossiers actor={actor} product={product} workspace={workspace} refresh={load} />
      {audit && <section className="panel"><SectionHead title={`Audit - ${audit.title}`}><button className="button secondary" onClick={() => setAudit(null)}>Audit schliessen</button></SectionHead>
        <pre className="eudamed-audit">{JSON.stringify(audit.value, null, 2)}</pre></section>}
    </>}
    {dialog && <ActionDialog key={dialog.title} dialog={dialog} close={() => { if (live.current) setDialog(null) }} />}
  </>
}
