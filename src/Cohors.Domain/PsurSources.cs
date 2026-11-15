namespace Cohors.Domain;

public sealed record AggregateRevision(string Kind, string Id, int Version, DateTimeOffset At, string Json, string Hash);
public sealed record PsurCaseSource(VersionReference Reference, string ProductId, string Country, string Jurisdiction,
    DateTimeOffset? OccurredAt, DateTimeOffset FirstAwarenessAt, DateTimeOffset SystemReceivedAt, bool Synthetic,
    VersionReference? CutoffVersion, string StatusAtCutoff,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] CaseCoding? Coding = null);
public sealed record PsurAddendum(string Period, VersionReference Source, DateTimeOffset AwarenessAt, string Reason);
public sealed record PsurSourceManifest(string Version, DateTimeOffset KnowledgeCutoffExclusiveUtc,
    string PeriodKnowledgeRule, PsurCaseSource[] Cases, PsurAddendum[] PostCutoffCases, string StateEvidence);

public static class PsurSources
{
    public const string Version = "cohors-psur-sources-2";
    public static DateTimeOffset EndExclusive(DateOnly end) => new(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    public static PsurSourceManifest Freeze(PortfolioSnapshotInput input, SafetyCase[] cases, PsurPeriod[] periods)
    {
        var cutoff = EndExclusive(input.End);
        var revisions = input.History ?? [];
        var sources = cases.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c =>
        {
            var atCutoff = revisions.Where(r => r.Kind == "case" && r.Id == c.Id && r.At < cutoff)
                .OrderByDescending(r => r.Version).FirstOrDefault();
            return new PsurCaseSource(PortfolioRules.Reference(c), c.ProductId, c.Country, c.Jurisdiction,
                c.OccurredAt, c.FirstAwarenessAt, c.SystemReceivedAt, c.Synthetic,
                atCutoff is null ? null : new(c.Id, atCutoff.Version, atCutoff.Hash),
                c.FirstAwarenessAt >= cutoff ? "NotKnownAtCutoff" :
                    atCutoff is null ? "NotRecordedAtCutoff" : Canonical.Read<SafetyCase>(atCutoff.Json).Stage.ToString(), c.Coding);
        }).ToArray();
        var addenda = periods.SelectMany(p => cases.Where(c => PortfolioRules.InPeriod(c, p.Start, p.End) &&
                c.FirstAwarenessAt >= EndExclusive(p.End) && c.FirstAwarenessAt <= input.FrozenAt)
            .Select(c => new PsurAddendum(p.Label, PortfolioRules.Reference(c), c.FirstAwarenessAt,
                "Kenntnis nach dem jeweiligen Periodenstichtag: gesonderter Nachtrag, nicht im Basiszaehler.")))
            .OrderBy(a => a.Period, StringComparer.Ordinal).ThenBy(a => a.Source.Id, StringComparer.Ordinal).ToArray();
        return new(Version, cutoff, "Each period uses first organizational awareness before its exclusive UTC period end; late intake alone does not exclude an already-known event.",
            sources, addenda, "CutoffVersion binds a recorded SQL aggregate revision before the N cutoff. Missing history is explicitly unknown. Narrative Stage/Disposition/Assessment describe the frozen current record, not an invented historical state.");
    }

    public static QualityFinding[] Check(PsurSnapshot snapshot)
    {
        QualityFinding Invalid(string message) => new("psur_numerator_provenance", "Critical", message,
            "Neuen nachvollziehbaren Snapshot mit ausdruecklichem Stichtag und vollstaendigen Quellfallversionen erzeugen; Altberichte nicht still reparieren.", null);
        var manifest = snapshot.SourceManifest;
        if (manifest is null || manifest.Version != Version || manifest.Cases is null || manifest.PostCutoffCases is null ||
            manifest.KnowledgeCutoffExclusiveUtc != EndExclusive(snapshot.End))
            return [Invalid("PSUR besitzt keinen unterstuetzten vollstaendigen Stichtags-/Quellenmanifest.")];
        if (manifest.Cases.Any(c => c is null || c.Reference is null) ||
            manifest.Cases.Select(c => c.Reference.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Cases.Length)
            return [Invalid("Quellfallidentitaeten sind unvollstaendig oder nicht eindeutig.")];
        var known = manifest.Cases.ToDictionary(c => c.Reference.Id, StringComparer.Ordinal);
        var findings = new List<QualityFinding>();
        foreach (var p in snapshot.Periods)
            foreach (var region in p.Regions)
            {
                var expected = manifest.Cases.Where(c => c.Synthetic && c.ProductId == snapshot.ProductId &&
                    PortfolioRules.InRegion(c.Country, c.Jurisdiction, region.Region) &&
                    c.OccurredAt is { } occurred && PortfolioRules.UtcDate(occurred) >= p.Start &&
                    PortfolioRules.UtcDate(occurred) <= p.End && c.FirstAwarenessAt < EndExclusive(p.End))
                    .Select(c => c.Reference).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
                if (region.NumeratorSources is null || region.NumeratorSources.Any(r => r is null) ||
                    !region.NumeratorSources.OrderBy(r => r.Id, StringComparer.Ordinal).SequenceEqual(expected) ||
                    region.Events is { } count && count != expected.Length)
                    findings.Add(Invalid($"{p.Label}/{region.Region}: Zaehler und eindeutige Quellfallversionen stimmen nicht ueberein."));
            }
        var expectedAddenda = snapshot.Periods.SelectMany(p => manifest.Cases.Where(c =>
                c.OccurredAt is { } occurred && PortfolioRules.UtcDate(occurred) >= p.Start && PortfolioRules.UtcDate(occurred) <= p.End &&
                c.FirstAwarenessAt >= EndExclusive(p.End) && c.FirstAwarenessAt <= snapshot.FrozenAt)
            .Select(c => (p.Label, c.Reference))).ToHashSet();
        if (!expectedAddenda.SetEquals(manifest.PostCutoffCases.Select(a => (a.Period, a.Source))) ||
            manifest.PostCutoffCases.Length != expectedAddenda.Count)
            findings.Add(Invalid("Spaet bekannt gewordene Faelle sind nicht eindeutig als separate Nachtraege gebunden."));
        foreach (var c in snapshot.Cases)
            if (!known.TryGetValue(c.Id, out var source) || source.Reference.Version != c.Version ||
                c.CurrentPeriodEvent && source.FirstAwarenessAt >= manifest.KnowledgeCutoffExclusiveUtc ||
                c.StatusAtCutoff != source.StatusAtCutoff)
                findings.Add(Invalid("Narrativliste, Stichtagsstatus und Quellenmanifest verwenden verschiedene Fallstaende."));
        return findings.ToArray();
    }
}
