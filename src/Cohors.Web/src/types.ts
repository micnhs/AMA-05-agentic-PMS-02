export interface Actor { id: string; name: string; roles: string[]; products: string[]; isHuman: boolean }
export interface Session { authenticated: boolean; actor: Actor | null; csrfToken: string; mode: string; entra: { tenantId: string; clientId: string; scope: string } }
export interface Device { id: string; name: string; kind: string; model: string; basicUdi: string }
export interface Deadline { id: string; jurisdiction: string; legalDue: string; safetyDue: string; triggerAt: string; ruleVersion: string; calendarVersion: string; timeZone: string; calculation: string[]; status: string; immediateAction: boolean }
export interface FollowUpSchedule { reportDate: string; dueAt: string; safetyDue: string; timeZone: string; calendarVersion: string; ruleVersion: string }
export interface FollowUpObligation { id: string; jurisdiction: string; sourceReportHash: string; sourceReportVersion: number; sourceReceiptReference: string; schedule: FollowUpSchedule; status: string; createdAt: string; completedByReportHash: string | null; completedAt: string | null }
export interface Evidence { id: string; version: number; title: string; kind: string; content: string; hash: string; locator: string; origin: string; approved: boolean; synthetic: boolean; recordedAt: string; productId: string; validFrom: string }
export interface Citation { evidenceId: string; hash: string; locator: string; quote: string }
export interface Claim { id: string; text: string; kind: string; citations: Citation[] }
export interface Section { key: string; title: string; text: string; claimIds: string[] }
export interface Recommendation { title: string; rationale: string; owner: string; urgency: string; limitation: string; claimIds: string[] }
export interface AssessmentContent { sections: Section[]; claims: Claim[]; recommendations: Recommendation[]; uncertainties: string[]; probability: number | null; probabilityBasis: string }
export interface Finding { code: string; severity: string; description: string; remedy: string; claimId?: string }
export interface Assessment { version: number; createdAt: string; authorId: string; mode: string; model: string; evidenceHash: string; content: AssessmentContent; quality: { findings: Finding[]; summary: string } }
export interface MirFacts {
  eventDateFrom?: string | null; eventDateTo?: string | null; nextReportDate?: string | null;
  eventClassification?: string | null; deviceProblemCode?: string | null; deviceLocation?: string | null;
  preliminaryAnalysis?: string | null; initialCorrectiveAction?: string | null; furtherInvestigation?: string | null;
  udiDi?: string | null; udiEntity?: string | null; udiPi?: string | null; basicUdiEntity?: string | null;
  distribution?: string | null; rootCauses?: string | null; rootCauseConfirmed?: string | null; riskAssessmentReviewed?: string | null;
  investigationTypeCode?: string | null; investigationFindingCode?: string | null; investigationConclusionCode?: string | null;
  componentCode?: string | null; correctiveAction?: string | null; finalComments?: string | null;
  evidenceIds: string[]; rationale: string;
}
export interface MirArtifact { kind: string; schemaFile: string; schemaHash: string; mappingVersion: string; xmlHash: string; valid: boolean; errors: string[]; limitations: string[]; facts: MirFacts; followUpSchedule?: FollowUpSchedule | null; createdAt: string }
export interface Report { version: number; kind: string; language: string; jurisdiction: string; recipient: string; formVersion: string; schemaVersion: string; narrative: string; state: string; scopeHash: string; preliminary: boolean; openQuestions: string; mir?: MirArtifact | null; obligationIds: string[]; followUpObligationIds?: string[] | null }
export interface Approval { id: string; actorId: string; actorName: string; role: string; scopeHash: string; at: string; meaning: string; rationale: string }
export interface Submission { id: string; correlationId: string; reportScopeHash: string; status: string; receiptReference: string | null; attemptedAt: string; simulated: boolean; attempts: number }
export interface AgentRun { id: string; role: string; mode: string; status: string; startedAt: string; finishedAt: string; errorCode: string | null; model: string }
export interface InfoRequest { id: string; question: string; minimalData: string; relevance: string; manufacturerBasis: string; disclosureBasis: string; dueAt: string; status: string; approvals: Approval[]; response: string | null }
export interface CaseSummary { id: string; version: number; reference: string; productId: string; title: string; stage: string; disposition: string; jurisdiction: string; seriousness: string; firstAwarenessAt: string; systemReceivedAt: string; udi: string | null; batch: string | null; deadlines: Deadline[]; followUps?: FollowUpObligation[] | null; alerts: string[]; sourceCount: number; reportState: string | null; assessmentVersion: number | null; synthetic: boolean }
export interface SafetyCase extends CaseSummary { description: string; country: string; occurredAt: string | null; sources: { id: string; externalId: string; title: string; awarenessAt: string; receivedAt: string; hash: string; matchReason: string }[]; awareness: { id: string; kind: string; at: string; recordedAt: string; source: string; reason: string }[]; evidence: Evidence[]; assessments: Assessment[]; currentAssessment: Assessment | null; currentReport: Report | null; reports: Report[]; approvals: Approval[]; submissions: Submission[]; informationRequests: InfoRequest[]; agentRuns: AgentRun[] }
export interface AuditEvent { sequence: number; aggregateId: string; version: number; actorId: string; actorName: string; action: string; reason: string; at: string; hash: string; previousHash: string }
export interface Status { persistence: string; objects: string; search: string; orchestration: string; foundryConfigured: boolean; heartbeatMissing: boolean; heartbeat: { lastTick: string; openDeadlines: number } | null; warnings: string[] }
export interface Capa { id: string; version: number; caseId: string; productId: string; title: string; action: string; rationale: string; ownerId: string; effectivenessCriterion: string; dueAt: string; status: string; evidence: string | null; verifiedBy: string | null }
export interface ChangeRequest { id: string; version: number; documentId: string; documentVersion: number; productId: string; proposedChange: string; rationale: string; status: string }
export interface Trend { id: string; version: number; productId: string; status: string; interpretation: string; result: { events: number; exposure: number; ratePerThousand: number; expectedEvents: number; upperTailProbability: number; signalCandidate: boolean; limitations: string[] }; input: { region: string; start: string; end: string } }
export interface Psur { id: string; version: number; productId: string; snapshotHash: string; scopeHash: string; status: string; draft: string; approvals: Approval[]; snapshot: { start: string; end: string; region: string; cases: { reference: string; stage: string; openQuestions: string[] }[] }; notifiedBodyReceipt: string | null }
export interface SourceRun { id: string; version: number; source: string; productId: string; status: string; at: string; items: { externalId: string; title: string; url: string; publishedAt: string | null; summary: string; provenanceHash: string }[]; error: string | null }
