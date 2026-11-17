using System.Globalization;

namespace Cohors.Domain;

public sealed record PsurMonthCount(string Month, DateOnly From, DateOnly Through, VersionReference[] Cases,
    VersionReference[] OpenAtCutoff, VersionReference[] UnknownStatusAtCutoff);
public sealed record PsurMonthlyExclusion(string Kind, string Reason, VersionReference[] Cases);
public sealed record PsurMonthlyView(string Contract, string ReportId, int ReportVersion, string SnapshotHash,
    string ProductId, string? BasicUdi, string? MemberSnapshotHash, string Region, DateOnly Start, DateOnly End,
    DateTimeOffset KnowledgeCutoffExclusiveUtc, DateTimeOffset FrozenAt, string DateBasis, string Status,
    string[] Issues, PsurMonthCount[] Months, PsurMonthlyExclusion[] Exclusions, PsurCaseSource[] Sources,
    string Boundary);

public static class PsurMonthlyCounts
{
    public const string Contract = "cohors-psur-months-1";
    public const string Boundary =
        "Abgeleitete Fallzahlen nur aus dem gebundenen Snapshot, keine neue Freigabe und keine klinische Inzidenz. " +
        "Kalendermonate verwenden das Ereignisdatum in UTC und nur den jeweiligen Ausschnitt der Berichtsperiode. " +
        "Erste organisatorische Kenntnis muss vor dem exklusiven Periodenstichtag liegen; Systemeingang ersetzt weder Kenntnis noch Ereignis. " +
        "Freezezeit bindet den Quellenstand, beweist aber keinen historischen Fallstatus. Fehlende Ereignisdaten erhalten kein Ersatzdatum. " +
        "Basiszaehler und Nachtraege bleiben getrennt. Ausschlusskategorien koennen sich ueberschneiden und werden nicht addiert. " +
        "Gruppenmitglieder werden einzeln ausgewiesen; Null ist kein Nachweis klinischer Vollstaendigkeit.";

    public static PsurMonthlyView[] All(Psur report) =>
        (PsurGroupRules.Check(report.Snapshot).Length > 0 ? [report.Snapshot.ProductId] :
            PsurGroupRules.Snapshots(report.Snapshot).Select(s => s.ProductId).ToArray())
        .Select(id => Read(report, id)).ToArray();

    public static PsurMonthlyView Read(Psur report, string productId)
    {
        Guard.Against(!report.Simulated || report.SnapshotHash != Canonical.Hash(report.Snapshot) ||
            report.ScopeHash.Length > 0 && report.ScopeHash != PortfolioRules.PsurScope(report),
            "psur_hash", "Monatssicht stimmt nicht mit dem gebundenen Snapshot/Entwurf ueberein.", 409);
        var groupIssues = PsurGroupRules.Check(report.Snapshot);
        if (groupIssues.Length > 0)
            return Empty(report, report.Snapshot, null) with { ProductId = productId, MemberSnapshotHash = null,
                Issues = groupIssues.Select(f => f.Description).ToArray() };
        var snapshot = PsurGroupRules.Snapshots(report.Snapshot).SingleOrDefault(s => s.ProductId == productId)
            ?? throw new DomainException("psur_month_product", "Produkt ist kein Mitglied dieses eingefrorenen PSUR.", 404);
        var basicUdi = report.Snapshot.Group?.Members.Single(m => m.Product.ProductId == productId).Product.BasicUdi
            ?? snapshot.Series?.Governance.Device.BasicUdi;
        var result = Empty(report, snapshot, basicUdi);
        var issues = PsurSources.Check(snapshot).Select(f => f.Description).ToList();
        if (snapshot.DateBasis != "OccurredAt.UtcDate") issues.Add("Historische Datumsbasis wird nicht in eine neue Monatslogik umgedeutet.");
        var n = snapshot.Periods.Where(p => p.Label == "N").ToArray();
        if (n.Length != 1 || n[0].Start != snapshot.Start || n[0].End != snapshot.End ||
            n[0].Regions.Count(r => r.Region == snapshot.Region) != 1)
            issues.Add("N/Region ist nicht eindeutig an das ausgewaehlte Berichtsfenster gebunden.");
        if (issues.Count > 0) return result with { Issues = issues.ToArray() };
        var manifest = snapshot.SourceManifest!;
        var sources = manifest.Cases.Where(c => c.Synthetic && c.ProductId == productId &&
            PortfolioRules.InRegion(c.Country, c.Jurisdiction, snapshot.Region))
            .OrderBy(c => c.Reference.Id, StringComparer.Ordinal).ToArray();
        var numerator = n[0].Regions.Single(r => r.Region == snapshot.Region).NumeratorSources!.ToHashSet();
        var counted = sources.Where(c => numerator.Contains(c.Reference)).ToArray();
        var months = new List<PsurMonthCount>();
        var lastMonth = new DateOnly(snapshot.End.Year, snapshot.End.Month, 1);
        for (var month = new DateOnly(snapshot.Start.Year, snapshot.Start.Month, 1);; month = month.AddMonths(1))
        {
            var from = month < snapshot.Start ? snapshot.Start : month;
            var monthEnd = new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
            var through = monthEnd > snapshot.End ? snapshot.End : monthEnd;
            var entries = counted.Where(c => c.OccurredAt is { } occurred &&
                PortfolioRules.UtcDate(occurred) >= from && PortfolioRules.UtcDate(occurred) <= through).ToArray();
            bool KnownStatus(PsurCaseSource c) => c.CutoffVersion is not null &&
                Enum.TryParse<CaseStage>(c.StatusAtCutoff, out var stage) && Enum.IsDefined(stage);
            months.Add(new(month.ToString("yyyy-MM", CultureInfo.InvariantCulture), from, through,
                entries.Select(c => c.Reference).ToArray(),
                entries.Where(c => KnownStatus(c) && c.StatusAtCutoff != nameof(CaseStage.Closed)).Select(c => c.Reference).ToArray(),
                entries.Where(c => !KnownStatus(c)).Select(c => c.Reference).ToArray()));
            if (month == lastMonth) break;
        }
        Guard.Against(months.Sum(m => m.Cases.Length) != numerator.Count, "psur_month_numerator",
            "Monate rekonstruieren den gebundenen eindeutigen N-Zaehler nicht vollstaendig.", 409);
        VersionReference[] Select(Func<PsurCaseSource, bool> predicate) => sources.Where(predicate).Select(c => c.Reference).ToArray();
        var carry = snapshot.Cases.Where(c => c.CarryOver).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var addenda = manifest.PostCutoffCases.Where(a => a.Period == "N").Select(a => a.Source).ToHashSet();
        return result with { Status = "Available", Months = months.ToArray(), Sources = sources, Exclusions = [
            new("UnknownEventDate", "Ereigniszeit unbekannt: keine Monatszuordnung. Kenntnis und Systemeingang sind kein Ersatzdatum.",
                Select(c => c.OccurredAt is null)),
            new("CarryOver", "Im eingefrorenen Narrativ als Carry-over gebunden, nicht im N-Ereigniszaehler. Kann unbekannte Ereignisdaten einschliessen.",
                Select(c => carry.Contains(c.Reference.Id))),
            new("PostCutoffAddendum", "N-Ereignis erst ab dem exklusiven Stichtag bis zum Freeze bekannt: gesonderter Nachtrag, nie zum Basiszaehler addiert.",
                Select(c => addenda.Contains(c.Reference))),
            new("OutsidePeriod", "Bekanntes Ereignisdatum ausserhalb N; nicht im dargestellten Monatszaehler, gegebenenfalls in einer Vergleichsperiode.",
                Select(c => c.OccurredAt is { } occurred &&
                    (PortfolioRules.UtcDate(occurred) < snapshot.Start || PortfolioRules.UtcDate(occurred) > snapshot.End)))
        ] };
    }

    private static PsurMonthlyView Empty(Psur report, PsurSnapshot snapshot, string? basicUdi)
    {
        Guard.Against(snapshot.Start.Year == 9999 || snapshot.End == DateOnly.MaxValue ||
            snapshot.End != snapshot.Start.AddYears(1).AddDays(-1), "psur_month_period",
            "Monatsansicht erfordert das unveraenderte zusammenhaengende Zwoelfmonatsfenster.", 409);
        return new(Contract, report.Id, report.Version, report.SnapshotHash, snapshot.ProductId, basicUdi,
            Canonical.Hash(snapshot), snapshot.Region, snapshot.Start, snapshot.End, PsurSources.EndExclusive(snapshot.End),
            snapshot.FrozenAt, snapshot.DateBasis, "Unavailable", [], [], [], [], Boundary);
    }
}
