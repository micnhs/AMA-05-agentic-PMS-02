using System.Text.Json.Serialization;

namespace Cohors.Domain;

public sealed record MirFacts
{
    public DateOnly? EventDateFrom { get; init; }
    public DateOnly? EventDateTo { get; init; }
    public DateOnly? NextReportDate { get; init; }
    public string? EventClassification { get; init; }
    public string? DeviceProblemCode { get; init; }
    public string? DeviceLocation { get; init; }
    public string? PreliminaryAnalysis { get; init; }
    public string? InitialCorrectiveAction { get; init; }
    public string? FurtherInvestigation { get; init; }
    public string? UdiDi { get; init; }
    public string? UdiEntity { get; init; }
    public string? UdiPi { get; init; }
    public string? BasicUdiEntity { get; init; }
    public string? Distribution { get; init; }
    public string? RootCauses { get; init; }
    public string? RootCauseConfirmed { get; init; }
    public string? RiskAssessmentReviewed { get; init; }
    public string? InvestigationTypeCode { get; init; }
    public string? InvestigationFindingCode { get; init; }
    public string? InvestigationConclusionCode { get; init; }
    public string? ComponentCode { get; init; }
    public string? CorrectiveAction { get; init; }
    public string? FinalComments { get; init; }
    public string[] EvidenceIds { get; init; } = [];
    public string Rationale { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EventDateRationale { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthorityReference { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthorityReferenceRationale { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RationaleNoRiskReview { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RiskAssessmentAdequate { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RiskAssessmentResults { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirSimilarIncidents? SimilarIncidents { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManufacturerName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManufacturerSrn { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManufacturerCountry { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContactFirstName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContactLastName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContactStreet { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContactCity { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContactPostcode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContactPhone { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContactEmail { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SubmitterIsManufacturerContact { get; init; }
}

public sealed record MirSimilarPeriod(DateOnly Start, DateOnly End, int? CountryIncidents, int? RegionalIncidents,
    int? WorldwideIncidents, int? CountryDevices, int? RegionalDevices, int? WorldwideDevices, string MissingDataReason);
public sealed record MirSimilarIncidents(string Variant, string Details, string DenominatorBasis,
    string? OtherDenominator, string Method, string Geography, MirSimilarPeriod[] Periods);
public sealed record MirChecks(bool SchemaValid, string[] ContentErrors, string ContentBoundary,
    bool NationalSubmissionReady, string[] ExternalRequirements);
public sealed record MirArtifact(string Kind, string SchemaFile, string SchemaHash, string MappingVersion,
    string Xml, string XmlHash, bool Valid, string[] Errors, string[] Limitations, MirFacts Facts,
    DateTimeOffset CreatedAt, bool Simulated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FollowUpSchedule? FollowUpSchedule = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MirChecks? Checks = null);

public static class MirSchemas
{
    public static (string File, string Hash) For(string kind) => kind switch
    {
        "Initial" => ("incident-Initial-v7.3.1.xsd", "9eb0dc128520f7000fb5f61eeb41b88fed8631b531ffdf3db4c9d05ab30a24a9"),
        "FollowUp" => ("incident-Followup-v7.3.1.xsd", "9a1f67504dce36c16930df6d72932c7799de6410c38c480184acdaedfed05dc4"),
        "Final" => ("incident-FinalRep-v7.3.1.xsd", "b2394adf8cfe67cde2e5ee3c8c828c88f7c877cefecc13f51dc2d20ed4a7e158"),
        _ => throw new DomainException("mir_kind", "Nur Initial, FollowUp und Final (Reportable) sind im Pilot-Mapping implementiert.")
    };

    public static void RequireValid(ReportDraft report)
    {
        ReportTargets.RequireBound(report);
        if (report.Jurisdiction == "GB") return;
        var m = report.Mir;
        var schema = For(report.Kind);
        Guard.Against(m is null || !m.Valid || !m.Simulated || m.Errors.Length != 0 ||
            m.Kind != report.Kind || m.SchemaFile != schema.File || m.SchemaHash != schema.Hash ||
            m.XmlHash != Canonical.HashText(m.Xml), "mir_required",
            "Gueltiger, hashgebundener MIR-XSD-Export mit menschlich belegten Pflichtdaten erforderlich; keine erfundenen Daten.", 409);
        if (report.Kind != "Final")
            Guard.Against(m!.FollowUpSchedule is null || m.FollowUpSchedule.ReportDate != m.Facts.NextReportDate ||
                m.FollowUpSchedule.RuleVersion != FollowUpRules.RuleVersion, "mir_followup_schedule",
                "Geplanter Folgeberichtstermin braucht vor Freigabe einen expliziten unveraenderlichen Zeit-/Kalenderscope.", 409);
    }
}
