using System.IO.Compression;
using System.Text.Json;
using Cohors.Api;
using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Tests;

[Collection("SQL pilot")]
[Trait("Category", "Sql")]
public sealed class SummaryReportingTests : IAsyncLifetime
{
    private readonly SqlFixture fixture = new() { SeedCases = false };
    private const string Reason = "SYNTHETISCH: Quellenfassungen, gemeinsame Ereignisbeschreibung und ausdrueckliche Vereinbarungsgrenzen werden menschlich geprueft; keine reale Risikoakzeptanz oder Einzelpflichtbefreiung.";
    private static readonly SummaryContent Content = new(
        "SYNTHETISCH: Die einzeln referenzierten Revisionshinweise gehoeren zur gleichen Produktfamilie und Jurisdiktion; die je Fall begruendeten Gemeinsamkeiten werden ohne Verlust ihrer Einzelakte dargestellt.",
        "SYNTHETISCH: Die versionierten Risikodokumente und gezielt erfassten Rueckmeldungen werden getrennt gewuerdigt. Der vorliegende Softwaretest beweist keine klinische Ursache oder reale Untersuchungswirksamkeit.",
        "SYNTHETISCH: Unsicherheiten und Gegenhypothesen bleiben nachvollziehbar. Kontrollierte Massnahmen brauchen ihre eigenen menschlichen Entscheidungen; dieses Paket akzeptiert kein Restrisiko.",
        "SYNTHETISCH: Die vereinbarte Falluebersicht, Quellen, Untersuchungsstand und Massnahmen werden explizit abgebildet. Unbekannte UDI und klinische Grenzen bleiben sichtbar, ohne fehlende Angaben zu erfinden.");

    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    [Fact]
    public async Task C12_SeparateSummaryBindsAgreementOriginalCasesAndReviewsWithoutCompletingIndividualObligations()
    {
        var first = await Ready();
        var second = await Ready();
        var service = first.Regulatory;
        var agreement = await service.RecordSummaryAgreement(await AgreementInput(), SqlFixture.Ra, CaseScenario.Key());
        var originals = new[] { first.Case, second.Case };
        var originalAudit = new Dictionary<string, string>();
        foreach (var value in originals) originalAudit[value.Id] = Canonical.Serialize(await fixture.Store.Audit(value.Id));
        var report = await service.DraftSummary(Draft(agreement, originals), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(2, report.Cases.Length);
        Assert.All(report.Cases, c => Assert.Equal(SummaryReportingRules.Bind(c.Case), c.Source));
        var both = SqlFixture.Medical with { Roles = [Roles.Medical, Roles.Regulatory] };
        report = await service.ApproveSummary(report.Id, new(report.Version, Reason), both, CaseScenario.Key());
        Assert.Equal("four_eyes", (await Assert.ThrowsAsync<DomainException>(() =>
            service.ApproveSummary(report.Id, new(report.Version, Reason), both, CaseScenario.Key()))).Code);
        report = await service.ApproveSummary(report.Id, new(report.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        var frozen = report.FrozenJson;
        var send = new VersionCommand(report.Version, Reason);
        var key = CaseScenario.Key();
        report = await service.SimulateSummary(report.Id, send, SqlFixture.Ra, key);
        Assert.Equal("SimulatedAccepted", report.Status);
        Assert.StartsWith("DEMO-SUMMARY-RECEIPT-", report.SimulatedReceipt);
        Assert.Equal(frozen, report.FrozenJson);
        Assert.Equal(Canonical.Serialize(report), Canonical.Serialize(await service.SimulateSummary(report.Id, send, SqlFixture.Ra, key)));
        Assert.Equal("summary_duplicate", (await Assert.ThrowsAsync<DomainException>(() =>
            service.SimulateSummary(report.Id, new(report.Version, Reason), SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal("summary_immutable", (await Assert.ThrowsAsync<DomainException>(() =>
            service.DraftSummary(Draft(agreement, originals) with { ExpectedVersion = report.Version },
                SqlFixture.Pms, CaseScenario.Key()))).Code);
        foreach (var original in originals)
        {
            Assert.Equal(Canonical.Serialize(original), Canonical.Serialize(await fixture.Store.Get<SafetyCase>("case", original.Id)));
            Assert.Equal(originalAudit[original.Id], Canonical.Serialize(await fixture.Store.Audit(original.Id)));
            Assert.All(original.Deadlines, d => Assert.Equal("Open", d.Status));
        }
        var exported = await service.ExportSummary(report.Id, SqlFixture.Ra);
        var package = Canonical.Read<SummaryPackage>(exported.FrozenJson!);
        Assert.Equal(Canonical.Serialize(agreement), Canonical.Serialize(package.Agreement));
        Assert.Contains("Kein MIR, kein PSUR", package.Boundary);
        Assert.Equal(report.ScopeHash, Canonical.Hash(package));
        var archive = await new ProductAuditService(fixture.Store, fixture.Objects, fixture.Policies, first.Clock)
            .Export("ortho", SqlFixture.Ra);
        using var zip = new ZipArchive(new MemoryStream(archive.Bytes));
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        Assert.Contains(manifest.RootElement.GetProperty("aggregates").EnumerateArray(),
            a => a.GetProperty("kind").GetString() == "summaryReport" && a.GetProperty("id").GetString() == report.Id);
        await fixture.Store.Manifest(report.Id, kind: "summaryReport");
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.ExportSummary(report.Id, SqlFixture.Ra with { Products = ["cardio"] }))).Status);
    }

    [Fact]
    public async Task C12_ChangedOrWithdrawnAgreementInvalidatesOpenApprovalsButPreservesHistoricalPackage()
    {
        var scenario = await Ready();
        var service = scenario.Regulatory;
        var input = await AgreementInput();
        var agreement = await service.RecordSummaryAgreement(input, SqlFixture.Ra, CaseScenario.Key());
        var report = await Approved(scenario, agreement);
        var original = report;
        agreement = await service.RecordSummaryAgreement(input with { ExpectedVersion = agreement.Version, Language = "en" },
            SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal("summary_agreement_stale", (await Assert.ThrowsAsync<DomainException>(() =>
            service.SimulateSummary(report.Id, new(report.Version, Reason), SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal(original.FrozenJson, (await service.ExportSummary(report.Id, SqlFixture.Ra)).FrozenJson);
        report = await service.DraftSummary(Draft(agreement, scenario.Case) with { ExpectedVersion = report.Version },
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Empty(report.Approvals);
        Assert.Null(report.FrozenJson);
        Assert.NotEqual(original.ScopeHash, report.ScopeHash);
        report = await service.ApproveSummary(report.Id, new(report.Version, Reason), SqlFixture.Medical, CaseScenario.Key());
        report = await service.ApproveSummary(report.Id, new(report.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        await service.WithdrawSummaryAgreement(agreement.Id, new(agreement.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal("summary_agreement_stale", (await Assert.ThrowsAsync<DomainException>(() =>
            service.SimulateSummary(report.Id, new(report.Version, Reason), SqlFixture.Ra, CaseScenario.Key()))).Code);
        var history = await fixture.Store.History<PeriodicSummaryReport>("summaryReport", ["ortho"]);
        Assert.Contains(history, h => h.Version == original.Version && h.FrozenJson == original.FrozenJson && h.ScopeHash == original.ScopeHash);
    }

    [Fact]
    public async Task C12_CurrentCaseBindingAndUnknownGatePreventUsingAnOlderSummaryAsANewSubmission()
    {
        var scenario = await Ready();
        var service = scenario.Regulatory;
        var agreement = await service.RecordSummaryAgreement(await AgreementInput(), SqlFixture.Ra, CaseScenario.Key());
        var report = await Approved(scenario, agreement);
        scenario.Case = await scenario.Service.Submit(scenario.Case.Id,
            new(scenario.Case.Version, "Unknown", Reason), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal("summary_case_stale", (await Assert.ThrowsAsync<DomainException>(() =>
            service.SimulateSummary(report.Id, new(report.Version, Reason), SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<DomainException>(() =>
            service.DraftSummary(Draft(agreement, scenario.Case) with { ExpectedVersion = report.Version },
                SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Equal(report.FrozenJson, (await service.ExportSummary(report.Id, SqlFixture.Ra)).FrozenJson);
        await File.WriteAllTextAsync(Path.Combine(fixture.ObjectDirectory, agreement.Evidence.Hash), "Controlled corruption in this fixture only.");
        await Assert.ThrowsAsync<DomainException>(() => service.ExportSummary(report.Id, SqlFixture.Ra));
    }

    [Fact]
    public async Task C12_AgreementRequiresCorrectAuthorityFormatPrerequisiteAndExactValidInterval()
    {
        var scenario = await Ready();
        var service = scenario.Regulatory;
        var input = await AgreementInput();
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.RecordSummaryAgreement(input, SqlFixture.Pms, CaseScenario.Key()))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.RecordSummaryAgreement(input, SqlFixture.Ra with { IsHuman = false }, CaseScenario.Key()))).Status);
        Assert.Equal("summary_authority", (await Assert.ThrowsAsync<DomainException>(() =>
            service.RecordSummaryAgreement(input with { AuthorityName = "MHRA-NI-SIMULATOR" }, SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal("summary_agreement_scope", (await Assert.ThrowsAsync<DomainException>(() =>
            service.RecordSummaryAgreement(input with { Format = "PSUR" }, SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal("summary_prerequisite", (await Assert.ThrowsAsync<DomainException>(() =>
            service.RecordSummaryAgreement(input with { Prerequisite = "GroupedOnly" }, SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal("summary_frequency", (await Assert.ThrowsAsync<DomainException>(() =>
            service.RecordSummaryAgreement(input with { FrequencyDays = 0 }, SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal("summary_reporting_window", (await Assert.ThrowsAsync<DomainException>(() =>
            service.RecordSummaryAgreement(input with { FrequencyDays = 1, ValidThrough = input.ValidFrom },
                SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal("summary_reporting_window", (await Assert.ThrowsAsync<DomainException>(() =>
            service.RecordSummaryAgreement(input with { ValidThrough = input.ValidFrom.AddDays(input.FrequencyDays - 1) },
                SqlFixture.Ra, CaseScenario.Key()))).Code);
        var agreement = await service.RecordSummaryAgreement(input, SqlFixture.Ra, CaseScenario.Key());
        var draft = Draft(agreement, scenario.Case);
        Assert.Equal("summary_period", (await Assert.ThrowsAsync<DomainException>(() =>
            service.DraftSummary(draft with { End = draft.End.AddDays(-1) }, SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Equal("summary_period", (await Assert.ThrowsAsync<DomainException>(() =>
            service.DraftSummary(draft with { Start = draft.Start.AddDays(1), End = draft.End.AddDays(1) },
                SqlFixture.Pms, CaseScenario.Key()))).Code);
        var selection = Assert.Single(draft.Cases!);
        Assert.Equal("summary_cases", (await Assert.ThrowsAsync<DomainException>(() =>
            service.DraftSummary(draft with { Cases = [selection, selection] }, SqlFixture.Pms, CaseScenario.Key()))).Code);
        scenario.Clock.Now = new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("summary_agreement_inactive", (await Assert.ThrowsAsync<DomainException>(() =>
            service.DraftSummary(draft, SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Empty(await service.ListSummaries(SqlFixture.Ra));
    }

    private async Task<CaseScenario> Ready()
    {
        var scenario = new CaseScenario(fixture);
        await scenario.Create(); await scenario.Respond(); await scenario.Assess(); await scenario.Prepare(); await scenario.Approve();
        AssessmentRules.RequireReviewable(scenario.Case.CurrentAssessment!, false, null);
        return scenario;
    }

    private async Task<RecordSummaryAgreementCommand> AgreementInput()
    {
        var document = (await fixture.Store.List<Evidence>("evidence", ["ortho"])).Single(e => e.Kind == "RiskFile");
        return new(0, "ortho", "DEMO-SUMMARY-AGREEMENT-" + CaseScenario.Key(), "HPRA-SIMULATOR", "EU-IE", "de",
            SummaryReportingRules.ReportContract, Reason + " Das getrennte Pilotformat, die vier Inhaltsabschnitte und sieben Kalendertage werden ausschliesslich synthetisch vereinbart.",
            Reason + " Gefordert sind eindeutige Fallfassungen, Quellen, Untersuchungsstand, Massnahmen und offengelegte Datenluecken.",
            7, new(2026, 11, 1), new(2026, 11, 30), DateTimeOffset.Parse("2026-10-31T10:00:00Z"),
            "CommonAndWellDocumented", Reason, document.Id, document.Version, Reason);
    }

    private static DraftSummaryCommand Draft(SummaryAgreement agreement, params SafetyCase[] cases) =>
        new(0, agreement.Id, agreement.Version, new(2026, 11, 8), new(2026, 11, 14),
            cases.Select(c => new SummaryCaseSelection(c.Id, c.Version, Reason)).ToArray(), Content, Reason);

    private static async Task<PeriodicSummaryReport> Approved(CaseScenario scenario, SummaryAgreement agreement)
    {
        var report = await scenario.Regulatory.DraftSummary(Draft(agreement, scenario.Case), SqlFixture.Pms, CaseScenario.Key());
        report = await scenario.Regulatory.ApproveSummary(report.Id, new(report.Version, Reason), SqlFixture.Medical, CaseScenario.Key());
        return await scenario.Regulatory.ApproveSummary(report.Id, new(report.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
    }
}

public sealed class SummaryAgreementRuleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(366)]
    public void C12_ValidityIncludesAReportingDayAfterACompleteIntervalWithoutWeakeningExpiry(int frequency)
    {
        var agreement = Agreement(frequency);
        var end = agreement.ValidFrom.AddDays(frequency - 1);
        Assert.Equal("summary_reporting_window", Assert.Throws<DomainException>(() =>
            SummaryReportingRules.RequireAgreement(agreement with { ValidThrough = end })).Code);
        SummaryReportingRules.RequireAgreement(agreement);
        Assert.Equal("summary_period", Assert.Throws<DomainException>(() =>
            SummaryReportingRules.RequirePeriod(agreement, agreement.ValidFrom, end, At(end))).Code);
        SummaryReportingRules.RequirePeriod(agreement, agreement.ValidFrom, end, At(end.AddDays(1)));
        Assert.Equal("summary_agreement_inactive", Assert.Throws<DomainException>(() =>
            SummaryReportingRules.RequirePeriod(agreement, agreement.ValidFrom, end, At(end.AddDays(2)))).Code);
    }

    [Fact]
    public void C12_LateAgreementStillNeedsAnAnchoredIntervalAndItsReportingDay()
    {
        var agreement = Agreement(7) with
        {
            AgreedAt = DateTimeOffset.Parse("2026-01-02T12:00:00Z"),
            RecordedAt = DateTimeOffset.Parse("2026-01-02T12:00:00Z")
        };
        Assert.Equal("summary_reporting_window", Assert.Throws<DomainException>(() =>
            SummaryReportingRules.RequireAgreement(agreement)).Code);
        agreement = agreement with { ValidThrough = new(2026, 1, 15) };
        SummaryReportingRules.RequirePeriod(agreement, new(2026, 1, 8), new(2026, 1, 14), At(new(2026, 1, 15)));
        var error = Assert.Throws<DomainException>(() =>
            SummaryReportingRules.RequirePeriod(agreement, new(2026, 1, 1), new(2026, 1, 7), At(new(2026, 1, 15))));
        Assert.Equal("summary_period", error.Code);
        Assert.Contains("2026-01-08 bis 2026-01-14", error.Message);
    }

    [Fact]
    public void C12_AnIntervalEndingOnExpiryIsNotTheLastReportableInterval()
    {
        var agreement = Agreement(7) with { ValidThrough = new(2026, 1, 14) };
        var lastValidDay = At(agreement.ValidThrough);
        SummaryReportingRules.RequirePeriod(agreement, new(2026, 1, 1), new(2026, 1, 7), lastValidDay);
        var error = Assert.Throws<DomainException>(() =>
            SummaryReportingRules.RequirePeriod(agreement, new(2026, 1, 8), new(2026, 1, 14), lastValidDay));
        Assert.Equal("summary_period", error.Code);
        Assert.Contains("2026-01-01 bis 2026-01-07", error.Message);
    }

    private static DateTimeOffset At(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static SummaryAgreement Agreement(int frequency)
    {
        const string text = "SYNTHETISCH: Ausschliesslich der Eingangsvertrag dieser Sammelvereinbarung wird geprueft. Keine reale Ursache, Behoerdenannahme oder Befreiung von Einzelmeldepflichten wird behauptet.";
        var from = new DateOnly(2026, 1, 1);
        var evidenceAt = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var hash = Canonical.HashText(text);
        var evidence = new Evidence("summary-rule-evidence", 1, "ortho", "Synthetic source", "RiskFile",
            text, hash, "Synthetic", "DEMO-SOURCE", "Synthetic", "Internal", evidenceAt, null, evidenceAt, true, true, hash);
        return new("summary-rule-agreement", 1, "ortho", "DEMO-SUMMARY-RULE",
            new("ortho", "Synthetic implant", "Implant", "DEMO-UDI", "Synthetic test", "DEMO-MODEL", ["EU-IE"]),
            "HPRA-SIMULATOR", new("EU-IE", "synthetic-1", "Europe/Dublin", new(2025, 1, 1), new(2028, 12, 31), [], true),
            SummaryReportingRules.Recipient("EU-IE"), "de", SummaryReportingRules.ReportContract, text, text,
            frequency, from, from.AddDays(frequency), DateTimeOffset.Parse("2025-12-31T12:00:00Z"),
            "CommonAndWellDocumented", text, SummaryReportingRules.Bind(evidence), evidence, "Active", "RA-SYNTHETIC",
            DateTimeOffset.Parse("2026-01-01T12:00:00Z"), true);
    }
}
