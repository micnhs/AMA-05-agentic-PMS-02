namespace Cohors.Domain;

public sealed record PsurMonth(DateOnly Month, DateOnly From, DateOnly Through, VersionReference[] Cases,
    VersionReference[] PostCutoffCases, int OpenAtCutoff, int UnknownStateAtCutoff);
public sealed record PsurMonthlyCase(VersionReference Source, DateTimeOffset? OccurredAt,
    DateTimeOffset FirstAwarenessAt, DateTimeOffset SystemReceivedAt, string Selection,
    string Knowledge, string StatusAtCutoff, string? StageAtFreeze, bool InCarryOverNarrative);
public sealed record PsurMonthlyView(string Contract, string ReportId, int ReportVersion, string SnapshotHash,
    string ProductId, string[] FrozenMembers, VersionReference? GroupSource, DateOnly Start, DateOnly End,
    string Region, string DateBasis, string TimeZone, DateTimeOffset FrozenAt,
    DateTimeOffset? KnowledgeCutoffExclusiveUtc, string Status, string[] Limitations,
    PsurMonth[]? Months, PsurMonthlyCase[] Cases);

public static class PsurMonthly
{
    public static PsurMonthlyView Read(Psur report, string productId, Actor actor)
    {
        PsurGroupRules.RequireScope(report.Snapshot, actor);
        actor.RequireProduct(productId);
        Guard.Against(Canonical.Hash(report.Snapshot) != report.SnapshotHash, "psur_snapshot_hash",
            "Gespeicherter Snapshot stimmt nicht mit seinem Originalhash ueberein.", 409);
        Guard.Against(PsurGroupRules.Check(report.Snapshot).Length != 0, "psur_group_integrity",
            "Eingefrorene Gruppierung ist nicht konsistent; keine Live-Mitgliedschaft als Ersatz.", 409);
        var families = PsurGroupRules.Snapshots(report.Snapshot);
        var family = families.SingleOrDefault(f => f.ProductId == productId)
            ?? throw new DomainException("psur_monthly_scope", "Produktfamilie gehoert nicht zu diesem eingefrorenen Bericht.", 404);
        var manifest = family.SourceManifest;
        var result = new PsurMonthlyView("cohors-psur-monthly-1", report.Id, report.Version, report.SnapshotHash,
            productId, families.Select(f => f.ProductId).ToArray(), report.Snapshot.Group?.DefinitionSource,
            family.Start, family.End, family.Region, family.DateBasis, "UTC", family.FrozenAt,
            manifest?.KnowledgeCutoffExclusiveUtc, "Unavailable",
            ["Eindeutige synthetische Quellfaelle je Ereignismonat (OccurredAt.UtcDate); keine Inzidenz, Expositionsrate oder Vollstaendigkeitsaussage.",
             "Kenntnis vor dem exklusiven N-Stichtag bestimmt den Basiszaehler. Spaetere Kenntnis bis zum Freeze wird getrennt nach Ereignismonat ausgewiesen, nicht zum Basiszaehler addiert.",
             "Systemeingang ist kein Ereignisdatum. Unbekannte Ereignisdaten und Ereignisse ausserhalb der Periode werden nicht in einen Monat geraten.",
             "Stichtagsstatus und Zustand beim Freeze sind getrennt. Fehlender historischer Status bleibt unbekannt; offene Faelle werden nicht aus dem Ereigniszaehler entfernt.",
             "Mitgliedschaft und Produktscope stammen nur aus diesem Snapshot; keine Addition von Familien oder ueberlappenden Regionen. Randmonate sind auf die Berichtsperiode begrenzt."],
            null, []);
        if (family.Cases is null || family.Periods is null || family.Periods.Any(p => p is null ||
             p.Regions is null || p.Regions.Any(r => r is null)) || family.End.Year == 9999)
             return Unavailable(result, ["Historische Fall-/Periodenstruktur fehlt oder ist nicht unterstuetzt."]);
        var problems = PsurSources.Check(family).Select(f => f.Description).ToList();
        if (problems.Count != 0) return Unavailable(result, problems);
        if (family.DateBasis != "OccurredAt.UtcDate" || family.End < family.Start ||
             family.FrozenAt < PsurSources.EndExclusive(family.End) || !PortfolioRules.Regions.Contains(family.Region, StringComparer.Ordinal))
            problems.Add("Ereignisdatumsbasis, abgeschlossener Zeitraum oder Freeze-Grenze sind nicht reproduzierbar.");
        if (family.Periods.Count(p => p.Label == "N" && p.Start == family.Start && p.End == family.End &&
            p.Regions.Count(r => r.Region == family.Region) == 1) != 1)
            problems.Add("Gebundene N-Periode und Region fehlen oder sind mehrdeutig.");
        if (family.Cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != family.Cases.Length)
            problems.Add("Narrativliste enthaelt mehrere Fassungen desselben Falls.");
        if (manifest is not null && manifest.Cases.Any(c => c.ProductId != family.ProductId ||
            c.Reference.Version < 1 || c.Reference.Hash.Length != 64 || c.FirstAwarenessAt > family.FrozenAt))
            problems.Add("Quellfallversion, Produktzuordnung oder Kenntnis bis zum Freeze sind nicht hinreichend gebunden.");
        if (problems.Count != 0)
            return Unavailable(result, problems);
        var cutoff = manifest!.KnowledgeCutoffExclusiveUtc;
        var rows = manifest.Cases.Where(c => c.Synthetic && PortfolioRules.InRegion(c.Country, c.Jurisdiction, family.Region))
            .OrderBy(c => c.Reference.Id, StringComparer.Ordinal).Select(c =>
            {
                var day = c.OccurredAt is { } occurred ? PortfolioRules.UtcDate(occurred) : (DateOnly?)null;
                var selection = day is null ? "UnknownEventDate" : day < family.Start ? "BeforePeriod" :
                    day > family.End ? "AfterPeriod" : c.FirstAwarenessAt >= cutoff ? "PostCutoff" : "Period";
                var narrative = family.Cases.SingleOrDefault(n => n.Id == c.Reference.Id);
                return new PsurMonthlyCase(c.Reference, c.OccurredAt, c.FirstAwarenessAt, c.SystemReceivedAt,
                    selection, c.FirstAwarenessAt < cutoff ? "Baseline" : "PostCutoff",
                    c.StatusAtCutoff, narrative?.Stage, narrative?.CarryOver == true);
            }).ToArray();
        var months = new List<PsurMonth>();
        for (var month = new DateOnly(family.Start.Year, family.Start.Month, 1); month <= family.End; month = month.AddMonths(1))
        {
            var from = month < family.Start ? family.Start : month;
            var through = month.AddMonths(1).AddDays(-1);
            if (through > family.End) through = family.End;
            var matching = rows.Where(c => c.OccurredAt is { } occurred &&
                PortfolioRules.UtcDate(occurred) >= from && PortfolioRules.UtcDate(occurred) <= through).ToArray();
            var baseline = matching.Where(c => c.Selection == "Period").ToArray();
            months.Add(new(month, from, through, baseline.Select(c => c.Source).ToArray(),
                matching.Where(c => c.Selection == "PostCutoff").Select(c => c.Source).ToArray(),
                baseline.Count(c => Enum.TryParse<CaseStage>(c.StatusAtCutoff, out var stage) && Enum.IsDefined(stage) && stage != CaseStage.Closed),
                baseline.Count(c => !Enum.TryParse<CaseStage>(c.StatusAtCutoff, out var stage) || !Enum.IsDefined(stage))));
        }
        return result with { Status = "Available", Months = months.ToArray(), Cases = rows };
    }

    private static PsurMonthlyView Unavailable(PsurMonthlyView view, IEnumerable<string> problems) =>
        view with { Limitations = [.. problems,
            "Fehlende Grundlage: keine Monatswerte, keine erfundenen Nullzahlen und kein Live-Fallback.", .. view.Limitations] };
}
