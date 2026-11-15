namespace Cohors.Domain;

public sealed record PsurSeriesRequest(string ProductId, string LeadProductId, DateOnly Start, DateOnly End,
    string? PreviousReportId, int? PreviousReportVersion, DateOnly? SubmissionDate,
    string? ScheduleAgreement, string? RevisionReason);
public sealed record PsurPreviousReport(VersionReference Source, int ReportVersion, DateOnly Start, DateOnly End,
    string Status, string? SimulatedReceipt);
public sealed record PsurLeadingPeriod(string ProductId, string BasicUdi, string Reference, DateOnly Start, DateOnly End);
public sealed record PsurSeries(string Contract, string Reference, int ReportVersion, string Kind,
    string GroupRationale, VersionReference GovernanceSource, ProductGovernance Governance,
    PsurPreviousReport? PreviousReport, DateOnly SubmissionDate, string ScheduleAgreement, string RevisionReason);

public static class PsurSeriesRules
{
    public const string Contract = "cohors-psur-series-1";

    public static PsurSeries Bind(PsurSeriesRequest input, ProductGovernance profile, Psur[] reports, PsurGroupDefinition? group = null,
        PsurLeadingPeriod[]? leadingPeriods = null)
    {
        Guard.Against(profile.ProductId != input.ProductId || input.LeadProductId != input.ProductId ||
            !profile.Synthetic || profile.Device.RiskClass != "III" || !profile.Device.Implantable,
            "psur_group", "Fuehrender Scope braucht ein synthetisches Klasse-III-Implantat mit festem Leitprodukt.");
        var reference = group?.Reference ?? profile.PsurReference;
        var leadingReports = reports.Where(p => p.ProductId == input.ProductId ||
            p.Snapshot.Series?.Governance.Device.BasicUdi == profile.Device.BasicUdi).ToArray();
        Guard.Against(leadingReports.Any(p => p.Snapshot.Series is null),
            "psur_series_legacy", "Vorhandene Altberichte haben keine kontrollierte Serienbindung. Keine automatische Ruecknummerierung; gesonderte kontrollierte Uebernahme erforderlich.", 409);
        Guard.Against(leadingReports.Any(p => p.Snapshot.Series!.Reference != reference && p.Snapshot.Start <= input.End && p.Snapshot.End >= input.Start),
            "psur_lead_period_conflict", "Dieselbe Basic-UDI ist in diesem Zeitraum bereits fuehrend in einer anderen Serie. Nichtfuehrende Mitverwendung ist davon getrennt erlaubt.", 409);
        Guard.Against((leadingPeriods ?? []).Any(p => p.BasicUdi == profile.Device.BasicUdi &&
            (p.Reference != reference || p.ProductId != input.ProductId) && p.Start <= input.End && p.End >= input.Start),
            "psur_lead_period_conflict", "Fuehrende Basic-UDI-Verwendung dieses Zeitraums ist bereits gebunden, auch bei abweichender interner Produkt-ID. Keine automatische Neuzuordnung.", 409);
        var previousReports = leadingReports.Where(p => p.Snapshot.Series!.Reference == reference).ToArray();
        Guard.Against(!previousReports.Select(p => p.Snapshot.Series!.ReportVersion).Order().SequenceEqual(Enumerable.Range(1, previousReports.Length)) ||
            previousReports.Any(p => p.Snapshot.Series!.Contract != Contract || p.Snapshot.Series.Reference != reference ||
                p.Snapshot.LeadProductId != input.LeadProductId ||
                p.Snapshot.Series.Governance.Device.BasicUdi != profile.Device.BasicUdi ||
                p.Snapshot.Series.Governance.Device.NotifiedBodyNumber != profile.Device.NotifiedBodyNumber),
            "psur_series_conflict", "Kennung, Leitprodukt, Versionsfolge oder Benannte Stelle widersprechen der bestehenden Serie.", 409);
        var prior = previousReports.OrderByDescending(p => p.Snapshot.Series!.ReportVersion).FirstOrDefault();
        Guard.Against(prior is null
            ? input.PreviousReportId is not null || input.PreviousReportVersion is not null
            : input.PreviousReportId != prior.Id || input.PreviousReportVersion != prior.Version,
            "psur_predecessor", "Die aktuelle Vorfassung muss mit ID und erwarteter Aggregatversion ausdruecklich ausgewaehlt werden.", 409);
        var kind = prior is null ? "Initial" : input.Start == prior.Snapshot.Start ? "Refreeze" : "NextPeriod";
        Guard.Against(prior is null ? input.Start != (group?.FirstPeriodStart ?? profile.FirstPeriodStart) :
            kind == "Refreeze" ? input.End != prior.Snapshot.End :
                input.Start != prior.Snapshot.End.AddDays(1),
            "psur_continuity", "Erste Periode muss dem kontrollierten Zeitplan entsprechen; danach nur dieselbe Periode oder der lueckenlose Anschluss.");
        if (prior is not null) Guard.Text(input.RevisionReason ?? "", "Begruendung der neuen PSUR-Fassung", 40);
        var previous = prior is null ? null : new PsurPreviousReport(new(prior.Id, prior.Version, Canonical.Hash(prior)),
            prior.Snapshot.Series!.ReportVersion, prior.Snapshot.Start, prior.Snapshot.End, prior.Status, prior.NotifiedBodyReceipt);
        var defaultDate = prior is null ? input.Start == profile.FirstPeriodStart ? profile.FirstSubmissionDate : (DateOnly?)null :
            kind == "Refreeze" ? prior.Snapshot.Series!.SubmissionDate : (DateOnly?)null;
        var submissionDate = input.SubmissionDate ?? defaultDate
            ?? throw new DomainException("psur_schedule", "Fuer die Folgeperiode einen gesondert vereinbarten Einreichungstermin angeben; keine pauschale Tagesfrist ableiten.");
        var defaultAgreement = prior is null ? profile.ScheduleAgreement : prior.Snapshot.Series!.ScheduleAgreement;
        Guard.Against((kind == "NextPeriod" || defaultDate is null || submissionDate != defaultDate) && string.IsNullOrWhiteSpace(input.ScheduleAgreement),
            "psur_schedule", "Ein neuer oder veraenderter Einreichungstermin braucht die ausdrueckliche Vereinbarungsreferenz.");
        var agreement = input.ScheduleAgreement ?? defaultAgreement;
        Guard.Text(agreement, "Vereinbarung zum Einreichungstermin", 12);
        Guard.Against(submissionDate <= input.End, "psur_schedule", "Einreichungstermin muss nach dem Berichtsfenster liegen.");
        return new(Contract, reference, (prior?.Snapshot.Series!.ReportVersion ?? 0) + 1, kind,
            group is null ? "Eine Basic-UDI-Produktfamilie; einziges und damit hoechstklassiges Leitprodukt Klasse III, eine gebundene Benannte Stelle. Keine familienuebergreifende Gruppierung." :
                $"Versionierte synthetische Mitgliedschaft {group.Id}@{group.Version}. {group.Rationale} Leitprodukt-Zeitplan: {group.ScheduleBasis} Daten jeder Basic-UDI bleiben separat; keine Ratenaddition.",
            new(profile.Id, profile.Version, Canonical.Hash(profile)), profile, previous, submissionDate, agreement,
            input.RevisionReason ?? "Erster PSUR-Snapshot nach dem kontrollierten synthetischen Produktzeitplan.");
    }

    public static QualityFinding[] Check(PsurSnapshot snapshot)
    {
        var series = snapshot.Series;
        if (series is null || series.Contract != Contract || series.Governance is null || series.GovernanceSource is null ||
            series.Governance.Device is null || series.Reference != (snapshot.Group?.Definition.Reference ?? series.Governance.PsurReference) ||
            series.GovernanceSource != new VersionReference(series.Governance.Id, series.Governance.Version, Canonical.Hash(series.Governance)) ||
            series.Governance.ProductId != snapshot.ProductId || snapshot.LeadProductId != snapshot.ProductId ||
            !series.Governance.Synthetic || series.ReportVersion < 1 || series.SubmissionDate <= snapshot.End ||
            string.IsNullOrWhiteSpace(series.ScheduleAgreement) ||
            (series.PreviousReport is null ? series.Kind != "Initial" || series.ReportVersion != 1 || snapshot.Start != (snapshot.Group?.Definition.FirstPeriodStart ?? series.Governance.FirstPeriodStart) :
                series.ReportVersion != series.PreviousReport.ReportVersion + 1 ||
                (series.Kind == "Refreeze" ? snapshot.Start != series.PreviousReport.Start || snapshot.End != series.PreviousReport.End :
                    series.Kind != "NextPeriod" || snapshot.Start != series.PreviousReport.End.AddDays(1))))
            return [new("psur_series_integrity", "Critical", "Kontrollierte Serien-/Produkt-/Zeitplanbindung fehlt oder ist widerspruechlich.",
                "Neue nachvollziehbare Fassung mit kontrollierter Vorfassung und Vereinbarung anlegen; Altberichte nicht still vervollstaendigen.", null)];
        return [];
    }
}
