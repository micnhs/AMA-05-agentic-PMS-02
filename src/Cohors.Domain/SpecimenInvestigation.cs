namespace Cohors.Domain;

public sealed record SpecimenCaseContext(string CaseId, string ProductId, string? Udi, string? Batch,
    string Country, string Jurisdiction, Seriousness Seriousness, DateTimeOffset FirstAwarenessAt,
    string EvidenceHash, string? AssessmentHash);
public sealed record SpecimenPreservation(string SpecimenReference, DateTimeOffset ReceivedAt,
    string OriginDescription, string ConditionBefore, string CustodyReference, string PreservationMeasures,
    string TraceabilityAssessment, Evidence Evidence);
public sealed record SpecimenPriorNotice(string Receipt, string Recipient, string ScopeHash,
    DateTimeOffset At, string ActorId, int CaseVersion, string Meaning, bool Simulated);
public sealed record SpecimenOutcome(string Findings, string RemainingState, string ConsequencesAndGaps,
    Evidence Evidence, DateTimeOffset At, string ActorId, int CaseVersion, bool Simulated);
public sealed record SpecimenCancellation(string Reason, string ActorId, DateTimeOffset At);
public sealed record SpecimenInvestigation(string Id, int Revision, string Reference,
    VersionReference CaseSource, SpecimenCaseContext CaseContext, Device Device,
    SpecimenPreservation Preservation, Evidence[] Sources, string Method, bool MayCompromiseCauseEvaluation,
    string ImpactRationale, string AuthorityNoticeText, string AuthorityName, string Recipient, string Language,
    string CreatedBy, DateTimeOffset CreatedAt, string ScopeHash, Approval[] Approvals, string Status,
    string? FrozenJson, SpecimenPriorNotice? PriorNotice, SpecimenOutcome? Outcome,
    SpecimenCancellation? Cancellation)
{
    public string Contract { get; init; } = SpecimenInvestigationRules.Contract;
}
public sealed record SpecimenInvestigationPackage(string Contract, string Reference, int Revision,
    VersionReference CaseSource, SpecimenCaseContext CaseContext, Device Device,
    SpecimenPreservation Preservation, Evidence[] Sources, string Method, bool MayCompromiseCauseEvaluation,
    string ImpactRationale, string AuthorityNoticeText, string AuthorityName, string Recipient, string Language,
    string CreatedBy, DateTimeOffset CreatedAt, string Boundary);
public sealed record SpecimenInvestigationExport(string Contract, string Boundary,
    VersionReference SnapshotSource, SpecimenInvestigation Investigation);

public static class SpecimenInvestigationRules
{
    public const string Contract = "cohors-specimen-investigation-1";
    public const string Boundary = "SYNTHETISCHE ASSERVATEN-/UNTERSUCHUNGSDOKUMENTATION. Keine reale Materialpruefung oder Behoerdenkommunikation. Vorherige simulierte Unterrichtung ist keine behoerdliche Genehmigung; interne Prueffreigaben sind keine endgueltige Sicherheitsentscheidung. Kenntnis und Einzelmeldefristen bleiben unveraendert.";
    public const string ApprovalMeaning = "Interne Prueffreigabe; keine behoerdliche Genehmigung und keine endgueltige Sicherheitsentscheidung.";
    public const string NoticeMeaning = "Nur simulierte vorherige Unterrichtung; keine echte Zustellung, keine Behoerdenzustimmung oder Erledigung einer Meldepflicht.";

    public static string Recipient(string jurisdiction) => "cohors:specimen-simulator:" + jurisdiction;
    public static SpecimenCaseContext Context(SafetyCase c) => new(c.Id, c.ProductId, c.Udi, c.Batch,
        c.Country, c.Jurisdiction, c.Seriousness, c.FirstAwarenessAt, Canonical.Hash(c.Evidence),
        c.CurrentAssessment is null ? null : Canonical.Hash(c.CurrentAssessment));
    public static SpecimenInvestigationPackage Package(SpecimenInvestigation value) => new(Contract,
        value.Reference, value.Revision, value.CaseSource, value.CaseContext, value.Device, value.Preservation,
        value.Sources, value.Method, value.MayCompromiseCauseEvaluation, value.ImpactRationale,
        value.AuthorityNoticeText, value.AuthorityName, value.Recipient, value.Language,
        value.CreatedBy, value.CreatedAt, Boundary);
    public static string Scope(SpecimenInvestigation value) => Canonical.Hash(Package(value));

    public static void RequireIntegrity(SpecimenInvestigation value)
    {
        if (value.CaseSource is null || value.CaseContext is null || value.Device is null ||
            value.Preservation is null || value.Preservation.Evidence is null ||
            value.Sources is not { Length: > 0 } || value.Approvals is null)
            throw new DomainException("specimen_scope", "Gebundene Fall-, Asservaten-, Quellen- und Freigabemetadaten fehlen.", 409);
        Guard.Against(value.Contract != Contract || value.Revision < 1 ||
            value.CaseSource.Id != value.CaseContext.CaseId || value.CaseSource.Version < 1 ||
            value.Device.Id != value.CaseContext.ProductId || value.Recipient != Recipient(value.CaseContext.Jurisdiction) ||
            value.Language is not ("de" or "en") ||
            value.Status is not ("Draft" or "MedicalApproved" or "Approved" or "AuthorityInformed" or "SimulatedExamined" or "Cancelled"),
            "specimen_scope", "Asservaten-, Produkt-, Sprach- und Simulatorscope sind nicht konsistent.", 409);
        RequireReference(value.Reference, "Untersuchungsreferenz");
        RequireReference(value.Preservation.SpecimenReference, "Asservatenreferenz");
        RequireReference(value.Preservation.CustodyReference, "Synthetische Verwahrungs-/Verantwortungsreferenz");
        Guard.Text(value.Preservation.OriginDescription, "Herkunft, Uebergabe und bekannte Verwahrungsluecken", 80, 4000);
        Guard.Text(value.Preservation.ConditionBefore, "Dokumentierter Ausgangszustand", 80, 4000);
        Guard.Text(value.Preservation.PreservationMeasures, "Konkrete Beweissicherung vor Untersuchung", 80, 4000);
        Guard.Text(value.Preservation.TraceabilityAssessment, "Land-/Chargen-/UDI-Zuordnung und verbleibende Luecken", 80, 4000);
        Guard.Text(value.Method, "Geplante synthetische Untersuchung", 80, 4000);
        Guard.Text(value.ImpactRationale, "Moegliche Beeintraechtigung der Ursachenbewertung", 80, 4000);
        Guard.Text(value.AuthorityNoticeText, "Minimale vorherige Unterrichtung", value.MayCompromiseCauseEvaluation ? 80 : 0, 4000);
        Guard.Text(value.AuthorityName, "Gebundene fachlich zustaendige Behoerde", 5, 200);
        Guard.Text(value.CreatedBy, "Menschlicher Auftraggeber", 1, 200);
        Guard.Against(value.Preservation.ReceivedAt == default || value.Preservation.ReceivedAt > value.CreatedAt ||
            value.CaseSource.Hash is not { Length: 64 } || value.CaseSource.Hash.Any(c => !char.IsAsciiHexDigit(c)) ||
            value.CaseContext.EvidenceHash != Canonical.Hash(value.Sources) ||
            value.Sources.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != value.Sources.Length ||
            value.Sources.Any(e => e.ProductId != value.Device.Id) ||
            !value.Preservation.Evidence.Synthetic || !value.Sources.Contains(value.Preservation.Evidence) ||
            value.Preservation.Evidence.Kind != "SpecimenPreservation" ||
            value.Preservation.Evidence.RecordedAt != value.CreatedAt ||
            value.Preservation.Evidence.Content != PreservationText(value.Reference, value.Revision, value.CaseContext,
                value.Preservation.SpecimenReference, value.Preservation.ReceivedAt, value.Preservation.OriginDescription,
                value.Preservation.ConditionBefore, value.Preservation.CustodyReference, value.Preservation.PreservationMeasures,
                value.Preservation.TraceabilityAssessment, value.CreatedBy, value.CreatedAt) ||
            value.ScopeHash != Scope(value),
            "specimen_preservation", "Ausgangszustand, Originalevidenz und Untersuchungsbindung stimmen nicht.", 409);
        foreach (var evidence in value.Sources) EvidenceRules.RequireIntegrity(evidence);
        Guard.Against(value.Approvals.Any(a => a.ScopeHash != value.ScopeHash || a.Meaning != ApprovalMeaning ||
                string.IsNullOrWhiteSpace(a.ActorId) || string.IsNullOrWhiteSpace(a.Rationale)) ||
            value.Approvals.Select(a => a.ActorId).Distinct(StringComparer.Ordinal).Count() != value.Approvals.Length ||
            value.Approvals.Select(a => a.Role).Distinct(StringComparer.Ordinal).Count() != value.Approvals.Length ||
            value.Approvals.Any(a => a.Role is not (Roles.Medical or Roles.Regulatory) || a.At < value.CreatedAt) ||
            value.Approvals.Length > 2 || value.Status == "Draft" && value.Approvals.Length != 0 ||
            value.Status == "MedicalApproved" && value.Approvals.Length != 1 ||
            value.Approvals.Length > 0 && value.Approvals[0].Role != Roles.Medical,
            "specimen_approval", "Interne Unterschriften muessen unterschiedliche Rollen/Identitaeten und dieses Paket binden.", 409);
        if (value.Status is "Approved" or "AuthorityInformed" or "SimulatedExamined" || value.FrozenJson is not null)
            RequireFrozen(value);
        if (value.PriorNotice is { } notice)
        {
            RequireFrozen(value);
            Guard.Against(!notice.Simulated || notice.ScopeHash != value.ScopeHash || notice.Recipient != value.Recipient ||
                notice.Receipt != "DEMO-SPECIMEN-NOTICE-" + value.ScopeHash[..20] || notice.Meaning != NoticeMeaning ||
                string.IsNullOrWhiteSpace(notice.ActorId) || !value.MayCompromiseCauseEvaluation ||
                value.Status is not ("AuthorityInformed" or "SimulatedExamined" or "Cancelled") ||
                notice.At < value.CreatedAt || value.Approvals.Any(a => a.At > notice.At) ||
                notice.CaseVersion <= value.CaseSource.Version,
                "specimen_notice_integrity", "Vorabunterrichtung bindet nicht die gespeicherte interne Prueffassung.", 409);
        }
        if (value.Status == "AuthorityInformed")
            Guard.Against(value.PriorNotice is null, "specimen_notice_integrity", "Simulierter Vorabbeleg fehlt.", 409);
        if (value.Outcome is { } outcome)
        {
            EvidenceRules.RequireIntegrity(outcome.Evidence);
            Guard.Text(outcome.Findings, "Synthetische technische Befunde und Gegenhypothesen", 80, 5000);
            Guard.Text(outcome.RemainingState, "Verbleibender Zustand und Beweissicherung", 80, 4000);
            Guard.Text(outcome.ConsequencesAndGaps, "Auswirkungen auf Ursachenbewertung und verbleibende Luecken", 80, 5000);
            Guard.Against(value.Status != "SimulatedExamined" || !outcome.Simulated || !outcome.Evidence.Synthetic ||
                string.IsNullOrWhiteSpace(outcome.ActorId) ||
                outcome.Evidence.ProductId != value.Device.Id || outcome.Evidence.Kind != "SpecimenFinding" ||
                outcome.Evidence.RecordedAt != outcome.At ||
                outcome.Evidence.Content != ResultText(value, outcome.Findings, outcome.RemainingState,
                    outcome.ConsequencesAndGaps, outcome.At, outcome.ActorId) || outcome.At < value.CreatedAt ||
                outcome.CaseVersion <= value.CaseSource.Version || value.Approvals.Any(a => a.At > outcome.At) ||
                value.MayCompromiseCauseEvaluation && value.PriorNotice is null ||
                value.PriorNotice is { } prior && (prior.At > outcome.At || prior.CaseVersion >= outcome.CaseVersion),
                "specimen_sequence", "Keine belegte Reihenfolge von Beweissicherung, internen Freigaben, noetiger Vorabunterrichtung und Simulation.", 409);
        }
        Guard.Against(value.Status == "SimulatedExamined" && value.Outcome is null ||
            value.Status == "Cancelled" && value.Cancellation is null ||
            value.Cancellation is { } cancellation && (value.Status != "Cancelled" || value.Outcome is not null ||
                string.IsNullOrWhiteSpace(cancellation.ActorId) || string.IsNullOrWhiteSpace(cancellation.Reason) ||
                cancellation.At < value.CreatedAt || value.Approvals.Any(a => a.At > cancellation.At) ||
                value.PriorNotice is { } priorNotice && priorNotice.At > cancellation.At),
            "specimen_completion", "Untersuchungsabschluss oder begruendeter Verzicht ist nicht belegt.", 409);
    }

    public static void RequireFrozen(SpecimenInvestigation value)
    {
        Guard.Against(value.Approvals.Length != 2 ||
            !value.Approvals.Any(a => a.Role == Roles.Medical && a.ScopeHash == value.ScopeHash) ||
            !value.Approvals.Any(a => a.Role == Roles.Regulatory && a.ScopeHash == value.ScopeHash) ||
            value.Approvals[0].ActorId == value.Approvals[1].ActorId ||
            value.Approvals[1].At < value.Approvals[0].At ||
            value.FrozenJson != Canonical.Serialize(Package(value)),
            "specimen_approval", "Genau zwei getrennte interne Freigaben und unveraendertes Originalpaket erforderlich.", 409);
    }

    public static void RequireReadyToSimulate(SpecimenInvestigation value)
    {
        RequireIntegrity(value);
        Guard.Against(value.Status is not ("Approved" or "AuthorityInformed") || value.Outcome is not null,
            "specimen_immutable", "Nur offene intern freigegebene Fassungen duerfen simuliert werden.", 409);
        RequireFrozen(value);
        Guard.Against(value.MayCompromiseCauseEvaluation && value.PriorNotice is null, "specimen_prior_notice_required",
            "Vor einer potenziell beeintraechtigenden Pruefung muss die getrennte vorherige Behoerdenunterrichtung simuliert dokumentiert sein.", 409);
    }

    public static string PreservationText(string reference, int revision, SpecimenCaseContext context,
        string specimenReference, DateTimeOffset receivedAt, string originDescription, string conditionBefore,
        string custodyReference, string preservationMeasures, string traceabilityAssessment, string actorId,
        DateTimeOffset at) => Canonical.Serialize(new
        {
            Contract = "cohors-specimen-preservation-1", context.CaseId, context.ProductId, context.Country,
            context.Batch, context.Udi, Reference = reference, Revision = revision, SpecimenReference = specimenReference,
            ReceivedAt = receivedAt, OriginDescription = originDescription, ConditionBefore = conditionBefore,
            CustodyReference = custodyReference, PreservationMeasures = preservationMeasures,
            TraceabilityAssessment = traceabilityAssessment, RecordedBy = actorId, RecordedAt = at, Boundary
        });

    public static string ResultText(SpecimenInvestigation value, string findings, string remainingState,
        string consequencesAndGaps, DateTimeOffset at, string actorId) => Canonical.Serialize(new
    {
        Contract = "cohors-specimen-result-1", value.Reference, value.Revision, value.ScopeHash,
        value.CaseContext.CaseId, value.CaseContext.ProductId, value.CaseContext.Country,
        value.CaseContext.Batch, value.CaseContext.Udi, value.Preservation.SpecimenReference,
        Findings = findings, RemainingState = remainingState, ConsequencesAndGaps = consequencesAndGaps,
        At = at, ActorId = actorId, Boundary
    });

    public static void RequireCurrent(SafetyCase c, SpecimenInvestigation value)
    {
        RequireIntegrity(value);
        Guard.Against(!c.Synthetic || c.Stage == CaseStage.Closed || value.CaseSource.Id != c.Id ||
            value.CaseSource.Version >= c.Version || value.CaseContext != Context(c),
            "specimen_stale", "Fall-/Evidenz-/Assessmentgrundlage geaendert oder Fall geschlossen; Untersuchungsplan neu binden und intern freigeben.", 409);
    }

    public static void RequireReference(string value, string field)
    {
        Guard.Text(value, field, 8, 120);
        Guard.Against(!value.StartsWith("DEMO-", StringComparison.Ordinal) || value != value.Trim(),
            "specimen_reference", "Explizite synthetische DEMO-Referenz ohne Rand-Leerzeichen erforderlich.");
    }
}
