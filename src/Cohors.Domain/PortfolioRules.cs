using System.Globalization;
using System.Text.RegularExpressions;

namespace Cohors.Domain;

public static partial class PortfolioRules
{
    public static readonly string[] Regions = ["Worldwide", "EEA-Turkey-NI"];
    private static readonly HashSet<string> SubregionCountries =
        new("AT BE BG HR CY CZ DK EE FI FR DE GR HU IE IT LV LT LU MT NL PL PT RO SK SI ES SE IS LI NO TR".Split(' '), StringComparer.Ordinal);

    public static void RequireRegion(string region) =>
        Guard.Against(!Regions.Contains(region, StringComparer.Ordinal), "region_scope", "Explizites Worldwide- oder EEA-Turkey-NI-Profil erforderlich.");

    public static bool InRegion(SafetyCase c, string region) =>
        InRegion(c.Country, c.Jurisdiction, region);

    public static bool InRegion(string country, string jurisdiction, string region) =>
        region == "Worldwide" || region == "EEA-Turkey-NI" && (jurisdiction == "NI" || SubregionCountries.Contains(country));

    public static DateOnly UtcDate(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);

    public static bool InPeriod(SafetyCase c, DateOnly start, DateOnly end) =>
        c.OccurredAt is { } occurred && UtcDate(occurred) >= start && UtcDate(occurred) <= end;

    public static VersionReference Reference(SafetyCase c) => new(c.Id, c.Version, Canonical.Hash(c));

    public static PsurSnapshot Snapshot(PortfolioSnapshotInput input)
    {
        Guard.Against(input.End != input.Start.AddYears(1).AddDays(-1), "psur_period",
            "PSUR braucht eine explizite zusammenhaengende Zwoelfmonatsperiode.");
        Guard.Against(input.LeadProductId != input.ProductId, "psur_group",
            "Die Pilotgruppe enthaelt eine Produktfamilie; ein anderes Leitprodukt braucht separat gepruefte Gruppierung.");
        RequireRegion(input.Region);
        var cases = input.Cases.Where(c => c.Synthetic && c.ProductId == input.ProductId).ToArray();
        Guard.Against(cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != cases.Length,
            "psur_duplicate_case", "Ein Vorfall darf nur mit genau einer eingefrorenen Quellversion eingehen.");
        var periods = new List<PsurPeriod>();
        for (var offset = 0; offset < 4; offset++)
        {
            var start = input.Start.AddYears(-offset);
            var end = input.Start.AddYears(1 - offset).AddDays(-1);
            var summaries = Regions.Select(region =>
            {
                var matches = input.Exposure.Where(e => e.ProductId == input.ProductId && e.Region == region &&
                    e.Start == start && e.End == end && e.Approved).ToArray();
                Guard.Against(matches.Length > 1, "psur_denominator_ambiguous", "Mehrere Nenner fuer dieselbe Periode/Region: fachlich aufloesen.");
                var e = matches.SingleOrDefault();
                var numerator = cases.Where(c => InRegion(c, region) && InPeriod(c, start, end) &&
                    c.FirstAwarenessAt < PsurSources.EndExclusive(end)).OrderBy(c => c.Id, StringComparer.Ordinal).Select(Reference).ToArray();
                if (e is null) return new PsurRegionSummary(region, "MissingExposureAndCompleteness", null, null, null, null, null, null, numerator);
                Guard.Against(e.Count < 0 || e.Unit is not ("Implants" or "DeviceYears"), "psur_denominator", "Ungueltiger PSUR-Nenner.");
                var count = numerator.Length;
                return new PsurRegionSummary(region, "SyntheticExposureAvailable", count, e.Count, e.Id, e.Version, e.Unit,
                    e.Count == 0 ? null : Statistics.DescriptiveRate(new(input.ProductId, region, start, end, count, e, e.Unit, 1000)), numerator);
            }).ToArray();
            var worldwide = summaries[0];
            var subset = summaries[1];
            Guard.Against(worldwide.Exposure is not null && subset.Exposure is not null &&
                (worldwide.Unit != subset.Unit || subset.Exposure > worldwide.Exposure || subset.Events > worldwide.Events),
                "psur_subset", "Die Teilregion muss im weltweiten Nenner und Zaehler enthalten sein; keine Addition oder widerspruechlichen Populationen.");
            periods.Add(new(offset == 0 ? "N" : $"N{offset + 1}", start, end, summaries));
        }
        Guard.Against(periods[0].Regions.Single(r => r.Region == input.Region).Exposure is null,
            "psur_current_exposure", "Passender freigegebener Nenner fuer N fehlt.");
        var sourceManifest = PsurSources.Freeze(input, cases, periods.ToArray());
        var cutoff = sourceManifest.KnowledgeCutoffExclusiveUtc;
        var included = cases.Where(c => InRegion(c, input.Region) &&
            (c.FirstAwarenessAt < cutoff && (InPeriod(c, input.Start, input.End) || c.Stage != CaseStage.Closed ||
                c.ClosedAt is { } closed && UtcDate(closed) >= input.Start) ||
                c.FirstAwarenessAt >= cutoff && c.FirstAwarenessAt <= input.FrozenAt && InPeriod(c, input.Start, input.End)))
            .OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        var snapshots = included.Select(c =>
        {
            var assessment = c.CurrentAssessment;
            var currentEvidence = assessment is not null && assessment.EvidenceHash == Canonical.Hash(c.Evidence);
            var decision = c.Decisions.LastOrDefault(d => d.AssessmentVersion == assessment?.Version);
            var reviewed = currentEvidence && decision is not null &&
                (decision.Disposition != Disposition.NonReportable ||
                    c.Disposition == Disposition.NonReportable && c.Approvals.Any(a => a.Role == Roles.Regulatory && a.ScopeHash == decision.ScopeHash));
            var questions = (assessment?.Content.Uncertainties ?? ["Kein aktuelles Assessment."])
                .Concat(c.InformationRequests.Where(r => r.Status != "SimulatedAnswered").Select(r => r.Question))
                .Concat(c.OccurredAt is null ? ["Ereignisdatum unbekannt; aus periodischen Raten ausgeschlossen."] : [])
                .Concat(currentEvidence ? [] : ["Assessment/Evidenzstand nicht aktuell freigegeben."]).Distinct().ToArray();
            return new CaseSnapshot(c.Id, c.Version, c.Reference, c.Disposition.ToString(), c.Stage.ToString(), c.Country,
                c.Description, assessment is null ? null : Canonical.Hash(assessment), reviewed, questions,
                c.FirstAwarenessAt < cutoff && InPeriod(c, input.Start, input.End),
                c.FirstAwarenessAt < cutoff && !InPeriod(c, input.Start, input.End),
                reviewed ? decision!.ScopeHash : null, assessment?.Content,
                c.FirstAwarenessAt < cutoff ? "Baseline" : "PostCutoffAddendum",
                sourceManifest.Cases.Single(source => source.Reference.Id == c.Id).StatusAtCutoff);
        }).ToArray();
        var exposureIds = periods.SelectMany(p => p.Regions).Select(r => r.ExposureId).OfType<string>().ToHashSet();
        return new(input.ProductId, input.LeadProductId, input.Start, input.End, input.Region, snapshots,
            input.Exposure.Where(e => exposureIds.Contains(e.Id)).OrderBy(e => e.Id).ToArray(),
            input.Trends.Where(t => t.ProductId == input.ProductId && t.Input.Start == input.Start && t.Input.End == input.End).ToArray(),
            input.Capas.Where(c => c.ProductId == input.ProductId).ToArray(),
            EvidenceRules.Effective(input.Documents.Where(e => e.ProductId == input.ProductId), input.FrozenAt),
            input.FrozenAt, periods.Select(p => p.Label).ToArray(), periods.ToArray(),
            input.Changes.Where(c => c.ProductId == input.ProductId).ToArray(), "OccurredAt.UtcDate",
            "Synthetic dataset; no clinical completeness claim. Each numerator binds all unique source IDs, versions and hashes, including other regions and closed comparison-period cases. Period-end organizational knowledge cutoffs apply; later knowledge is a separate addendum. Freeze time does not prove historical case state. Documents use the single effective approved revision per ID at FrozenAt, not the case-knowledge cutoff. Missing periods remain unknown, not zero.",
            sourceManifest, CodingRules.Tables(periods.ToArray(), sourceManifest.Cases));
    }

    public static string PsurScope(Psur p) => Canonical.Hash(new { p.SnapshotHash, p.Draft, p.Findings,
        language = "de", recipient = "NOTIFIED-BODY-SIMULATOR", template = "cohors-psur-2026.1" });

    public static QualityFinding[] CheckPsur(PsurSnapshot snapshot, string draft)
    {
        var findings = new List<QualityFinding>(PsurSources.Check(snapshot));
        if (snapshot.SourceManifest is { } sourceManifest && snapshot.CodingTables is { } codingTables)
        {
            if (Canonical.Hash(codingTables) != Canonical.Hash(CodingRules.Tables(snapshot.Periods, sourceManifest.Cases)))
                findings.Add(new("psur_coding_sources", "Critical", "Kodetabellen und eingefrorene Quellfallkodierung stimmen nicht ueberein.",
                    "Tabellen aus den gebundenen Quellversionen neu erzeugen; keine stille Umkodierung.", null));
            if (codingTables.Any(t => t.Rows.Any(r => r.Status == "BlockedHierarchy")))
                findings.Add(new("psur_coding_hierarchy", "Major", "Der Mappingstand hat fuer verwendete Kodes eine widerspruechliche Hierarchie.",
                    "Autoritative Hierarchieklaerung kontrolliert nachweisen und neu einfrieren; Kode-Mitgliedschaft bleibt davon getrennt gueltig.", null));
            if (codingTables.Any(t => t.MissingAnnexCodes.Length > 0))
                findings.Add(new("psur_coding_incomplete", "Minor", "A/C/D/F-Kodierung ist nicht fuer alle Faelle vollstaendig belegt.",
                    "Unbekannte Kodierung und Grenzen der Todesfallauswertung im Human Review ausdruecklich benennen; keine Null-Vollstaendigkeit behaupten.", null));
        }
        else if (snapshot.SourceManifest?.Cases.Any(c => c.Coding is not null) == true)
            findings.Add(new("psur_coding_sources", "Critical", "Kodierte Fallquellen besitzen keine nachvollziehbaren Kodetabellen.",
                "Neuen pruefbaren Snapshot einfrieren; Altberichte nicht im Hintergrund reparieren.", null));
        foreach (var kind in new[] { "RiskFile", "CER", "PMCF", "SSCP" })
            if (!snapshot.Documents.Any(e => e.Kind == kind))
                findings.Add(new("psur_document_missing", "Critical", $"Aktuelle kontrollierte {kind}-Evidenz fehlt.",
                    "Dokumentstand kontrolliert bereitstellen und neuen konsistenten Snapshot einfrieren.", null));
        var caseRefs = (snapshot.SourceManifest?.Cases.Select(c => $"case:{c.Reference.Id}@{c.Reference.Version}") ??
            snapshot.Cases.Select(c => $"case:{c.Id}@{c.Version}")).ToHashSet(StringComparer.Ordinal);
        var evidenceRefs = snapshot.Documents.Select(e => $"evidence:{e.Id}@{e.Version}").ToHashSet(StringComparer.Ordinal);
        var references = ReferencePattern().Matches(draft).Select(m => m.Groups[1].Value).ToArray();
        if (snapshot.Cases.Length > 0 && !references.Any(caseRefs.Contains) ||
            snapshot.Documents.Length > 0 && !references.Any(evidenceRefs.Contains))
            findings.Add(new("psur_sources_missing", "Critical", "Tragende Fall-/Dokumentbelege fehlen.",
                "Konkrete [case:ID@Version] und [evidence:ID@Version] aus dem eingefrorenen Snapshot zitieren.", null));
        if (references.Any(r => !caseRefs.Contains(r) && !evidenceRefs.Contains(r)))
            findings.Add(new("psur_source_invalid", "Critical", "PSUR enthaelt einen nicht aufloesbaren Versionsbezug.",
                "Nur die gebundenen Snapshot-Versionen verwenden.", null));
        foreach (var c in snapshot.Cases.Where(c => c.Stage != "Closed"))
            if (!references.Contains($"case:{c.Id}@{c.Version}", StringComparer.Ordinal))
                findings.Add(new("psur_open_case_missing", "Critical", $"Offener Fall {c.Reference} fehlt im belegten Narrativ.",
                    "Untersuchung, Datenluecken und Freigabestatus transparent aufnehmen.", c.Id));
        var expectedMetrics = snapshot.Periods.SelectMany(p => p.Regions.SelectMany(r => new[]
        {
            ($"{p.Label}:{r.Region}:events", r.Events?.ToString(CultureInfo.InvariantCulture) ?? "unknown"),
            ($"{p.Label}:{r.Region}:exposure", r.Exposure?.ToString(CultureInfo.InvariantCulture) ?? "unknown")
        })).Concat((snapshot.CodingTables ?? []).Select(t =>
            ($"{t.Period}:{t.Region}:deathCoded", t.ExplicitDeathCases.Length.ToString(CultureInfo.InvariantCulture))))
            .ToDictionary(p => p.Item1, p => p.Item2, StringComparer.Ordinal);
        var metrics = MetricPattern().Matches(draft).Select(m => (Key: m.Groups[1].Value, Value: m.Groups[2].Value)).ToArray();
        if (expectedMetrics.Any(e => !metrics.Any(m => m.Key == e.Key && m.Value == e.Value)) ||
            metrics.Any(m => !expectedMetrics.TryGetValue(m.Key, out var expected) || expected != m.Value))
            findings.Add(new("psur_numbers", "Critical", "Kennzahlen-/Unbekanntbelege stimmen nicht mit Perioden und Regionen im Snapshot ueberein.",
                "Die [metric:Periode:Region:events|exposure=Wert] Angaben aus dem Snapshot korrigieren; Regionen nicht addieren.", null));
        findings.Add(new("psur_semantic_review", "Minor",
            "Struktur-, Versions- und Kennzahlenpruefung ersetzt keine medizinische, sprachliche oder Nutzen-Risiko-Plausibilitaetspruefung.",
            "Getrennte medizinische und regulatorische Human Reviews durchfuehren; offene Unsicherheit nicht als Risikoakzeptanz formulieren.", null));
        return findings.ToArray();
    }

    [GeneratedRegex(@"\[((?:case|evidence):[^\]\r\n]+)\]")]
    private static partial Regex ReferencePattern();
    [GeneratedRegex(@"\[metric:([^=\]\r\n]+)=([^\]\r\n]+)\]")]
    private static partial Regex MetricPattern();
}

public sealed record PortfolioSnapshotInput(string ProductId, string LeadProductId, DateOnly Start, DateOnly End,
    string Region, SafetyCase[] Cases, Exposure[] Exposure, TrendRun[] Trends, Capa[] Capas,
    Evidence[] Documents, ChangeRequest[] Changes, DateTimeOffset FrozenAt, AggregateRevision[]? History = null);
