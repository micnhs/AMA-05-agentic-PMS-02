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

    public static PsurSnapshot Snapshot(PortfolioSnapshotInput input, PsurSeries? series = null)
    {
        Guard.Against(input.End != input.Start.AddYears(1).AddDays(-1), "psur_period",
            "PSUR braucht eine explizite zusammenhaengende Zwoelfmonatsperiode.");
        Guard.Against(input.LeadProductId != input.ProductId, "psur_group",
            "Jede Einzelauswertung braucht ihren eigenen Produktscope; das Gruppenleitprodukt wird separat versioniert.");
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
        var actions = PsurSupplement.Actions(input);
        var result = new PsurSnapshot(input.ProductId, input.LeadProductId, input.Start, input.End, input.Region, snapshots,
            input.Exposure.Where(e => exposureIds.Contains(e.Id)).OrderBy(e => e.Id).ToArray(),
            input.Trends.Where(t => t.ProductId == input.ProductId && t.Input.Start == input.Start && t.Input.End == input.End).ToArray(),
            actions.Capas.Where(c => c.Selection != "ExcludedClosedBeforePeriod").Select(c => c.CurrentAtFreeze).ToArray(),
            EvidenceRules.Effective(input.Documents.Where(e => e.ProductId == input.ProductId), input.FrozenAt),
            input.FrozenAt, periods.Select(p => p.Label).ToArray(), periods.ToArray(),
            actions.Changes.Where(c => c.Selection != "ExcludedClosedBeforePeriod").Select(c => c.CurrentAtFreeze).ToArray(), "OccurredAt.UtcDate",
            "Synthetic dataset; no clinical completeness claim. Each numerator binds all unique source IDs, versions and hashes, including other regions and closed comparison-period cases. Period-end organizational knowledge cutoffs apply; later knowledge is a separate addendum. Freeze time does not prove historical case state. Documents use the single effective approved revision per ID at FrozenAt, not the case-knowledge cutoff. Missing periods remain unknown, not zero.",
            sourceManifest, CodingRules.Tables(periods.ToArray(), sourceManifest.Cases), series, actions);
        result = result with { RegulatoryHistory = input.RegulatoryReviews is null ? null : PsurRegulatoryRules.Capture(input, [input.ProductId]) };
        return result with { Directory = PsurSupplement.Directory(input, result) };
    }

    public static string PsurScope(Psur p) => Canonical.Hash(new { p.SnapshotHash, p.Draft, p.Findings,
        language = "de", recipient = "NOTIFIED-BODY-SIMULATOR",
        template = PsurRegulatoryRules.For(p.Snapshot) is not null ? "cohors-psur-2026.4" :
            p.Snapshot.Group is not null ? "cohors-psur-2026.3" : p.Snapshot.Series is null ? "cohors-psur-2026.1" : "cohors-psur-2026.2" });

    public static QualityFinding[] CheckPsur(PsurSnapshot snapshot, string draft)
    {
        var families = PsurGroupRules.Snapshots(snapshot);
        var findings = new List<QualityFinding>([.. PsurSeriesRules.Check(snapshot), .. PsurGroupRules.Check(snapshot),
            .. families.SelectMany(CheckFamilyData)]);
        var regulatory = PsurRegulatoryRules.For(snapshot);
        findings.AddRange(PsurRegulatoryRules.Check(regulatory,
            snapshot.Group?.Members.Select(m => m.Product.ProductId).ToArray() ?? [snapshot.ProductId], snapshot.End));
        if (snapshot.Group is not null)
            foreach (var family in families)
            {
                findings.AddRange(PsurRegulatoryRules.Check(family.RegulatoryHistory, [family.ProductId], family.End));
                if ((family.RegulatoryHistory?.Reviews ?? []).Any(r =>
                    regulatory?.Reviews.Any(g => Canonical.Hash(g) == Canonical.Hash(r)) != true))
                    findings.Add(new("psur_bs_group_source", "Critical", "Einzelfamilien- und gruppenweiter BS-Schnitt widersprechen sich.",
                        "Alle Mitglieder aus demselben konsistenten SQL-Schnitt einfrieren.", null));
            }
        var regulatoryRefs = PsurRegulatoryRules.References(regulatory).Select(r => $"psurReview:{r.Id}@{r.Version}").ToHashSet(StringComparer.Ordinal);
        var caseRefs = families.SelectMany(s => s.SourceManifest?.Cases.Select(c => $"case:{c.Reference.Id}@{c.Reference.Version}") ??
            s.Cases.Select(c => $"case:{c.Id}@{c.Version}")).ToHashSet(StringComparer.Ordinal);
        var evidenceRefs = families.SelectMany(s => s.Documents).Select(e => $"evidence:{e.Id}@{e.Version}").ToHashSet(StringComparer.Ordinal);
        var references = ReferencePattern().Matches(draft).Select(m => m.Groups[1].Value).ToArray();
        foreach (var family in families)
            if (family.Cases.Length > 0 && !references.Any(r => family.Cases.Any(c => r == $"case:{c.Id}@{c.Version}")) ||
                family.Documents.Length > 0 && !references.Any(r => family.Documents.Any(e => r == $"evidence:{e.Id}@{e.Version}")))
                findings.Add(new("psur_sources_missing", "Critical", $"Tragende Fall-/Dokumentbelege fuer {family.ProductId} fehlen.",
                    "Konkrete [case:ID@Version] und [evidence:ID@Version] aus jeder eingefrorenen Familie zitieren.", null));
        if (references.Any(r => !caseRefs.Contains(r) && !evidenceRefs.Contains(r) && !regulatoryRefs.Contains(r)))
            findings.Add(new("psur_source_invalid", "Critical", "PSUR enthaelt einen nicht aufloesbaren Versionsbezug.",
                "Nur die gebundenen Snapshot-Versionen verwenden.", null));
        if (regulatory?.Reviews.Any(r => r.Selection != "ExcludedClosedBeforePeriod" &&
            !references.Contains($"psurReview:{r.CurrentSource.Id}@{r.CurrentSource.Version}", StringComparer.Ordinal)) == true)
            findings.Add(new("psur_bs_source_missing", "Critical", "Ein offener, aktualisierter oder nachtraeglich bekannter BS-Vorgang fehlt im belegten Narrativ.",
                "Jeden relevanten [psurReview:ID@Version]-Bezug mit getrenntem Stichtags-/Freeze-Zustand aufnehmen.", null));
        foreach (var c in families.SelectMany(s => s.Cases).Where(c => c.Stage != "Closed"))
            if (!references.Contains($"case:{c.Id}@{c.Version}", StringComparer.Ordinal))
                findings.Add(new("psur_open_case_missing", "Critical", $"Offener Fall {c.Reference} fehlt im belegten Narrativ.",
                    "Untersuchung, Datenluecken und Freigabestatus transparent aufnehmen.", c.Id));
        var expectedMetrics = families.SelectMany(s => Metrics(s, snapshot.Group is null ? "" : s.ProductId + ":"))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var metrics = MetricPattern().Matches(draft).Select(m => (Key: m.Groups[1].Value, Value: m.Groups[2].Value)).ToArray();
        if (expectedMetrics.Any(e => !metrics.Any(m => m.Key == e.Key && m.Value == e.Value)) ||
            metrics.Any(m => !expectedMetrics.TryGetValue(m.Key, out var expected) || expected != m.Value))
            findings.Add(new("psur_numbers", "Critical", "Kennzahlen-/Unbekanntbelege stimmen nicht mit Produkt, Perioden und Regionen im Snapshot ueberein.",
                "Gebundene Kennzahlen korrigieren; bei Gruppen [metric:Produkt:Periode:Region:events|exposure=Wert] verwenden. Keine Produkte oder Regionen addieren.", null));
        findings.Add(new("psur_semantic_review", "Minor",
            "Struktur-, Versions- und Kennzahlenpruefung ersetzt keine medizinische, sprachliche oder Nutzen-Risiko-Plausibilitaetspruefung.",
            "Getrennte medizinische und regulatorische Human Reviews durchfuehren; offene Unsicherheit nicht als Risikoakzeptanz formulieren.", null));
        return findings.ToArray();
    }

    private static QualityFinding[] CheckFamilyData(PsurSnapshot snapshot)
    {
        var findings = new List<QualityFinding>([.. PsurSources.Check(snapshot), .. PsurSupplement.Check(snapshot)]);
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
            var relevantIds = snapshot.Cases.Select(c => c.Id)
                .Concat(snapshot.Periods.SelectMany(p => p.Regions).SelectMany(r => r.NumeratorSources ?? []).Select(r => r.Id))
                .Concat(sourceManifest.PostCutoffCases.Select(a => a.Source.Id)).ToHashSet(StringComparer.Ordinal);
            foreach (var source in sourceManifest.Cases.Where(c => relevantIds.Contains(c.Reference.Id) && c.Coding?.HistoricalAsOf is not null))
                findings.AddRange(CodingRules.HistoricalFindings(source.Coding!.Codes, source.Coding.HistoricalAsOf));
        }
        else if (snapshot.SourceManifest?.Cases.Any(c => c.Coding is not null) == true)
            findings.Add(new("psur_coding_sources", "Critical", "Kodierte Fallquellen besitzen keine nachvollziehbaren Kodetabellen.",
                "Neuen pruefbaren Snapshot einfrieren; Altberichte nicht im Hintergrund reparieren.", null));
        foreach (var kind in new[] { "RiskFile", "CER", "PMCF", "SSCP" })
            if (!snapshot.Documents.Any(e => e.Kind == kind))
                findings.Add(new("psur_document_missing", "Critical", $"Aktuelle kontrollierte {kind}-Evidenz fehlt.",
                    "Dokumentstand kontrolliert bereitstellen und neuen konsistenten Snapshot einfrieren.", null));
        return findings.ToArray();
    }

    private static IEnumerable<(string Key, string Value)> Metrics(PsurSnapshot snapshot, string prefix) =>
        snapshot.Periods.SelectMany(p => p.Regions.SelectMany(r => new[]
        {
            ($"{prefix}{p.Label}:{r.Region}:events", r.Events?.ToString(CultureInfo.InvariantCulture) ?? "unknown"),
            ($"{prefix}{p.Label}:{r.Region}:exposure", r.Exposure?.ToString(CultureInfo.InvariantCulture) ?? "unknown")
        })).Concat((snapshot.CodingTables ?? []).SelectMany(t => new[]
        {
            ($"{prefix}{t.Period}:{t.Region}:deathCoded", t.ExplicitDeathCases.Length.ToString(CultureInfo.InvariantCulture))
        }.Concat(t.UnverifiedHistoricalDeathCases is null ? [] : new[]
        {
            ($"{prefix}{t.Period}:{t.Region}:historicalDeathClaimed", t.UnverifiedHistoricalDeathCases.Length.ToString(CultureInfo.InvariantCulture))
        })));

    [GeneratedRegex(@"\[((?:case|evidence|psurReview):[^\]\r\n]+)\]")]
    private static partial Regex ReferencePattern();
    [GeneratedRegex(@"\[metric:([^=\]\r\n]+)=([^\]\r\n]+)\]")]
    private static partial Regex MetricPattern();
}

public sealed record PortfolioSnapshotInput(string ProductId, string LeadProductId, DateOnly Start, DateOnly End,
    string Region, SafetyCase[] Cases, Exposure[] Exposure, TrendRun[] Trends, Capa[] Capas,
    Evidence[] Documents, ChangeRequest[] Changes, DateTimeOffset FrozenAt, AggregateRevision[]? History = null,
    PublicSourceRun[]? SourceRuns = null, PsurReview[]? RegulatoryReviews = null);
