export interface Actor { id: string; name: string; roles: string[]; products: string[]; isHuman: boolean }
export interface Session { authenticated: boolean; actor: Actor | null; csrfToken: string; mode: string; entra: { tenantId: string; clientId: string; scope: string } }
export interface Device { id: string; name: string; kind: string; model: string; basicUdi: string }
export interface Deadline { id: string; jurisdiction: string; legalDue: string; safetyDue: string; triggerAt: string; ruleVersion: string; calendarVersion: string; timeZone: string; calculation: string[]; status: string; immediateAction: boolean }
export interface FollowUpSchedule { reportDate: string; dueAt: string; safetyDue: string; timeZone: string; calendarVersion: string; ruleVersion: string }
export interface FollowUpObligation { id: string; jurisdiction: string; sourceReportHash: string; sourceReportVersion: number; sourceReceiptReference: string; schedule: FollowUpSchedule; status: string; createdAt: string; completedByReportHash: string | null; completedAt: string | null }
export interface Evidence { id: string; version: number; title: string; kind: string; content: string; hash: string; locator: string; origin: string; approved: boolean; synthetic: boolean; recordedAt: string; productId: string; validFrom: string }
export interface EvidenceDocument { evidence: Evidence; content: string; hashVerified: boolean }
export interface CaseOutcomeSource { code: string; status: string; term: string; source: string }
export interface CaseOutcome extends CaseOutcomeSource { sources: CaseOutcomeSource[] }
export interface CaseProcessView {
  caseId: string; caseVersion: number; reference: string; jurisdiction: string; recipient: string;
  currentStep: string; nextAction: string; nextRole: string; targetTab: string; blockers: string[];
  steps: { id: string; label: string; state: string; role: string; detail: string }[]; outcome: CaseOutcome;
}
export interface WorkspaceCount { key: string; label: string; count: number }
export interface IntakePoint { from: string; through: string; count: number }
export interface CockpitCase {
  id: string; version: number; reference: string; productId: string; title: string; stage: string;
  seriousness: string; outcome: CaseOutcome; systemReceivedAt: string; currentStep: string; stepLabel: string;
  reviewState: string; assessmentMode: string | null; riskGroup: string;
  openObligations: number; upcomingObligations: number; overdueObligations: number;
}
export interface CockpitSnapshot {
  contract: string; asOf: string; from: string; through: string; timeZone: string; productId: string | null;
  dateBasis: string; totalCases: number;
  kpis: { openCases: number; pendingMedical: number; pendingRegulatory: number; openObligations: number; upcomingObligations: number; overdueObligations: number };
  riskGroupsAvailable: boolean; riskBoundary: string; riskGroups: WorkspaceCount[]; reviewStates: WorkspaceCount[];
  urgencyClasses: WorkspaceCount[]; processSteps: WorkspaceCount[]; intake: IntakePoint[];
  focus: string; offset: number; pageSize: number; matchingCases: number; cases: CockpitCase[];
}
export interface CalendarEvent {
  id: string; obligationId: string; kind: string; entityKind: 'case' | 'psur' | 'psurReview'; entityId: string;
  psurId: string | null; round: number | null; productId: string; products: string[]; reference: string; title: string;
  sourceVersion: number; sourceField: string; originalTimeZone: string | null; dueAt: string | null;
  dueDate: string | null; displayDate: string; status: string; timing: string; isOpen: boolean;
  isCurrent: boolean; primary: boolean; details: string[];
}
export interface CalendarDay { date: string; events: number; openEvents: number; overdueMarkers: number; kinds: WorkspaceCount[] }
export interface CalendarSnapshot {
  contract: string; asOf: string; month: string; timeZone: string; productId: string | null; kind: string; status: string;
  day: string | null; days: CalendarDay[]; obligations: number; openObligations: number; overduePrimaryObligations: number;
  missingPsurDates: number; totalEvents: number; matchingEvents: number; offset: number; pageSize: number; events: CalendarEvent[];
}
export interface PsurFocus { reportId: string; reviewId?: string; round?: number }
export interface Citation { evidenceId: string; hash: string; locator: string; quote: string }
export interface Claim { id: string; text: string; kind: string; citations: Citation[] }
export interface Section { key: string; title: string; text: string; claimIds: string[] }
export interface Recommendation { title: string; rationale: string; owner: string; urgency: string; limitation: string; claimIds: string[] }
export interface AssessmentContent { sections: Section[]; claims: Claim[]; recommendations: Recommendation[]; uncertainties: string[]; probability: number | null; probabilityBasis: string }
export interface Finding { code: string; severity: string; description: string; remedy: string; claimId?: string }
export interface Assessment { version: number; createdAt: string; authorId: string; mode: string; model: string; evidenceHash: string; evidenceIds: string[]; content: AssessmentContent; quality: { findings: Finding[]; summary: string } }
export interface MirFacts {
  eventDateFrom?: string | null; eventDateTo?: string | null; nextReportDate?: string | null;
  eventClassification?: string | null; deviceProblemCode?: string | null; deviceLocation?: string | null;
  preliminaryAnalysis?: string | null; initialCorrectiveAction?: string | null; furtherInvestigation?: string | null;
  udiDi?: string | null; udiEntity?: string | null; udiPi?: string | null; basicUdiEntity?: string | null;
  distribution?: string | null; rootCauses?: string | null; rootCauseConfirmed?: string | null; riskAssessmentReviewed?: string | null;
  investigationTypeCode?: string | null; investigationFindingCode?: string | null; investigationConclusionCode?: string | null;
  componentCode?: string | null; correctiveAction?: string | null; finalComments?: string | null;
  eventDateRationale?: string | null; authorityReference?: string | null; authorityReferenceRationale?: string | null;
  rationaleNoRiskReview?: string | null; riskAssessmentAdequate?: string | null; riskAssessmentResults?: string | null;
  manufacturerName?: string | null; manufacturerSrn?: string | null; manufacturerCountry?: string | null;
  contactFirstName?: string | null; contactLastName?: string | null; contactStreet?: string | null; contactCity?: string | null;
  contactPostcode?: string | null; contactPhone?: string | null; contactEmail?: string | null;
  submitterIsManufacturerContact?: boolean | null; similarIncidents?: MirSimilarIncidents | null;
  evidenceIds: string[]; rationale: string;
}
export interface MirSimilarPeriod { start: string; end: string; countryIncidents: number | null; regionalIncidents: number | null; worldwideIncidents: number | null; countryDevices: number | null; regionalDevices: number | null; worldwideDevices: number | null; missingDataReason: string }
export interface MirSimilarIncidents { variant: string; details: string; denominatorBasis: string; otherDenominator: string | null; method: string; geography: string; periods: MirSimilarPeriod[] }
export interface ReportTarget { version: string; jurisdiction: string; authority: string; simulator: string; formFamily: string; mirSupported: boolean; nationalSubmissionReady: boolean; externalRequirements: string[] }
export interface PreparedReportField { field: string; value: string | null; origin: string; source: string; rationale: string; claimIds: string[]; citations: Citation[] }
export interface ReportFieldIssue { field: string; reason: string; explanation: string; requiredInformation: string; sources: string[] }
export interface ReportPreparation { contract: string; productId: string; caseSource: VersionReference; device: Device; governance: ProductGovernance | null; target: ReportTarget; mode: string; authoringRunId: string; fields: PreparedReportField[]; problems?: ReportFieldIssue[] | null }
export interface MirArtifact { kind: string; schemaFile: string; schemaHash: string; mappingVersion: string; xmlHash: string; valid: boolean; errors: string[]; limitations: string[]; facts: MirFacts; followUpSchedule?: FollowUpSchedule | null; createdAt: string; checks?: { schemaValid: boolean; contentErrors: string[]; contentBoundary: string; nationalSubmissionReady: boolean; externalRequirements: string[]; completionIssues?: ReportFieldIssue[] | null } | null }
export interface Report { version: number; assessmentVersion: number; evidenceHash: string; kind: string; language: string; jurisdiction: string; recipient: string; formVersion: string; schemaVersion: string; narrative: string; state: string; scopeHash: string; preliminary: boolean; openQuestions: string; mir?: MirArtifact | null; obligationIds: string[]; followUpObligationIds?: string[] | null; preparation?: ReportPreparation | null; reviewReturn?: { actorId: string; role: string; at: string; reason: string; previousScopeHash: string } | null }
export interface Approval { id: string; actorId: string; actorName: string; role: string; scopeHash: string; at: string; meaning: string; rationale: string }
export interface Submission { id: string; correlationId: string; reportScopeHash: string; status: string; receiptReference: string | null; attemptedAt: string; simulated: boolean; attempts: number }
export interface AgentRun { id: string; role: string; mode: string; status: string; startedAt: string; finishedAt: string; errorCode: string | null; model: string }
export interface InfoRequest { id: string; question: string; minimalData: string; relevance: string; manufacturerBasis: string; disclosureBasis: string; dueAt: string; status: string; approvals: Approval[]; response: string | null }
export interface CaseSummary { id: string; version: number; reference: string; productId: string; title: string; stage: string; disposition: string; jurisdiction: string; seriousness: string; outcome?: CaseOutcome; firstAwarenessAt: string; systemReceivedAt: string; udi: string | null; batch: string | null; deadlines: Deadline[]; followUps?: FollowUpObligation[] | null; alerts: string[]; sourceCount: number; reportState: string | null; assessmentVersion: number | null; synthetic: boolean }
export interface SafetyCase extends CaseSummary { description: string; country: string; occurredAt: string | null; sources: { id: string; externalId: string; title: string; awarenessAt: string; receivedAt: string; hash: string; matchReason: string }[]; awareness: { id: string; kind: string; at: string; recordedAt: string; source: string; reason: string }[]; evidence: Evidence[]; assessments: Assessment[]; currentAssessment: Assessment | null; currentReport: Report | null; reports: Report[]; approvals: Approval[]; submissions: Submission[]; informationRequests: InfoRequest[]; agentRuns: AgentRun[]; signalReviews?: SignalReview[] | null }
export interface SignalDecision { action: string; origin: string; reason: string; at: string; actorId: string; actorName: string; role: string; ownerVersion: number; sourceHash: string; agentRunId: string | null; agentMode: string | null; model: string | null; promptVersion: string | null }
export interface SignalReview { sourceId: string; sourceHash: string; reviewReference: string; status: string; history: SignalDecision[] }
export interface SignalView { ownerKind: 'case' | 'source'; ownerId: string; ownerVersion: number; sourceId: string; productId: string; title: string; sourceReference: string; sourceHash: string; sourceUri: string; receivedAt: string; awarenessAt: string | null; caseId: string | null; caseReference: string | null; status: string; reviewReference: string | null; history: SignalDecision[]; content: string; boundary: string }
export interface AuditEvent { sequence: number; aggregateId: string; version: number; actorId: string; actorName: string; action: string; reason: string; at: string; hash: string; previousHash: string }
export interface Status { persistence: string; objects: string; search: string; orchestration: string; foundryConfigured: boolean; heartbeatMissing: boolean; heartbeat: { lastTick: string; openDeadlines: number } | null; warnings: string[] }
export interface Capa { id: string; version: number; caseId: string; productId: string; title: string; action: string; rationale: string; ownerId: string; effectivenessCriterion: string; dueAt: string; status: string; evidence: string | null; verifiedBy: string | null }
export interface ChangeRequest { id: string; version: number; documentId: string; documentVersion: number; productId: string; proposedChange: string; rationale: string; status: string }
export interface Trend { id: string; version: number; productId: string; status: string; interpretation: string; result: { events: number; exposure: number; ratePerThousand: number; expectedEvents: number; upperTailProbability: number; signalCandidate: boolean; limitations: string[] }; input: { region: string; start: string; end: string } }
export interface VersionReference { id: string; version: number; hash: string }
export interface SummaryAgreement {
  id: string; version: number; productId: string; reference: string; device: Device; authorityName: string;
  calendar: { id: string; version: string; timeZone: string }; recipient: string; language: string; format: string;
  agreementText: string; contentRequirements: string; frequencyDays: number; validFrom: string; validThrough: string;
  agreedAt: string; prerequisite: string; prerequisiteRationale: string; evidenceSource: VersionReference; evidence: Evidence;
  status: string; recordedBy: string; recordedAt: string; simulated: boolean; contract: string;
}
export interface SummaryContent { incidentOverview: string; investigationFindings: string; benefitRiskAndActions: string; agreementComplianceAndGaps: string }
export interface PeriodicSummaryReport {
  id: string; version: number; productId: string; reference: string; agreementSource: VersionReference; agreement: SummaryAgreement;
  start: string; end: string; cases: { source: VersionReference; case: SafetyCase; similarityRationale: string }[];
  content: SummaryContent; status: string; scopeHash: string; approvals: Approval[]; frozenJson: string | null;
  simulatedReceipt: string | null; submittedAt: string | null; simulated: boolean; contract: string;
}
export interface ProductGovernance {
  id: string; version: number; productId: string; psurReference: string; firstPeriodStart: string; firstSubmissionDate: string; scheduleAgreement: string; lifecyclePolicy: string;
  manufacturer: { name: string; srn: string; country: string };
  device: { basicUdi: string; udiDi: string; riskClass: string; certificateId: string; notifiedBodyName: string; notifiedBodyNumber: string; article18Applicable: boolean; article18Rationale: string };
  prrc: { primaryRole: string; deputyRole: string; responsibilityDocument: string; escalationReference: string; qualificationBoundary: string };
}
export interface PsurSeries {
  contract: string; reference: string; reportVersion: number; kind: string; groupRationale: string;
  governanceSource: VersionReference; governance: ProductGovernance; submissionDate: string; scheduleAgreement: string; revisionReason: string;
  previousReport: { source: VersionReference; reportVersion: number; start: string; end: string; status: string; simulatedReceipt: string | null } | null;
}
export interface PsurActionState<T> {
  currentSource: VersionReference; currentAtFreeze: T; cutoffSource: VersionReference | null; atCutoff: T | null;
  recordedAtCutoff: string | null; statusAtCutoff: string; selection: string; reason: string;
}
export interface PsurGroupProduct { productId: string; governanceSource: VersionReference; basicUdi: string; riskClass: string; notifiedBodyNumber: string }
export interface PsurDataSource { kind: string; source: VersionReference; name: string; sourceType: string; origin: string; selection: string; reason: string }
export interface PsurReviewReport { source: VersionReference; seriesReference: string; reportNumber: number; start: string; end: string; snapshotHash: string; scopeHash: string; products: string[] }
export interface PsurReviewQuestion { round: number; text: string; dueAt: string; actionRequired: boolean; scheduleBasis: string; recordedAt: string }
export interface PsurReviewAction { kind: string; source: VersionReference; productId: string; title: string; status: string }
export interface PsurReviewDraft { report: PsurReviewReport; response: string; sources: PsurDataSource[]; actions: PsurReviewAction[]; noActionRationale: string }
export interface PsurReview {
  id: string; version: number; productId: string; products: string[]; reference: string; originalReport: PsurReviewReport;
  jurisdiction: string; language: string; recipient: string; questions: PsurReviewQuestion[]; status: string;
  draft: PsurReviewDraft | null; scopeHash: string; approvals: Approval[]; alerts: string[]; simulated: boolean; contract: string;
  submissions: { round: number; question: PsurReviewQuestion; reply: PsurReviewDraft; scopeHash: string; approvals: Approval[]; simulatedReceipt: string; sentAt: string }[];
  assessments: { round: number; outcome: string; rationale: string; actionEvidence: PsurReviewAction[]; recordedBy: string; recordedAt: string }[];
}
export interface PsurGroupDefinition {
  id: string; version: number; productId: string; reference: string; members: PsurGroupProduct[]; firstPeriodStart: string;
  rationale: string; scheduleBasis: string; recordedAt: string;
}
export interface PsurRegulatoryHistory {
  contract: string; products: string[]; cutoffExclusiveUtc: string; reviews: PsurActionState<PsurReview>[]; coverage: string;
}
export interface PsurSnapshot {
  start: string; end: string; region: string; productId: string; leadProductId: string; frozenAt: string; dateBasis: string; completeness: string;
  cases: { id: string; version: number; reference: string; stage: string; disposition: string; openQuestions: string[]; statusAtCutoff?: string; knowledgeStatus?: string }[];
  series?: PsurSeries | null;
  group?: { contract: string; definitionSource: VersionReference; definition: PsurGroupDefinition;
    members: { product: PsurGroupProduct; governance: ProductGovernance; snapshot: PsurSnapshot }[]; separationRule: string; regulatoryHistory?: PsurRegulatoryHistory | null } | null;
  regulatoryHistory?: PsurRegulatoryHistory | null;
  documents: Evidence[];
  periods: { label: string; start: string; end: string; regions: { region: string; dataStatus: string; events: number | null; exposure: number | null; unit: string | null; ratePerThousand: number | null; exposureId: string | null; exposureVersion: number | null; numeratorSources?: VersionReference[] | null }[] }[];
  sourceManifest?: { version: string; knowledgeCutoffExclusiveUtc: string; periodKnowledgeRule: string; stateEvidence: string;
    cases: { reference: VersionReference; cutoffVersion: VersionReference | null; statusAtCutoff: string }[];
    postCutoffCases: { period: string; source: VersionReference; awarenessAt: string; reason: string }[] } | null;
  actions?: { cutoffExclusiveUtc: string; capas: PsurActionState<Capa>[]; changes: PsurActionState<ChangeRequest>[] } | null;
  directory?: { documentsEffectiveAt: string; coverage: string; entries: PsurDataSource[] } | null;
  codingTables?: { period: string; region: string; completeness: string; explicitDeathCases: VersionReference[]; missingAnnexCodes: VersionReference[]; unverifiedHistoricalDeathCases?: VersionReference[] | null;
    rows: { annex: string; code: string; term: string; historical: boolean; status: string; cases: VersionReference[]; originalCodes: string[] }[] }[] | null;
}
export interface Psur { id: string; version: number; productId: string; snapshotHash: string; scopeHash: string; status: string; draft: string; mode: 'Replay' | 'Live' | 'Failure'; findings: Finding[]; error: string | null; approvals: Approval[]; snapshot: PsurSnapshot; notifiedBodyReceipt: string | null }
export interface SourceRun { id: string; version: number; source: string; productId: string; status: string; at: string; items: { externalId: string; title: string; url: string; publishedAt: string | null; summary: string; provenanceHash: string }[]; error: string | null; signalReviews?: SignalReview[] | null }
export interface ProductLifecycle {
  id: string; version: number; productId: string; lastPlacingOnMarketLocalDate: string | null; confirmed: boolean;
  timeZone: string; ruleVersion: string; sourceReference: string; rationale: string; confirmedBy: string; recordedAt: string; synthetic: boolean;
}
export interface RetentionRecord {
  id: string; version: number; productId: string; reference: string; policyVersion: string; receivedAt: string; recordedAt: string;
  object: { contract: string; dataClass: string; objectKey: string; hash: string };
  synthetic: boolean; legalHold: boolean; holdReason: string; status: string; attempts: number; lastAttemptAt: string | null; errorCode: string | null;
  objectIoFailures?: { step: string; exceptionType: string; hResult: string }[] | null;
  authorization: { id: string; actorId: string; scopeHash: string; at: string; rationale: string; externalCopiesReviewed: boolean; externalCopiesReview: string } | null;
  deletion: { contract: string; operationId: string; objectKey: string; hash: string; at: string; state: string; storage: string } | null;
}
export interface RetentionView {
  record: RetentionRecord;
  evaluation: { policyVersion: string; minimumRetainThroughLocalDate: string | null; expiresAt: string | null; minimumIntervalElapsed: boolean | null;
    lifecycleSource: VersionReference | null; scopeHash: string; eligibleForAuthorization: boolean; canDeleteNow: boolean;
    retainedReferences: number; blockers: string[]; calculation: string[] };
  references: { kind: string; id: string; version: number; productId: string; versionHash: string }[];
}
