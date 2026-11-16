using System.Text.Json.Serialization;
using Cohors.Domain;

namespace Cohors.Application;

public sealed record CommandContext(string Id, string Kind, string ProductId, int ExpectedVersion,
    string IdempotencyKey, string Action, string Reason, Actor Actor, object Request, DateTimeOffset At)
{
    public StoreDependency[] Dependencies { get; init; } = [];
}
public sealed record StoreDependency(string Kind, VersionReference Source);
public sealed record StoreEntry<T>(string Id, string Kind, string ProductId, int Version, T Value);
public interface IStateStore
{
    Task<T?> Get<T>(string kind, string id, CancellationToken ct = default);
    Task<T[]> List<T>(string kind, string[] products, CancellationToken ct = default);
    Task<T[]> History<T>(string kind, string[] products, CancellationToken ct = default);
    Task<PortfolioState> Capture(string[] products, CancellationToken ct = default);
    Task<T?> Receipt<T>(CommandContext command, CancellationToken ct = default);
    Task<T> Execute<T>(CommandContext command, Func<T?, T> change, CancellationToken ct = default);
    Task<Psur> FreezePsur(CommandContext command, PsurCaptureScope scope, Func<PortfolioState, Psur> create, CancellationToken ct = default);
    Task<AuditEvent[]> Audit(string id, CancellationToken ct = default);
    Task<OutboxEvent[]> PendingEvents(int limit, CancellationToken ct = default);
    Task<OutboxEvent?> ClaimEvent(string eventId, string owner, CancellationToken ct = default);
    Task CompleteEvent(string eventId, string consumer, string owner, string? error, CancellationToken ct = default);
    Task<OutboxIssue[]> OutboxIssues(string[] products, CancellationToken ct = default);
    Task<bool> HasReceipt(string eventId, string consumer, CancellationToken ct = default);
}

public interface IObjectStore
{
    Task<string> Put(string content, CancellationToken ct = default);
    Task<string> Read(string hash, CancellationToken ct = default);
}

public interface IRawQuarantineStore
{
    Task<RetentionObjectBinding> CreateSyntheticRaw(string content, CancellationToken ct = default);
    Task<string> ReadRaw(RetentionObjectBinding value, CancellationToken ct = default);
    Task<ObjectDeletionReceipt> DeleteRaw(RetentionRecord value, DateTimeOffset at, CancellationToken ct = default);
}

public sealed class ObjectStoreIoException : IOException
{
    public ObjectIoFailure[] Failures { get; }

    public ObjectStoreIoException(RawDeletionIoStep step, Exception cause)
        : base($"Controlled raw-object I/O failed at {step}.", cause) => Failures = [Describe(step, cause)];

    public ObjectStoreIoException(Exception previous, ObjectStoreIoException cleanup)
        : base("Controlled raw-object I/O and temporary-journal cleanup both failed.", new AggregateException(previous, cleanup))
    {
        Failures = previous is ObjectStoreIoException original
            ? [.. original.Failures, .. cleanup.Failures] : cleanup.Failures;
    }

    public static ObjectIoFailure Describe(RawDeletionIoStep step, Exception cause) =>
        new(step, cause.GetType().Name, $"0x{cause.HResult:X8}");
}

public interface IRetentionStateStore
{
    Task<ObjectDependency[]> ObjectDependencies(string hash, string retentionId, CancellationToken ct = default);
    Task<RetentionRecord> ExecuteDeletion(CommandContext command, string hash,
        Func<RetentionRecord, ObjectDependency[], Task<RetentionRecord>> change, CancellationToken ct = default);
}

public sealed record CreateRawRetentionCommand(string ProductId, string Reference, DateTimeOffset SyntheticReceivedAt, string Reason);
public sealed record TrackRetentionCommand(string EvidenceId, int EvidenceVersion, string Reason);
public sealed record RecordLifecycleCommand(int ExpectedVersion, string ProductId, DateOnly? LastPlacingOnMarketLocalDate,
    bool Confirmed, string SourceReference, string Reason);
public sealed record RetentionHoldCommand(int ExpectedVersion, bool LegalHold, string Reason);
public sealed record AuthorizeDeletionCommand(int ExpectedVersion, bool ExternalCopiesReviewed, string ExternalCopiesReview, string Reason);
public sealed record RetentionOptions(bool EnableSyntheticRawDeletion);
public sealed record RetentionView(RetentionRecord Record, RetentionEvaluation Evaluation, ObjectDependency[] References);

public sealed record AgentInput(AgentRole Role, AgentMode Mode, SafetyCase? Case, PsurSnapshot? Snapshot,
    TrendRun? Trend, Evidence[] Evidence, string Instruction, int Revision, string Language = "de");
public sealed record AgentOutput(string Model, string PromptVersion, AssessmentContent? Assessment,
    string Narrative, QualityFinding[] Findings, string[] Tools,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] VigilanceFields? Vigilance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SignalTriageProposal? Triage = null);
public interface IAgentRunner
{
    Task<AgentOutput> Run(AgentInput input, CancellationToken ct = default);
}

public sealed class AgentExecutionException(string code, Exception inner) : Exception(code, inner)
{
    public string Code { get; } = code;
}

public interface IKnowledgeSearch
{
    Task<Evidence[]> Retrieve(string productId, DateTimeOffset effectiveAt, string query, Actor actor, CancellationToken ct = default);
    Task<int> Rebuild(CancellationToken ct = default);
}

public interface IPublicSourceReader
{
    Task<PublicSourceItem[]> Read(string source, string productId, CancellationToken ct = default);
}

public interface IMirComposer
{
    MirArtifact Build(SafetyCase c, ReportDraft report, Device device, CalendarProfile calendar, MirFacts facts, DateTimeOffset now);
    string[] Validate(string xml, string kind);
}

public sealed record PsurCaptureScope(string[] Products, string? GroupId, string LeadingBasicUdi);
public sealed record PortfolioState(SafetyCase[] Cases, Evidence[] Evidence, Exposure[] Exposures,
    TrendRun[] Trends, Capa[] Capas, ChangeRequest[] Changes, AggregateRevision[]? History = null,
    ProductGovernance[]? Governance = null, Psur[]? Psurs = null, PublicSourceRun[]? SourceRuns = null,
    PsurGroupDefinition[]? Groups = null, PsurLeadingPeriod[]? LeadingPeriods = null, PsurReview[]? RegulatoryReviews = null);

public sealed record ReadPublicSourceCommand(string ProductId);
public sealed record ApprovePublicSourceCommand(int ExpectedVersion, string ExternalId, string Reason);
public sealed record SignalDecisionCommand(int ExpectedVersion, string SourceId, string Reason);
public sealed record SignalView(string OwnerKind, string OwnerId, int OwnerVersion, string SourceId,
    string ProductId, string Title, string SourceReference, string SourceHash, string SourceUri,
    DateTimeOffset ReceivedAt, DateTimeOffset? AwarenessAt, string? CaseId, string? CaseReference,
    string Status, string? ReviewReference, SignalDecision[] History, string Content, string Boundary);
public sealed record RegisterComplaint(string IncidentKey, string SourceId, string Title, string Description,
    string ProductId, string? Udi, string? Batch, string Country, string Jurisdiction,
    Seriousness Seriousness, DateTimeOffset? OccurredAt, DateTimeOffset AwarenessAt,
    string Scenario, bool Synthetic);
public sealed record VersionCommand(int ExpectedVersion, string Reason);
public sealed record AssessmentCommand(int ExpectedVersion, AgentMode Mode, string Reason);
public sealed record ReviseAssessmentCommand(int ExpectedVersion, AssessmentContent Content, string Reason);
public sealed record ManualAssessmentCommand(int ExpectedVersion, string Facts, string Analysis, string Conclusion, string Reason);
public sealed record PrepareReportCommand(int ExpectedVersion, string Kind, string Jurisdiction,
    string Language, bool Preliminary, string OpenQuestions, string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool CompleteCurrent = false);
public sealed record ApprovalCommand(int ExpectedVersion, string Reason, string? FindingDisposition);
public sealed record DispositionCommand(int ExpectedVersion, Disposition Disposition, string Reason,
    ExpectedSideEffectException? ExpectedSideEffect = null);
public sealed record SubmissionCommand(int ExpectedVersion, string Scenario, string Reason);
public sealed record AwarenessCommand(int ExpectedVersion, DateTimeOffset At, string Source,
    string Reason, bool NewReportabilityInformation);
public sealed record LinkEvidenceCommand(int ExpectedVersion, string EvidenceId, string Reason, int? EvidenceVersion = null);
public sealed record EvidenceDocument(Evidence Evidence, string Content, bool HashVerified);
public sealed record ReviseEvidenceCommand(int ExpectedVersion, string ChangeId, string Title, string Content,
    DateTimeOffset ValidFrom, string Reason);
public sealed record RecordSpecimenPlanCommand(int ExpectedVersion, string Reference, string SpecimenReference,
    DateTimeOffset ReceivedAt, string OriginDescription, string ConditionBefore, string CustodyReference,
    string PreservationMeasures, string TraceabilityAssessment, string Method, bool? MayCompromiseCauseEvaluation,
    string ImpactRationale, string AuthorityNoticeText, string Language, string Reason);
public sealed record SimulateSpecimenCommand(int ExpectedVersion, string Findings, string RemainingState,
    string ConsequencesAndGaps, string Reason);
public sealed record RequestInformationCommand(int ExpectedVersion, string Question, string MinimalData,
    string Relevance, string RecipientRole, string ManufacturerBasis, string DisclosureBasis, DateTimeOffset DueAt);
public sealed record RespondInformationCommand(int ExpectedVersion, string RequestId, string Response, string Reason);
public sealed record OpenCapaCommand(string CaseId, string ProductId, string Title, string Action,
    string Rationale, string OwnerId, string EffectivenessCriterion, DateTimeOffset DueAt);
public sealed record VerifyCapaCommand(int ExpectedVersion, string Evidence, bool Effective, string Reason);
public sealed record AmendCapaCommand(int ExpectedVersion, string Action, string EffectivenessCriterion,
    DateTimeOffset DueAt, string Reason);
public sealed record OpenChangeCommand(string CaseId, string ProductId, string DocumentId,
    int DocumentVersion, string ProposedChange, string Rationale);
public sealed record ChangeDecisionCommand(int ExpectedVersion, string Reason, string? ImplementationEvidence);
public sealed record RunTrendCommand(string ProductId, string Region, DateOnly Start, DateOnly End, string ExposureId,
    AgentMode Mode = AgentMode.Replay);
public sealed record FreezePsurCommand(string ProductId, string LeadProductId, DateOnly Start, DateOnly End, string Region,
    AgentMode Mode = AgentMode.Replay,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviousReportId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PreviousReportVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateOnly? SubmissionDate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ScheduleAgreement = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RevisionReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GroupId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? GroupVersion = null);
public sealed record RecordPsurGroupCommand(int ExpectedVersion, string Reference, string LeadProductId,
    string[] MemberProductIds, DateOnly FirstPeriodStart, string Rationale, string ScheduleBasis);
public sealed record RevisePsurCommand(int ExpectedVersion, string Draft, string Reason);
public sealed record OpenPsurReviewCommand(string PsurId, int PsurVersion, string Reference, string Question,
    DateTimeOffset DueAt, bool ActionRequired, string ScheduleBasis, string Language, string Reason);
public sealed record PsurReviewSelection(string Kind, string Id, int Version);
public sealed record DraftPsurReplyCommand(int ExpectedVersion, string PsurId, int PsurVersion, string Response,
    PsurReviewSelection[] Sources, PsurReviewSelection[] Actions, string NoActionRationale, string Reason);
public sealed record AssessPsurReplyCommand(int ExpectedVersion, string Outcome, string Reason,
    string? NextQuestion = null, DateTimeOffset? NextDueAt = null, bool NextActionRequired = false,
    string? NextScheduleBasis = null);
public sealed record MirCommand(int ExpectedVersion, MirFacts Facts, string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ResolveSourceProblems = false);
public sealed record RecordSummaryAgreementCommand(int ExpectedVersion, string ProductId, string Reference,
    string AuthorityName, string Jurisdiction, string Language, string Format, string AgreementText,
    string ContentRequirements, int FrequencyDays, DateOnly ValidFrom, DateOnly ValidThrough,
    DateTimeOffset AgreedAt, string Prerequisite, string PrerequisiteRationale,
    string EvidenceId, int EvidenceVersion, string Reason);
public sealed record SummaryCaseSelection(string CaseId, int Version, string SimilarityRationale);
public sealed record DraftSummaryCommand(int ExpectedVersion, string AgreementId, int AgreementVersion,
    DateOnly Start, DateOnly End, SummaryCaseSelection[]? Cases, SummaryContent? Content, string Reason);
public sealed record ReclassifyCommand(int ExpectedVersion, Seriousness Seriousness, string Reason);

public sealed class PolicyCatalog
{
    public required Device[] Devices { get; init; }
    public required CalendarProfile[] Calendars { get; init; }
    public required TrendPolicy Trend { get; init; }
    public required TerminologyCatalog Terminology { get; init; }
    public Device Device(string id) => Devices.SingleOrDefault(d => d.Id == id)
        ?? throw new DomainException("device_unknown", "Produktfamilie ist nicht im synthetischen Pilotportfolio.");
    public CalendarProfile Calendar(string jurisdiction) => Calendars.SingleOrDefault(c => c.Id == jurisdiction)
        ?? throw new DomainException("jurisdiction_unknown", "Jurisdiktion ist nicht freigegeben.");
    public static string Recipient(string jurisdiction) => ReportTargets.For(jurisdiction).Simulator;
}

public sealed record RecordCodingCommand(int ExpectedVersion, string ReleaseId, string[] Codes,
    string[] EvidenceIds, string Rationale, DateOnly? HistoricalAsOf = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HistoricalCodingProof[]? HistoricalProofs = null);
