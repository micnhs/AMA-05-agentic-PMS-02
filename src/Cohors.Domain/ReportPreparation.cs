using System.Text.Json.Serialization;

namespace Cohors.Domain;

public sealed record ReportTarget(string Version, string Jurisdiction, string Authority, string Simulator,
    string FormFamily, bool MirSupported, bool NationalSubmissionReady, string[] ExternalRequirements);
public sealed record PreparedReportField(string Field, string? Value, string Origin, string Source,
    string Rationale, string[] ClaimIds, Citation[] Citations);
public sealed record ReportPreparation(string Contract, string ProductId, VersionReference CaseSource, Device Device,
    ProductGovernance? Governance, ReportTarget Target, string Mode, string AuthoringRunId,
    PreparedReportField[] Fields,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReportFieldIssue[]? Problems = null);
public sealed record ReportFieldIssue(string Field, string Reason, string Explanation, string RequiredInformation,
    string[] Sources);
public sealed record ReportingContactFacts(string ContactId, string? FirstName, string? LastName, string? Street,
    string? City, string? Postcode, string? Phone, string? Email, bool? IsSubmitter);
public sealed record ReportingContactEvidence(string Contract, string ProductId, string ManufacturerSrn,
    string[] Jurisdictions, string Role, ReportingContactFacts Contact);
public sealed record ReportReturn(string ActorId, string Role, DateTimeOffset At, string Reason,
    string PreviousScopeHash);
public sealed record VigilanceText(string Text, string[] ClaimIds);
public sealed record VigilanceFields(VigilanceText PreliminaryAnalysis, VigilanceText FurtherInvestigation);

public static class ReportTargets
{
    public const string Version = "cohors-vigilance-targets-2026.1";

    public static ReportTarget For(string jurisdiction)
    {
        var (authority, simulator) = jurisdiction switch
        {
            "EU-IE" => ("Health Products Regulatory Authority (HPRA)", "HPRA-SIMULATOR"),
            "EU-DE" => ("Bundesinstitut fuer Arzneimittel und Medizinprodukte (BfArM)", "BfArM-SIMULATOR"),
            "EU-FR" => ("Agence nationale de securite du medicament et des produits de sante (ANSM)", "ANSM-SIMULATOR"),
            "EU-IT" => ("Ministero della Salute", "Ministero-Salute-SIMULATOR"),
            "NI" => ("Medicines and Healthcare products Regulatory Agency (MHRA), Northern Ireland", "MHRA-NI-SIMULATOR"),
            "GB" => ("Medicines and Healthcare products Regulatory Agency (MHRA), Great Britain", "MHRA-GB-SIMULATOR"),
            _ => throw new DomainException("recipient_unknown", "Kein freigegebenes Empfaengerprofil.")
        };
        string[] requirements = jurisdiction switch
        {
            "EU-FR" => ["ANSM verlangt PDF UND XML. Das Pilot-XML allein ist kein vollstaendiges franzoesisches Versandpaket; keine PDF-/Portalintegration."],
            "NI" => ["EU-MIR innerhalb MHRA MORE; konkrete technische NI-Importannahme wurde nicht getestet."],
            "GB" => ["GB-SIM-1 ist nur ein Pilotnarrativ. GB-MIR 7.2.1GB, UKRP, GMDN, IMDRF A-G und bedingte UKAB-Angaben sind nicht implementiert; kein EU-XSD als Ersatz. PDF ist kein MORE-Einreichungsweg."],
            _ => ["Nationale Kanal-/Sprachdetails sind nicht vollstaendig primaer verifiziert; kein Nachweis einer nationalen Versandbereitschaft."]
        };
        return new(Version, jurisdiction, authority, simulator, jurisdiction == "GB" ? "GB-SIM-1; GB-MIR 7.2.1GB nicht implementiert" : "EU MIR 7.3.1",
            jurisdiction != "GB", false,
            [.. requirements, "Nur synthetische Daten und Local-Simulator. Fachliche Vollstaendigkeit, Kodierung, Uebersetzung und reale Einreichungsfaehigkeit sind keine Folge einer bestandenen XSD-Pruefung."]);
    }

    public static void RequireBound(ReportDraft report)
    {
        if (report.Preparation is not { } preparation) return;
        Guard.Against(preparation.Contract is not ("cohors-report-prefill-1" or "cohors-report-prefill-2") ||
            preparation.Contract == "cohors-report-prefill-2" && preparation.Problems is null ||
            Canonical.Hash(preparation.Target) != Canonical.Hash(For(report.Jurisdiction)) ||
            preparation.Target.Simulator != report.Recipient ||
            preparation.Device.Id != preparation.ProductId || preparation.CaseSource.Version < 1 ||
            preparation.CaseSource.Hash.Length != 64 ||
            preparation.Governance is { } governance && governance.ProductId != preparation.ProductId,
            "report_target", "Vorbefuellter Ziel-/Produktscope passt nicht zum konkreten Bericht.", 409);
    }
}
