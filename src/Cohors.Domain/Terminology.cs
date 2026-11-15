namespace Cohors.Domain;

public sealed record TerminologyOrigin(string Publisher, string Page, string Download, string Sha256,
    DateOnly PageMaintenanceDate, string WorkbookRelease, string Scope);
public sealed record TerminologyTerm(string Code, string Annex, string Term, int? Level,
    int SourceLevelColumn, string[] Hierarchy, string? HierarchyIssue, bool AutomaticAggregationEligible,
    bool CurrentSelectable, string? SourceStatus, string? SourceStatusDescription, string SourceSheet,
    int SourceRow, string SourceCode);
public sealed record TerminologyReference(string ReleaseId, TerminologyOrigin Source, TerminologyTerm[] Terms);
public sealed record AssignedCode(TerminologyTerm Source, string? GroupCode, string? GroupTerm);
public sealed record CaseCoding(int Version, string ReleaseId, string ReferenceHash, AssignedCode[] Codes,
    Citation[] Evidence, string Rationale, string ActorId, DateTimeOffset RecordedAt, DateOnly? HistoricalAsOf);

public sealed class TerminologyCatalog(TerminologyReference reference, string hash)
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
            "Historische Ausnahme braucht einen expliziten belegten Zeitpunkt vor dem aktuellen Mappingstand.");
        var group = term.AutomaticAggregationEligible
            ? terms[term.Hierarchy.Length > 1 ? term.Hierarchy[1] : term.Hierarchy[0]] : null;
        return new(term, group?.Code, group?.Term);
    }
}

public sealed record PsurCodeRow(string Annex, string Code, string Term, bool Historical, string Status,
    VersionReference[] Cases, string[] OriginalCodes);
public sealed record PsurCodingTable(string Period, string Region, PsurCodeRow[] Rows,
    VersionReference[] ExplicitDeathCases, VersionReference[] MissingAnnexCodes, string Completeness);
public sealed record TerminologyBinding(string ReleaseId, string ReferenceHash);

public static class CodingRules
{
    public static PsurCodingTable[] Tables(PsurPeriod[] periods, PsurCaseSource[] sources)
    {
        var byId = sources.ToDictionary(c => c.Reference.Id, StringComparer.Ordinal);
        return periods.SelectMany(period => period.Regions.Select(region =>
        {
            var counted = (region.NumeratorSources ?? []).Select(r => byId[r.Id]).ToArray();
            var coded = counted.SelectMany(c => (c.Coding?.Codes ?? []).Select(code => (Case: c, Code: code)));
            var rows = coded.GroupBy(x => new
                {
                    x.Code.Source.Annex, Code = x.Code.GroupCode ?? x.Code.Source.Code,
                    Term = x.Code.GroupTerm ?? x.Code.Source.Term, Historical = x.Case.Coding!.HistoricalAsOf is not null,
                    Status = x.Code.Source.AutomaticAggregationEligible ? "Grouped" : "BlockedHierarchy"
                }).Select(g => new PsurCodeRow(g.Key.Annex, g.Key.Code, g.Key.Term, g.Key.Historical, g.Key.Status,
                    g.Select(x => x.Case.Reference).Distinct().OrderBy(r => r.Id, StringComparer.Ordinal).ToArray(),
                    g.Select(x => x.Code.Source.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()))
                .OrderBy(r => r.Annex, StringComparer.Ordinal).ThenByDescending(r => r.Cases.Length)
                .ThenBy(r => r.Code, StringComparer.Ordinal).ToArray();
            return new PsurCodingTable(period.Label, region.Region, rows,
                counted.Where(c => c.Coding?.Codes.Any(code => code.Source.Annex == "F" && code.Source.Code == "F02") == true)
                    .Select(c => c.Reference).ToArray(),
                counted.Where(c => new[] { "A", "C", "D", "F" }.Any(annex =>
                    c.Coding?.Codes.Any(code => code.Source.Annex == annex) != true)).Select(c => c.Reference).ToArray(),
                "Unique cases per group, not a sum of diagnoses. Original codes and terms remain bound. Explicit F02/Death is always shown, independently of frequency; missing F codes do not prove absence of death. Historical entries are separate. BlockedHierarchy rows are unaggregated source codes, not repaired Level-2 groups.");
        })).ToArray();
    }
}
