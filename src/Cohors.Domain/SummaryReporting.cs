namespace Cohors.Domain;

public sealed record SummaryAgreement(string Id, int Version, string ProductId, string Reference,
    Device Device, string AuthorityName, CalendarProfile Calendar, string Recipient, string Language,
    string Format, string AgreementText, string ContentRequirements, int FrequencyDays,
    DateOnly ValidFrom, DateOnly ValidThrough, DateTimeOffset AgreedAt, string Prerequisite,
    string PrerequisiteRationale, VersionReference EvidenceSource, Evidence Evidence,
    string Status, string RecordedBy, DateTimeOffset RecordedAt, bool Simulated)
{
    public string Contract { get; init; } = "cohors-summary-agreement-1";
}

public sealed record SummaryCase(VersionReference Source, SafetyCase Case, string SimilarityRationale);
public sealed record SummaryContent(string IncidentOverview, string InvestigationFindings,
    string BenefitRiskAndActions, string AgreementComplianceAndGaps);
public sealed record PeriodicSummaryReport(string Id, int Version, string ProductId, string Reference,
    VersionReference AgreementSource, SummaryAgreement Agreement, DateOnly Start, DateOnly End,
    SummaryCase[] Cases, SummaryContent Content, string Status, string ScopeHash, Approval[] Approvals,
    string? FrozenJson, string? SimulatedReceipt, DateTimeOffset? SubmittedAt, bool Simulated)
{
    public string Contract { get; init; } = SummaryReportingRules.ReportContract;
}

public sealed record SummaryPackage(string Contract, string Reference, string ProductId,
    VersionReference AgreementSource, SummaryAgreement Agreement, DateOnly Start, DateOnly End,
    SummaryCase[] Cases, SummaryContent Content, string Boundary);

public static class SummaryReportingRules
{
    public const string ReportContract = "cohors-periodic-summary-1";
    public const string Boundary = "SYNTHETISCHE PERIODISCHE SAMMELMELDUNG. Kein MIR, kein PSUR, kein amtliches Format, keine echte Behoerdenannahme. Einzelkenntnis, LegalDue und SafetyDue bleiben unveraendert; die Quittung erledigt keine Einzelmeldepflicht.";

    public static string Recipient(string jurisdiction) => "cohors:summary-simulator:" + jurisdiction;
    public static VersionReference Bind(SummaryAgreement value) => new(value.Id, value.Version, Canonical.Hash(value));
    public static VersionReference Bind(SafetyCase value) => new(value.Id, value.Version, Canonical.Hash(value));
    public static VersionReference Bind(Evidence value) => new(value.Id, value.Version, Canonical.Hash(value));

    public static void RequireAgreement(SummaryAgreement agreement)
    {
        Guard.Against(agreement.Contract != "cohors-summary-agreement-1" || !agreement.Simulated ||
            agreement.ProductId != agreement.Device.Id || agreement.Recipient != Recipient(agreement.Calendar.Id) ||
            agreement.Format != ReportContract || agreement.Language is not ("de" or "en") ||
            !(agreement.Calendar.Id.StartsWith("EU-", StringComparison.Ordinal) || agreement.Calendar.Id == "NI") ||
            agreement.Status is not ("Active" or "Withdrawn"),
            "summary_agreement_scope", "Kein konsistenter synthetischer MDR-Sammelvereinbarungs-/Format-/Empfaengerscope.", 409);
        Guard.Text(agreement.Reference, "DEMO-Vereinbarungsreferenz", 8, 120);
        Guard.Against(!agreement.Reference.StartsWith("DEMO-", StringComparison.Ordinal) ||
            agreement.Reference != agreement.Reference.Trim(), "summary_reference", "Explizite DEMO-Referenz ohne Rand-Leerzeichen erforderlich.");
        Guard.Text(agreement.AuthorityName, "Simulierte zustaendige Behoerde", 5, 200);
        Guard.Text(agreement.AgreementText, "Dokumentierte simulierte Behoerdenvereinbarung", 100, 8000);
        Guard.Text(agreement.ContentRequirements, "Ausdruecklich vereinbarter Berichtsinhalt", 80, 4000);
        Guard.Text(agreement.PrerequisiteRationale, "Quellengestuetzte Begruendung der Sammelvoraussetzung", 80, 4000);
        Guard.Against(agreement.Prerequisite is not ("RootCauseIdentified" or "CommonAndWellDocumented"),
            "summary_prerequisite", "Identifizierte Ursache oder haeufige, gut dokumentierte Vorfaelle explizit belegen; keine bloss behauptete FSCA-Erledigung.");
        Guard.Against(agreement.FrequencyDays is < 1 or > 366 || agreement.ValidThrough < agreement.ValidFrom ||
            agreement.ValidFrom < agreement.Calendar.ValidFrom || agreement.ValidThrough > agreement.Calendar.ValidTo,
            "summary_frequency", "Ein bis 366 vereinbarte Kalendertage und Gueltigkeit innerhalb des gebundenen Kalenders erforderlich.");
        Guard.Against(agreement.Evidence.ProductId != agreement.ProductId || !agreement.Evidence.Synthetic ||
            !agreement.Evidence.Approved || agreement.EvidenceSource != Bind(agreement.Evidence),
            "summary_prerequisite_source", "Freigegebene synthetische Voraussetzungen-Evidenz der gleichen Produktfamilie erforderlich.", 409);
        EvidenceRules.RequireIntegrity(agreement.Evidence);
        Guard.Against(agreement.AgreedAt > agreement.RecordedAt || agreement.Evidence.ValidFrom > agreement.AgreedAt ||
            agreement.Evidence.ValidTo is { } until && until < agreement.AgreedAt,
            "summary_agreement_time", "Simulierter Vereinbarungszeitpunkt und damals gueltige Voraussetzungen-Evidenz muessen nachvollziehbar sein.");
        var agreedOffset = Math.Max(0, LocalDate(agreement.AgreedAt, agreement.Calendar).DayNumber - agreement.ValidFrom.DayNumber);
        var firstStartOffset = (agreedOffset + agreement.FrequencyDays - 1) / agreement.FrequencyDays * agreement.FrequencyDays;
        Guard.Against(agreement.ValidThrough.DayNumber - agreement.ValidFrom.DayNumber - firstStartOffset < agreement.FrequencyDays,
            "summary_reporting_window",
            $"Gueltigkeit {agreement.ValidFrom:yyyy-MM-dd} bis {agreement.ValidThrough:yyyy-MM-dd} erlaubt kein vollstaendiges {agreement.FrequencyDays}-Tage-Intervall mit noch gueltigem Folgetag fuer den Bericht. Mindestens {agreement.FrequencyDays + 1} Kalendertage ab einem verankerten Intervallbeginn nicht vor dem Vereinbarungsdatum sind erforderlich; LegalDue/SafetyDue bleiben unveraendert.");
    }

    public static void RequireActive(SummaryAgreement agreement, DateTimeOffset now)
    {
        RequireAgreement(agreement);
        var date = LocalDate(now, agreement.Calendar);
        Guard.Against(agreement.Status != "Active" || date < agreement.ValidFrom || date > agreement.ValidThrough,
            "summary_agreement_inactive", "Keine aktuell gueltige simulierte Sammelvereinbarung; Fristen nicht ersatzweise verschieben.", 409);
    }

    public static void RequirePeriod(SummaryAgreement agreement, DateOnly start, DateOnly end, DateTimeOffset now)
    {
        RequireActive(agreement, now);
        var periods = (agreement.ValidThrough.DayNumber - agreement.ValidFrom.DayNumber) / agreement.FrequencyDays;
        var lastStart = agreement.ValidFrom.AddDays((periods - 1) * agreement.FrequencyDays);
        var lastEnd = lastStart.AddDays(agreement.FrequencyDays - 1);
        Guard.Against(start < agreement.ValidFrom || end >= agreement.ValidThrough || end < start ||
            end.DayNumber - start.DayNumber + 1 != agreement.FrequencyDays ||
            (start.DayNumber - agreement.ValidFrom.DayNumber) % agreement.FrequencyDays != 0 ||
            end >= LocalDate(now, agreement.Calendar) || LocalDate(agreement.AgreedAt, agreement.Calendar) > start,
            "summary_period",
            $"Vollstaendiges vergangenes, ab Gueltigkeitsbeginn verankertes Vereinbarungsintervall erforderlich. Letzter berichtsfaehiger Zeitraum: {lastStart:yyyy-MM-dd} bis {lastEnd:yyyy-MM-dd}; Bericht fruehestens am {lastEnd.AddDays(1):yyyy-MM-dd}, noch innerhalb der Gueltigkeit bis {agreement.ValidThrough:yyyy-MM-dd}. Kein Bericht fuer ein erst am Ablauftag endendes Intervall; keine Einzelpflichtbefreiung.");
    }

    public static void RequireCase(SafetyCase value, SummaryAgreement agreement, DateOnly start, DateOnly end)
    {
        Guard.Against(!value.Synthetic || value.ProductId != agreement.ProductId || value.Jurisdiction != agreement.Calendar.Id,
            "summary_case_scope", "Nur synthetische Vorfaelle derselben Produktfamilie und vereinbarten Jurisdiktion aufnehmen.", 403);
        var knowledge = LocalDate(value.FirstAwarenessAt, agreement.Calendar);
        Guard.Against(knowledge < start || knowledge > end, "summary_case_period",
            "Die erste Organisationskenntnis liegt ausserhalb des ausdruecklich gewaehlten Sammelintervalls.");
        ReportRules.RequireNoUnknown(value, value.Jurisdiction);
        var assessment = value.CurrentAssessment ?? throw new DomainException("summary_assessment", "Begruendete aktuelle Fallbewertung fehlt.");
        Guard.Against(assessment.EvidenceHash != Canonical.Hash(value.Evidence), "assessment_stale",
            "Fallbewertung bindet nicht die aktuelle Fallevidenz.", 409);
        AssessmentRules.RequireReviewable(assessment, false, null);
        var report = value.CurrentReport;
        Guard.Against(value.Disposition != Disposition.Reportable || report is null ||
            report.State is not (ReportState.MedicalApproved or ReportState.Frozen or ReportState.SimulatedAccepted) ||
            report.ScopeHash != ReportRules.Scope(assessment, report) ||
            !value.Approvals.Any(a => a.Role == Roles.Medical && a.ScopeHash == report.ScopeHash), "summary_reportability",
            "Meldepflichtige Einordnung muss vorher im menschlichen Fallfreigabeweg dokumentiert sein.");
    }

    public static void RequireContent(SummaryContent? content)
    {
        Guard.Against(content is null, "summary_content", "Begruendeter Sammelberichtsinhalt fehlt.");
        Guard.Text(content!.IncidentOverview, "Falluebersicht und Gemeinsamkeiten", 80, 6000);
        Guard.Text(content.InvestigationFindings, "Untersuchungsstand und tragende Quellen", 80, 6000);
        Guard.Text(content.BenefitRiskAndActions, "Nutzen-Risiko-Einordnung und kontrollierte Massnahmen", 80, 6000);
        Guard.Text(content.AgreementComplianceAndGaps, "Erfuellung des vereinbarten Inhalts und offengelegte Luecken", 80, 6000);
    }

    public static SummaryPackage Package(PeriodicSummaryReport value) => new(ReportContract,
        value.Reference, value.ProductId, value.AgreementSource, value.Agreement, value.Start, value.End,
        value.Cases, value.Content, Boundary);

    public static string Scope(PeriodicSummaryReport value) => Canonical.Hash(Package(value));

    public static void RequireIntegrity(PeriodicSummaryReport value)
    {
        RequireAgreement(value.Agreement);
        RequireContent(value.Content);
        Guard.Against(value.Contract != ReportContract || !value.Simulated || value.ProductId != value.Agreement.ProductId ||
            value.AgreementSource != Bind(value.Agreement) || value.Cases.Length is < 1 or > 32 ||
            value.Start < value.Agreement.ValidFrom || value.End >= value.Agreement.ValidThrough ||
            LocalDate(value.Agreement.AgreedAt, value.Agreement.Calendar) > value.Start ||
            value.End.DayNumber - value.Start.DayNumber + 1 != value.Agreement.FrequencyDays ||
            (value.Start.DayNumber - value.Agreement.ValidFrom.DayNumber) % value.Agreement.FrequencyDays != 0 ||
            value.Cases.Select(c => c.Source.Id).Distinct(StringComparer.Ordinal).Count() != value.Cases.Length ||
            value.Cases.Any(c => c.Source != Bind(c.Case) || c.Case.ProductId != value.ProductId || !c.Case.Synthetic ||
                c.Case.Jurisdiction != value.Agreement.Calendar.Id ||
                LocalDate(c.Case.FirstAwarenessAt, value.Agreement.Calendar) < value.Start ||
                LocalDate(c.Case.FirstAwarenessAt, value.Agreement.Calendar) > value.End) ||
            value.ScopeHash != Scope(value),
            "summary_integrity", "Sammelbericht, Originalquellen und Paket-Hash sind nicht konsistent.", 409);
    }

    public static void RequireFrozen(PeriodicSummaryReport value)
    {
        RequireIntegrity(value);
        var medical = value.Approvals.SingleOrDefault(a => a.Role == Roles.Medical && a.ScopeHash == value.ScopeHash);
        var regulatory = value.Approvals.SingleOrDefault(a => a.Role == Roles.Regulatory && a.ScopeHash == value.ScopeHash);
        Guard.Against(value.Status is not ("Frozen" or "SimulatedAccepted") || value.FrozenJson != Canonical.Serialize(Package(value)) ||
            value.Approvals.Length != 2 || medical is null || regulatory is null || medical.ActorId == regulatory.ActorId,
            "summary_approval", "Unveraendertes eingefrorenes Sammelpaket und zwei getrennte gebundene Freigaben erforderlich.", 409);
    }

    private static DateOnly LocalDate(DateTimeOffset value, CalendarProfile calendar) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZone)).DateTime);
}
