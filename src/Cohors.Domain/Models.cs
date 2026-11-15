using System.Text.Json.Serialization;

namespace Cohors.Domain;

public enum CaseStage { Intake, Assessing, AwaitingInformation, AwaitingMedicalReview, Investigating, Monitoring, Closed }
public enum Disposition { Undetermined, Reportable, NonReportable, Monitor, Investigate }
public enum Seriousness { PublicHealthThreat, DeathOrSeriousDeterioration, OtherSerious }
public enum ReportState { Draft, MedicalApproved, Frozen, TransmissionUnknown, SimulatedAccepted, Rejected, Superseded }
public enum AgentMode { Replay, Live, Failure }
public enum AgentRole { Ingestion, TrendSignal, CausalityRisk, EvidenceAssurance, VigilanceAuthoring, PsurAuthoring }

public sealed record Device(string Id, string Name, string Kind, string BasicUdi, string IntendedPurpose,
    string Model, string[] Markets);

public sealed record SourceReference(string Id, string ExternalId, string Kind, string Title,
    string Uri, string Hash, DateTimeOffset ReceivedAt, DateTimeOffset AwarenessAt, string MatchReason);

public sealed record AwarenessEvent(string Id, string Kind, DateTimeOffset At, DateTimeOffset RecordedAt,
    string Source, string Reason, string ActorId);

public sealed record Evidence(string Id, int Version, string ProductId, string Title, string Kind,
    string Content, string Hash, string Origin, string Locator, string License, string AccessLabel,
    DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, DateTimeOffset RecordedAt,
    bool Approved, bool Synthetic, string ObjectKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ChangeId = null);

public sealed record Citation(string EvidenceId, string Hash, string Locator, string Quote);
public sealed record Claim(string Id, string Text, string Kind, Citation[] Citations);
public sealed record AnalysisSection(string Key, string Title, string Text, string[] ClaimIds);
public sealed record Recommendation(string Title, string Rationale, string Owner, string Urgency,
    string Limitation, string[] ClaimIds);
public sealed record AssessmentContent(AnalysisSection[] Sections, Claim[] Claims,
    Recommendation[] Recommendations, string[] Uncertainties, decimal? Probability, string ProbabilityBasis);
public sealed record QualityFinding(string Code, string Severity, string Description, string Remedy, string? ClaimId);
public sealed record QualityReview(string Id, int AssessmentVersion, DateTimeOffset At, string RunId,
    QualityFinding[] Findings, string Summary);
public sealed record Assessment(int Version, DateTimeOffset CreatedAt, string AuthorId, string Mode,
    string PromptVersion, string Model, string[] EvidenceIds, string EvidenceHash,
    AssessmentContent Content, QualityReview? Quality);

public sealed record ReportDraft(int Version, int AssessmentVersion, string Kind, string Jurisdiction,
    string Recipient, string Language, string FormVersion, string SchemaVersion, string Narrative,
    bool Preliminary, string OpenQuestions, string RuleVersion, string EvidenceHash, string[] AttachmentHashes,
    ReportState State, string ScopeHash, string? FrozenJson, string[]? ObligationIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MirArtifact? Mir = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? FollowUpObligationIds = null);

public sealed record Approval(string Id, string ActorId, string ActorName, string Role, string ScopeHash,
    DateTimeOffset At, string Meaning, string Rationale, string? FindingDisposition);
public sealed record MedicalDecision(string Id, Disposition Disposition, int AssessmentVersion,
    string ScopeHash, string ActorId, DateTimeOffset At, string Rationale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExpectedSideEffectException? ExpectedSideEffect = null);
public sealed record ExpectedSideEffectException(string Effect, Citation ProductInformation,
    Citation TechnicalQuantification, Citation TrendMonitoring, string Rationale, decimal? ReferenceRate,
    string Denominator, string TrendPolicyVersion, bool IncludedInTrendReporting);
public sealed record Submission(string Id, string ReportScopeHash, int ReportVersion, string CorrelationId,
    string Status, string Scenario, DateTimeOffset AttemptedAt, DateTimeOffset? ReconciledAt,
    string? ReceiptReference, bool Simulated, int Attempts, string ReceiverStatus);

public sealed record InformationRequest(string Id, string Question, string MinimalData, string Relevance,
    string RecipientRole, string ManufacturerBasis, string DisclosureBasis, DateTimeOffset DueAt,
    string ScopeHash, Approval[] Approvals, string Status, string? Response, bool Simulated);

public sealed record AgentRun(string Id, AgentRole Role, AgentMode Mode, string Status, DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt, string Model, string PromptVersion, string InputHash, string? OutputHash,
    string[] EvidenceIds, string[] Tools, string? ErrorCode, int Revision, string? OutputJson, string? ExecutionId = null);

public sealed record AgentInvocation(string Id, int Version, string ProductId, string InputHash,
    string OwnerId, string Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    string? OutputJson, string? ErrorCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InputJson = null);

public sealed record SafetyCase(
    string Id, int Version, string Reference, string ProductId, string Title, string Description,
    string? Udi, string? Batch, string Country, string Jurisdiction, Seriousness Seriousness,
    DateTimeOffset? OccurredAt, DateTimeOffset FirstAwarenessAt, DateTimeOffset SystemReceivedAt,
    DateTimeOffset? ReportabilityAwarenessAt, string Scenario, bool Synthetic,
    CaseStage Stage, Disposition Disposition, SourceReference[] Sources, AwarenessEvent[] Awareness,
    Deadline[] Deadlines, Evidence[] Evidence, Assessment[] Assessments, ReportDraft[] Reports,
    Approval[] Approvals, MedicalDecision[] Decisions, Submission[] Submissions,
    InformationRequest[] InformationRequests, AgentRun[] AgentRuns, string[] Alerts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ClosedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FollowUpObligation[]? FollowUps = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CaseCoding? Coding = null)
{
    public Assessment? CurrentAssessment => Assessments.LastOrDefault();
    public ReportDraft? CurrentReport => Reports.LastOrDefault();
}

public sealed record Capa(string Id, int Version, string ProductId, string CaseId, string Title,
    string Rationale, string OwnerId, string Action, string EffectivenessCriterion, DateTimeOffset DueAt,
    string Status, string? Evidence, string? VerifiedBy, DateTimeOffset? VerifiedAt);

public sealed record ChangeRequest(string Id, int Version, string ProductId, string CaseId,
    string DocumentId, int DocumentVersion, string ProposedChange, string Rationale, string Status,
    string? ApprovedBy, string? ImplementationEvidence, string? RequestedBy = null,
    int SourceCaseVersion = 0, string? SourceEvidenceHash = null);

public sealed record Exposure(string Id, int Version, string ProductId, string Region,
    DateOnly Start, DateOnly End, string Unit, int Count, string Population, string Provenance, bool Approved);
public sealed record VersionReference(string Id, int Version, string Hash);
public sealed record TrendPolicy(string Version, string MethodVersion, decimal ReferenceRate, decimal Alpha, string Unit);
public sealed record TrendInput(string ProductId, string Region, DateOnly Start, DateOnly End,
    string[] CaseIds, Exposure Exposure, decimal ReferenceRate, string MethodVersion,
    VersionReference[]? CaseVersions = null, VersionReference[]? ExcludedUnknownDates = null,
    TrendPolicy? Policy = null, string DateBasis = "OccurredAt.UtcDate");
public sealed record TrendResult(int Events, int Exposure, string Unit, decimal RatePerThousand,
    decimal ExpectedEvents, double UpperTailProbability, bool SignalCandidate, string[] Limitations);
public sealed record TrendRun(string Id, int Version, string ProductId, TrendInput Input, TrendResult Result,
    DateTimeOffset CreatedAt, string Status, string Interpretation, string? ReviewerId, string? Rationale,
    string InputHash = "", AgentMode Mode = AgentMode.Replay, string? Error = null, string? ExecutionId = null);

public sealed record CaseSnapshot(string Id, int Version, string Reference, string Disposition, string Stage,
    string Country, string Description, string? AssessmentHash, bool MedicalApproved, string[] OpenQuestions,
    bool CurrentPeriodEvent, bool CarryOver, string? DecisionScopeHash, AssessmentContent? Assessment,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? KnowledgeStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StatusAtCutoff = null);
public sealed record PsurPeriod(string Label, DateOnly Start, DateOnly End, PsurRegionSummary[] Regions);
public sealed record PsurRegionSummary(string Region, string DataStatus, int? Events, int? Exposure,
    string? ExposureId, int? ExposureVersion, string? Unit, decimal? RatePerThousand,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] VersionReference[]? NumeratorSources = null);
public sealed record PsurSnapshot(string ProductId, string LeadProductId, DateOnly Start, DateOnly End,
    string Region, CaseSnapshot[] Cases, Exposure[] Exposure, TrendRun[] Trends, Capa[] Capas,
    Evidence[] Documents, DateTimeOffset FrozenAt, string[] PeriodLabels, PsurPeriod[] Periods,
    ChangeRequest[] Changes, string DateBasis, string Completeness,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PsurSourceManifest? SourceManifest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PsurCodingTable[]? CodingTables = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PsurSeries? Series = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PsurActionManifest? Actions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PsurDataDirectory? Directory = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PsurGrouping? Group = null);
public sealed record Psur(string Id, int Version, string ProductId, PsurSnapshot Snapshot,
    string SnapshotHash, string Draft, string ScopeHash, string Status, Approval[] Approvals,
    string? NotifiedBodyReceipt, bool Simulated, QualityFinding[] Findings, AgentMode Mode,
    string? Error, string? ExecutionId);

public sealed record AuditEvent(long Sequence, string AggregateId, int Version, string ActorId,
    string ActorName, string[] Roles, string Action, string Reason, DateTimeOffset At,
    string CorrelationId, string PayloadHash, string PreviousHash, string Hash);
public sealed record OutboxEvent(string Id, string AggregateId, int Version, string Type, string Payload,
    DateTimeOffset CreatedAt, int Attempts, string? Error, int RecoveryVersion = 0);
public sealed record OutboxDescriptor(string Id, string Type, int Attempts, int RecoveryVersion);
public sealed record OutboxDispatchResult(string Id, string State, string? Error);
public sealed record OutboxIssue(string Id, string ProductId, string Type, int Attempts, string? Error,
    DateTimeOffset? LeaseUntil, DateTimeOffset? NextAttemptAt, int RecoveryVersion);
public sealed record OutboxRecovery(string Id, int Version, string ProductId, string EventId,
    string ActorId, string Reason, DateTimeOffset At);

public sealed record OperationsHeartbeat(string Id, int Version, DateTimeOffset LastTick, DateTimeOffset BusinessTime,
    int OpenDeadlines, int Escalations, string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OpenFollowUps = null);

public sealed record PublicSourceItem(string ExternalId, string Title, string Url, DateTimeOffset? PublishedAt,
    string Summary, string ProvenanceHash);
public sealed record PublicSourceRun(string Id, int Version, string Source, string ProductId, string Status,
    DateTimeOffset At, PublicSourceItem[] Items, string? Error, string QueryContract, string? ExecutionId = null);
