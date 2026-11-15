using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Cohors.Domain;

public sealed record TerminologyOrigin(string Publisher, string Page, string Download, string Sha256,
    DateOnly PageMaintenanceDate, string WorkbookRelease, string Scope);
public sealed record TerminologyTerm(string Code, string Annex, string Term, int? Level,
    int SourceLevelColumn, string[] Hierarchy, string? HierarchyIssue, bool AutomaticAggregationEligible,
    bool CurrentSelectable, string? SourceStatus, string? SourceStatusDescription, string SourceSheet,
    int SourceRow, string SourceCode);
public sealed record TerminologyReference(string ReleaseId, TerminologyOrigin Source, TerminologyTerm[] Terms);
public sealed record HistoricalCodingProof(string Code, string EvidenceId, int EvidenceVersion, string EvidenceHash,
    string Locator, string Quote, DateOnly OriginalCodingDate, string OriginalTerm, string ClaimedRelease);
public sealed record AssignedCode(TerminologyTerm Source, string? GroupCode, string? GroupTerm,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EarliestKnownReleaseYear = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HistoricalCodingProof? OriginalProof = null);
public sealed record CaseCoding(int Version, string ReleaseId, string ReferenceHash, AssignedCode[] Codes,
    Citation[] Evidence, string Rationale, string ActorId, DateTimeOffset RecordedAt, DateOnly? HistoricalAsOf,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Interpretation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QualityFinding[]? Findings = null);

public sealed partial class TerminologyCatalog(TerminologyReference reference, string hash)
{
    public TerminologyReference Reference { get; } = reference;
    public string Hash { get; } = hash;
    private readonly Dictionary<string, TerminologyTerm> terms = reference.Terms.ToDictionary(t => t.Code, StringComparer.Ordinal);

    public AssignedCode Select(string code, DateOnly? historicalAsOf)
    {
        Guard.Against(!terms.TryGetValue(code, out var term), "terminology_unknown",
            "Kode fehlt im kontrollierten FDA-IMDRF-Mappingstand; keine reine Laengen-/XSD-Pruefung.", 400);
        Guard.Against(!term!.CurrentSelectable && historicalAsOf is null, "terminology_retired",
            "Historischer/retired Kode ist nicht fuer eine neue aktuelle Kodierung selektierbar.");
        Guard.Against(historicalAsOf >= Reference.Source.PageMaintenanceDate, "terminology_history",
            "Eine historische Originalangabe muss zeitlich vor dem aktuellen Mappingstand liegen; dies allein beweist keine damalige Gueltigkeit.");
        var introduction = IntroductionPattern().Match(term.SourceStatusDescription ?? "");
        int? year = introduction.Success ? int.Parse(introduction.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        var status = historicalAsOf is null ? "CurrentMapping" :
            year is { } introduced && historicalAsOf.Value.Year < introduced ? "HistoricalChronologyConflict" : "HistoricalValidityUnknown";
        var group = historicalAsOf is null && term.AutomaticAggregationEligible
            ? terms[term.Hierarchy.Length > 1 ? term.Hierarchy[1] : term.Hierarchy[0]] : null;
        return new(term, group?.Code, group?.Term, status, year);
    }

    [GeneratedRegex(@"\bTerm added for Release Number (20\d{2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex IntroductionPattern();
}

public sealed record PsurCodeRow(string Annex, string Code, string Term, bool Historical, string Status,
    VersionReference[] Cases, string[] OriginalCodes);
public sealed record PsurCodingTable(string Period, string Region, PsurCodeRow[] Rows,
    VersionReference[] ExplicitDeathCases, VersionReference[] MissingAnnexCodes, string Completeness,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] VersionReference[]? UnverifiedHistoricalDeathCases = null);
public sealed record TerminologyBinding(string ReleaseId, string ReferenceHash);

public static class CodingRules
{
    public static AssignedCode BindOriginal(AssignedCode code, HistoricalCodingProof proof, DateOnly? asOf,
        Evidence[] evidence, string[] selectedEvidence)
    {
        var source = evidence.SingleOrDefault(e => e.Id == proof.EvidenceId && e.Version == proof.EvidenceVersion);
        Guard.Against(asOf is null || proof.OriginalCodingDate != asOf || proof.Code != code.Source.Code ||
            !selectedEvidence.Contains(proof.EvidenceId, StringComparer.Ordinal) ||
            source is null || !source.Approved || !source.Synthetic ||
            source.Kind is not ("Complaint" or "ClinicalFollowUp" or "HistoricalCodingRecord") ||
            source.Hash != proof.EvidenceHash || source.Locator != proof.Locator,
            "coding_original_proof", "Historische Originalangabe braucht einen passenden versions-/hashgebundenen Fallbeleg; allgemeine Produktunterlagen genuegen nicht.");
        Guard.Text(proof.Quote, "Wortgetreuer historischer Kodierungsbeleg", 20, 4000);
        Guard.Text(proof.OriginalTerm, "Originalterm", 2, 300);
        Guard.Text(proof.ClaimedRelease, "Im Original genannter Terminologiestand", 4, 100);
        Guard.Against(!source!.Content.Contains(proof.Quote, StringComparison.Ordinal) ||
            !Regex.IsMatch(proof.Quote, @"(?<![A-Z0-9])" + Regex.Escape(proof.Code) + @"(?![A-Z0-9])", RegexOptions.CultureInvariant) ||
            !proof.Quote.Contains(proof.OriginalCodingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            !proof.Quote.Contains(proof.OriginalTerm, StringComparison.Ordinal) ||
            !proof.Quote.Contains(proof.ClaimedRelease, StringComparison.Ordinal),
            "coding_original_quote", "Der unveraenderte Originalauszug muss genau Kode, Term, Kodierungsdatum und behaupteten Release belegen.");
        EvidenceRules.RequireIntegrity(source);
        return code with { OriginalProof = proof, GroupCode = null, GroupTerm = null,
            Status = code.Status == "HistoricalChronologyConflict" ? code.Status : "HistoricalOriginalDocumentedReleaseUnknown" };
    }

    public static QualityFinding[] HistoricalFindings(AssignedCode[] codes, DateOnly? asOf)
    {
        if (asOf is null) return [];
        return codes.Select(code => new QualityFinding(
            code.Status == "HistoricalChronologyConflict" ? "coding_historical_chronology" : "coding_historical_validity",
            code.Status == "HistoricalChronologyConflict" ? "Major" : "Minor",
            code.Status == "HistoricalChronologyConflict"
                ? $"{code.Source.Code}: behaupteter Zeitpunkt {asOf:yyyy-MM-dd} liegt vor dem ausdruecklich dokumentierten Einfuehrungsjahr {code.EarliestKnownReleaseYear}."
                : $"{code.Source.Code}: {(code.OriginalProof is null ? "unbelegte historische Angabe" : "Originalangabe als wortgetreuer Fallbeleg gebunden")}; damalige Release-Gueltigkeit und Hierarchie sind nicht nachgewiesen.",
            "Original unveraendert und ungegroupiert erhalten. Heutige retrospektive Kodierung als neue menschliche Entscheidung getrennt erfassen; keine Rueckdatierung oder stille Umkodierung.",
            code.Source.Code)).ToArray();
    }

    private static string Status(CaseCoding coding, AssignedCode code) => coding.HistoricalAsOf is not null
        ? code.Status is "HistoricalChronologyConflict" or "HistoricalOriginalDocumentedReleaseUnknown"
            ? code.Status : "HistoricalValidityUnknown"
        : code.Source.AutomaticAggregationEligible ? "Grouped" : "BlockedHierarchy";

    public static PsurCodingTable[] Tables(PsurPeriod[] periods, PsurCaseSource[] sources)
    {
        var byId = sources.ToDictionary(c => c.Reference.Id, StringComparer.Ordinal);
        return periods.SelectMany(period => period.Regions.Select(region =>
        {
            var counted = (region.NumeratorSources ?? []).Select(r => byId[r.Id]).ToArray();
            var coded = counted.SelectMany(c => (c.Coding?.Codes ?? []).Select(code => (Case: c, Code: code)));
            var rows = coded.GroupBy(x => new
                {
                    x.Code.Source.Annex,
                    Code = x.Case.Coding!.HistoricalAsOf is null ? x.Code.GroupCode ?? x.Code.Source.Code : x.Code.Source.Code,
                    Term = x.Case.Coding.HistoricalAsOf is null ? x.Code.GroupTerm ?? x.Code.Source.Term :
                        x.Code.OriginalProof?.OriginalTerm ?? x.Code.Source.Term,
                    Historical = x.Case.Coding.HistoricalAsOf is not null,
                    Status = Status(x.Case.Coding, x.Code)
                }).Select(g => new PsurCodeRow(g.Key.Annex, g.Key.Code, g.Key.Term, g.Key.Historical, g.Key.Status,
                    g.Select(x => x.Case.Reference).Distinct().OrderBy(r => r.Id, StringComparer.Ordinal).ToArray(),
                    g.Select(x => x.Code.Source.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()))
                .OrderBy(r => r.Annex, StringComparer.Ordinal).ThenByDescending(r => r.Cases.Length)
                .ThenBy(r => r.Code, StringComparer.Ordinal).ToArray();
            return new PsurCodingTable(period.Label, region.Region, rows,
                counted.Where(c => c.Coding is { HistoricalAsOf: null } coding &&
                    coding.Codes.Any(code => code.Source.Annex == "F" && code.Source.Code == "F02"))
                    .Select(c => c.Reference).ToArray(),
                counted.Where(c => new[] { "A", "C", "D", "F" }.Any(annex =>
                    c.Coding is not { HistoricalAsOf: null } coding || !coding.Codes.Any(code => code.Source.Annex == annex)))
                    .Select(c => c.Reference).ToArray(),
                "Unique cases per current group, not a sum of diagnoses. Current retrospective coding is separate from claimed historical originals. Historical rows retain literal codes/terms without asserting historical release validity or reusing today's grouping. Undocumented historical F02 claims are separate from current F02 counts; neither missing codes nor unknown historical validity prove absence of death. BlockedHierarchy rows are unaggregated source codes, not repaired Level-2 groups.",
                counted.Where(c => c.Coding is { HistoricalAsOf: not null } coding &&
                    coding.Codes.Any(code => code.Source.Annex == "F" && code.Source.Code == "F02"))
                    .Select(c => c.Reference).ToArray());
        })).ToArray();
    }
}
