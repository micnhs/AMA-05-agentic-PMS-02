using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohors.Agents;
using Cohors.Api;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

[Collection("SQL pilot")]
[Trait("Category", "Sql")]
public sealed class ImportTests : IAsyncLifetime
{
    private const string Product = "ortho";
    private const string IntakeFile = "ortho-intake-distributor-2026-11.csv";
    private const string DistributionFile = "ortho-distribution-2026-q3.csv";
    private const string ExposureId = "exposure-ortho-Worldwide-20260701-20260930";
    private const string ImportReason = "Synthetischer Kanal- und Lieferimport fuer den Pilotnachweis; keine Echtdaten.";
    private const string ApprovalReason = "Lieferdatei, Region Worldwide und abgeschlossenes Q3 menschlich geprueft; synthetischer Nenner.";
    private const string DuplicateAlert = "Doppelter Eingang: ein Vorfall, beide Originalquellen erhalten. Abgeschlossene Pflicht bleibt erledigt; neue Information menschlich triagieren.";
    private const string UdiAlert = "UDI unbekannt; Produktzuordnung ist zu bestaetigen.";
    private static readonly DateOnly Q3Start = new(2026, 7, 1);
    private static readonly DateOnly Q3End = new(2026, 9, 30);
    private const string EeaRegion = "EEA-Turkey-NI";
    private static readonly string EeaId = ImportRules.ExposureId(Product, EeaRegion, Q3Start, Q3End);
    private static readonly Actor CardioPms = new("test-pms-cardio", "PMS cardio test", [Roles.Pms], ["cardio"], true);
    private static readonly Actor Auditor = new("audit-reader", "Synthetic auditor", [Roles.Auditor], ["ortho", "cardio"], true);
    private static readonly string Home = FindHome();
    // Own seeded database per test: DEMO-07 must exist and audit exports must not see data of other test classes.
    private readonly SqlFixture fixture = new();
    private readonly TestClock clock = new(DateTimeOffset.Parse("2026-11-19T12:00:00Z"));

    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    [Fact]
    [Trait("Category", "OperationsSqlBoundary")]
    public async Task ChannelBatchRowsAreRegisteredLinkedOrRejectedAndKeepStatutoryDeadlinesUnderTheIndependentWatchdog()
    {
        var demo = CaseIdentity.For(Product, "DEMO-07");
        var first = CaseIdentity.For(Product, "IMP-ORTHO-01");
        var serious = CaseIdentity.For(Product, "IMP-ORTHO-04");
        var before = await Case(demo);
        var content = Shipped(IntakeFile);
        var command = IntakeCommand(content);
        var key = CaseScenario.Key();

        var import = await Imports().ImportIntake(command, SqlFixture.Pms, key);

        Assert.Equal(IntakeImportStatus.Processed, import.Status);
        Assert.Equal(2, import.Version);
        Assert.Equal(Canonical.HashText(content), import.Original.Hash);
        Assert.Equal(new IntakeImportCounts(6, 2, 2, 2, 0, 0), import.Counts);
        Assert.Equal(
            new[]
            {
                "2:Registered:registered", "3:Duplicate:row_duplicate", "4:Duplicate:case_known",
                "5:Rejected:row_awareness_missing", "6:Rejected:row_jurisdiction_country", "7:Registered:registered"
            },
            import.Rows.Select(r => $"{r.Line}:{r.Outcome}:{r.Code}"));
        Assert.All(import.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
        var rows = import.Rows.ToDictionary(r => r.Line);
        Assert.Equal(first, rows[2].Case!.Id);
        Assert.Equal("SyntheticDistributorFeedback:HF-2026-1101", rows[2].SourceId);
        Assert.Equal(2, rows[3].DuplicateOf);
        Assert.Equal(rows[2].Case, rows[3].Case);
        Assert.Equal(IntakeKnown.Case, rows[4].Known);
        Assert.Equal(demo, rows[4].Case!.Id);
        Assert.Null(rows[5].Case);
        Assert.Null(rows[6].Case);
        Assert.Equal(serious, rows[7].Case!.Id);

        var cases = new[]
        {
            (Id: first, Jurisdiction: "EU-IE", Seriousness: Seriousness.OtherSerious, Awareness: "2026-11-03T08:30:00Z",
                Legal: "2026-11-18T23:59:59.9999999Z", Safety: "2026-11-18T11:59:59.9999999Z", Source: "SyntheticDistributorFeedback:HF-2026-1101"),
            (Id: serious, Jurisdiction: "NI", Seriousness: Seriousness.DeathOrSeriousDeterioration, Awareness: "2026-11-09T09:00:00Z",
                Legal: "2026-11-19T23:59:59.9999999Z", Safety: "2026-11-19T11:59:59.9999999Z", Source: "SyntheticDistributorFeedback:HF-2026-1105")
        };
        foreach (var expected in cases)
        {
            var registered = await Case(expected.Id);
            Assert.Equal(CaseStage.Intake, registered.Stage);
            Assert.Equal("channel-import", registered.Scenario);
            Assert.Contains(registered.Sources, s => s.ExternalId == expected.Source);
            var deadline = Assert.Single(registered.Deadlines);
            var awareness = DateTimeOffset.Parse(expected.Awareness);
            var calculated = DeadlineCalculator.Calculate(expected.Jurisdiction, expected.Seriousness, awareness,
                fixture.Policies.Calendar(expected.Jurisdiction));
            Assert.Equal((expected.Jurisdiction, expected.Seriousness, awareness, "Open"),
                (deadline.Jurisdiction, deadline.Seriousness, deadline.TriggerAt, deadline.Status));
            Assert.Equal((calculated.LegalDue, calculated.SafetyDue), (deadline.LegalDue, deadline.SafetyDue));
            Assert.Equal((DateTimeOffset.Parse(expected.Legal), DateTimeOffset.Parse(expected.Safety)), (deadline.LegalDue, deadline.SafetyDue));
            Assert.Equal((deadline.Id, deadline.LegalDue, deadline.SafetyDue, "Open"), Assert.Single(await Projection(expected.Id)));
        }
        // The original awareness date stays authoritative: a late batch import cannot move the statutory due date.
        Assert.True((await Case(first)).Deadlines[0].LegalDue < clock.GetUtcNow());
        Assert.Contains(UdiAlert, (await Case(serious)).Alerts);

        var after = await Case(demo);
        Assert.DoesNotContain(before.Sources, s => s.ExternalId == "SyntheticDistributorFeedback:HF-2026-1102");
        Assert.Contains(after.Sources, s => s.ExternalId == "SyntheticDistributorFeedback:HF-2026-1102");
        Assert.Equal(Tuples(before), Tuples(after));
        Assert.Contains(DuplicateAlert, after.Alerts);
        Assert.Equal(before.Stage == CaseStage.Closed ? CaseStage.Closed : CaseStage.AwaitingMedicalReview, after.Stage);

        var replay = await Imports().ImportIntake(command, SqlFixture.Pms, key);
        Assert.Equal((import.Id, import.Version), (replay.Id, replay.Version));
        Assert.Equal("import_duplicate_file", await Fails(() => Imports().ImportIntake(command, SqlFixture.Pms, CaseScenario.Key())));
        Assert.Equal("product_scope", await Fails(() => Imports().ImportIntake(command, CardioPms, CaseScenario.Key())));
        Assert.Equal("forbidden", await Fails(() => Imports().ImportIntake(command, SqlFixture.Quality, CaseScenario.Key())));
        Assert.Equal("synthetic_only", await Fails(() => Imports().ImportIntake(command with { Synthetic = false }, SqlFixture.Pms, CaseScenario.Key())));
        Assert.Equal(new[] { "IntakeImportReceived", "IntakeImportProcessed" }, (await fixture.Store.Audit(import.Id)).Select(e => e.Action));
        var manifest = await fixture.Store.Manifest(import.Id, kind: "intakeImport");
        Assert.True(manifest.HashChainVerified);
        Assert.Equal(2, manifest.CurrentVersion);

        var urgent = Assert.Single((await Case(serious)).Deadlines);
        var legal = Assert.Single(await CalendarEvents("LegalDue"), e => e.ObligationId == $"case:{serious}:deadline:{urgent.Id}");
        Assert.Equal(urgent.LegalDue, legal.DueAt);
        Assert.True(legal.IsOpen);

        // Independent watchdog with the restricted Operations SQL role, exactly as for existing case deadlines.
        var restricted = await fixture.RestrictedOperations();
        await restricted.RequireOperationsReady();
        await new OperationsService(restricted, restricted, clock, restricted).Tick();
        var overdue = await Case(first);
        Assert.Contains(Legal(overdue.Deadlines[0]), overdue.Alerts);
        Assert.Contains(Safety(overdue.Deadlines[0]), overdue.Alerts);
        var pending = await Case(serious);
        Assert.Contains(Safety(urgent), pending.Alerts);
        Assert.DoesNotContain(Legal(urgent), pending.Alerts);

        clock.Now = urgent.LegalDue.AddMinutes(1);
        await new OperationsService(restricted, restricted, clock, restricted).Tick();
        var escalated = await Case(serious);
        Assert.Contains(Legal(urgent), escalated.Alerts);
        Assert.Equal((urgent.LegalDue, urgent.SafetyDue), (escalated.Deadlines[0].LegalDue, escalated.Deadlines[0].SafetyDue));
    }

    [Fact]
    public async Task DeliveryDenominatorNeedsSeparateHumanApprovalAndBoundTrendsKeepTheirExposureVersion()
    {
        var content = Shipped(DistributionFile);
        var delivery = await Imports().ImportDistribution(DistributionCommand(content), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(DistributionImportStatus.Validated, delivery.Status);
        Assert.Empty(delivery.Findings);
        Assert.Equal(new DistributionTotals(7, 7, 0, 225, 186, 181, 5), delivery.Totals);

        var preview = await Imports().Proposal(delivery.Id, "Worldwide", Q3Start, Q3End, SqlFixture.Quality);
        Assert.Equal((ExposureId, 0, false), (preview.TargetExposureId, preview.TargetVersion, preview.Unchanged));
        Assert.Null(preview.CurrentCount);
        Assert.Equal((178, 173, 5), (preview.Proposal.Count, preview.Proposal.Recipients, preview.Proposal.AdditionalImplants));
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7 }, preview.Proposal.Lines);
        Assert.Null(await fixture.Store.Get<Exposure>("exposure", ExposureId));

        var approval = Approval(delivery.Id, 178, 0);
        Assert.Equal("forbidden", await Fails(() => Imports().ApproveDenominator(approval, SqlFixture.Pms, CaseScenario.Key())));
        var own = await Imports().ImportDistribution(
            DistributionCommand(content.Replace("2026-07-15,60,52,50", "2026-07-15,60,51,50"), "ortho-distribution-2026-q3-quality.csv"),
            SqlFixture.Quality, CaseScenario.Key());
        Assert.Equal("four_eyes", await Fails(() => Imports().ApproveDenominator(Approval(own.Id, 177, 0), SqlFixture.Quality, CaseScenario.Key())));
        Assert.Null(await fixture.Store.Get<Exposure>("exposure", ExposureId));

        var key = CaseScenario.Key();
        var approved = await Imports().ApproveDenominator(approval, SqlFixture.Quality, key);
        Assert.Equal((ExposureId, 1, 178, true, "Implants"), (approved.Id, approved.Version, approved.Count, approved.Approved, approved.Unit));
        Assert.Equal((delivery.Id, 1), (approved.Source!.Import.Id, approved.Source.Import.Version));
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7 }, approved.Source.Lines);
        Assert.Equal((173, 5, "test-quality", preview.Proposal.Hash),
            (approved.Source.Recipients, approved.Source.AdditionalImplants, approved.Source.ApprovedBy, approved.Source.ProposalHash));
        var replay = await Imports().ApproveDenominator(approval, SqlFixture.Quality, key);
        Assert.Equal((approved.Id, approved.Version), (replay.Id, replay.Version));
        Assert.Equal("denominator_unchanged", await Fails(() => Imports().ApproveDenominator(approval with { ExpectedVersion = 1 }, SqlFixture.Quality, CaseScenario.Key())));
        Assert.Equal("denominator_changed", await Fails(() => Imports().ApproveDenominator(
            approval with { ExpectedVersion = 1, ExpectedCount = 177 }, SqlFixture.Quality, CaseScenario.Key())));

        var trend = new RunTrendCommand(Product, "Worldwide", Q3Start, Q3End, ExposureId);
        var bound = await Analytics().Trend(trend, SqlFixture.Quality, CaseScenario.Key());
        Assert.Equal((ExposureId, 1, 178, 178), (bound.Input.Exposure.Id, bound.Input.Exposure.Version, bound.Input.Exposure.Count, bound.Result.Exposure));
        Assert.Equal(Canonical.Hash(bound.Input), bound.InputHash);
        Assert.Equal("Candidate", bound.Status);

        var corrected = await Imports().ImportDistribution(
            DistributionCommand(content.Replace("2026-07-08,40,36,35", "2026-07-08,40,35,35"), "ortho-distribution-2026-q3-korrigiert.csv"),
            SqlFixture.Pms, CaseScenario.Key());
        var replaced = await Imports().ApproveDenominator(Approval(corrected.Id, 177, 1), SqlFixture.Quality, CaseScenario.Key());
        Assert.Equal((ExposureId, 2, 177), (replaced.Id, replaced.Version, replaced.Count));
        Assert.Equal((corrected.Id, 173, 4), (replaced.Source!.Import.Id, replaced.Source.Recipients, replaced.Source.AdditionalImplants));

        // No silent recalculation: the stored trend keeps the denominator version it was computed with.
        var stored = (await fixture.Store.Get<TrendRun>("trend", bound.Id))!;
        Assert.Equal((1, 178, 178, bound.InputHash),
            (stored.Input.Exposure.Version, stored.Input.Exposure.Count, stored.Result.Exposure, stored.InputHash));
        var exposureManifest = await fixture.Store.Manifest(ExposureId, kind: "exposure");
        Assert.True(exposureManifest.HashChainVerified);
        Assert.Equal(2, exposureManifest.CurrentVersion);
    }

    [Fact]
    public async Task ProductAuditExportCarriesImportOriginalsAndTheApprovedDenominator()
    {
        var intakeContent = Shipped(IntakeFile);
        var deliveryContent = Shipped(DistributionFile);
        var intake = await Imports().ImportIntake(IntakeCommand(intakeContent), SqlFixture.Pms, CaseScenario.Key());
        var delivery = await Imports().ImportDistribution(DistributionCommand(deliveryContent), SqlFixture.Pms, CaseScenario.Key());
        var exposure = await Imports().ApproveDenominator(Approval(delivery.Id, 178, 0), SqlFixture.Quality, CaseScenario.Key());

        var file = await new ProductAuditService(fixture.Store, fixture.Objects, fixture.Policies, clock).Export(Product, Auditor);
        using var archive = new ZipArchive(new MemoryStream(file.Bytes), ZipArchiveMode.Read);
        using var manifestStream = (archive.GetEntry("manifest.json") ?? throw new InvalidOperationException("manifest.json fehlt.")).Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var aggregates = manifest.RootElement.GetProperty("aggregates").EnumerateArray().ToArray();
        AssertAggregate(archive, aggregates, "intakeImport", intake.Id, 2);
        AssertAggregate(archive, aggregates, "distributionImport", delivery.Id, 1);
        AssertAggregate(archive, aggregates, "exposure", exposure.Id, 1);
        var objects = manifest.RootElement.GetProperty("objects").EnumerateArray().ToArray();
        foreach (var (original, content) in new[] { (intake.Original, intakeContent), (delivery.Original, deliveryContent) })
        {
            var entry = Assert.Single(objects, o => o.GetProperty("hash").GetString() == original.Hash);
            var bytes = Read(archive, entry.GetProperty("path").GetString()!);
            Assert.Equal(original.Hash, Hash(bytes));
            Assert.Equal(Encoding.UTF8.GetBytes(content), bytes);
        }
    }

    [Fact]
    public async Task SameChannelSourceWithEarlierAwarenessIsHeldForHumanCorrectionWhileAnIdenticalRepeatStaysADuplicate()
    {
        var id = CaseIdentity.For(Product, "IMP-R01-1");
        var recorded = R01Row("2026-11-10T08:00:00Z");
        var first = await Imports().ImportIntake(IntakeCommand(R01File(recorded), "ortho-intake-r01-erstmeldung.csv"),
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(new[] { "2:Registered:registered" }, first.Rows.Select(r => $"{r.Line}:{r.Outcome}:{r.Code}"));
        var registered = await Case(id);
        var source = Assert.Single(registered.Sources);
        var original = Assert.Single(registered.Deadlines);
        Assert.Equal(DateTimeOffset.Parse("2026-11-10T08:00:00Z"), registered.FirstAwarenessAt);

        // Same channel source and identical text, but an earlier first awareness: never an unchanged duplicate.
        var earlier = R01Row("2026-11-01T08:00:00Z");
        var held = await Imports().ImportIntake(IntakeCommand(R01File(earlier, earlier), "ortho-intake-r01-fruehere-kenntnis.csv"),
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(IntakeImportStatus.Processed, held.Status);
        Assert.Equal(new IntakeImportCounts(2, 0, 0, 0, 0, 2), held.Counts);
        Assert.Equal(new[] { "2:CorrectionRequired:source_changed", "3:CorrectionRequired:source_changed" },
            held.Rows.Select(r => $"{r.Line}:{r.Outcome}:{r.Code}"));
        Assert.All(held.Rows, r =>
        {
            Assert.Equal((id, registered.Version), (r.Case!.Id, r.Case.Version));
            Assert.Contains("erste Kenntnis 2026-11-01T08:00:00Z statt erfasst 2026-11-10T08:00:00Z", r.Reason, StringComparison.Ordinal);
            Assert.Contains("zur menschlichen Korrektur zurueckgestellt", r.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("Vorfall unveraendert", r.Reason, StringComparison.Ordinal);
        });
        Assert.Equal(2, held.Rows[1].DuplicateOf);
        Assert.Equal(new[] { "IntakeImportReceived", "IntakeImportProcessed" }, (await fixture.Store.Audit(held.Id)).Select(e => e.Action));
        // The import corrects nothing: case, source, awareness and deadline stay exactly as recorded.
        Assert.Equal(Canonical.Hash(registered), Canonical.Hash(await Case(id)));

        var repeat = await Imports().ImportIntake(IntakeCommand(R01File(recorded, recorded), "ortho-intake-r01-wiederholung.csv"),
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(new IntakeImportCounts(2, 0, 2, 0, 0, 0), repeat.Counts);
        Assert.Equal(new[] { "2:Duplicate:source_known", "3:Duplicate:row_duplicate" }, repeat.Rows.Select(r => $"{r.Line}:{r.Outcome}:{r.Code}"));
        Assert.All(repeat.Rows, r => Assert.Equal((id, registered.Version), (r.Case!.Id, r.Case.Version)));
        Assert.Equal(Canonical.Hash(registered), Canonical.Hash(await Case(id)));

        // Only Medical or RA correct the earlier awareness, through the existing case path; the PMS importer cannot.
        var at = DateTimeOffset.Parse("2026-11-01T08:00:00Z");
        var correction = new AwarenessCommand(registered.Version, at, held.Original.FileName,
            "Zurueckgestellte Importzeile geprueft: fruehere Organisationskenntnis laut synthetischer Kanaldatei belegt.", false);
        Assert.Equal("forbidden", await Fails(() => fixture.Service(clock).Awareness(id, correction, SqlFixture.Pms, CaseScenario.Key())));
        var corrected = await fixture.Service(clock).Awareness(id, correction, SqlFixture.Medical, CaseScenario.Key());
        var calculated = DeadlineCalculator.Calculate("EU-IE", Seriousness.OtherSerious, at, fixture.Policies.Calendar("EU-IE"));
        var deadline = Assert.Single(corrected.Deadlines);
        Assert.Equal((at, original.Id, at, calculated.LegalDue, calculated.SafetyDue),
            (corrected.FirstAwarenessAt, deadline.Id, deadline.TriggerAt, deadline.LegalDue, deadline.SafetyDue));
        Assert.True(deadline.LegalDue < original.LegalDue);
        Assert.Equal(source, Assert.Single(corrected.Sources));
    }

    [Fact]
    public async Task InterruptedImportRowBindsItsRegisteredSourceSoAChangedSeverityIsHeldAndTheResumeStaysIdempotent()
    {
        var id = CaseIdentity.For(Product, "IMP-R02-1");
        var recorded = R02Row(1, "OtherSerious");
        var interrupted = IntakeCommand(R01File(recorded), "ortho-intake-r02-unterbrochen.csv");
        var key = CaseScenario.Key();
        // Stops after the case registration and before the import records its row results.
        var stopped = new HookStore(fixture.Store).BeforeExecute("IntakeImportProcessed",
            () => throw new InvalidOperationException("Simulierte Unterbrechung nach der Fallregistrierung."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Imports(stopped).ImportIntake(interrupted, SqlFixture.Pms, key));
        var registered = await Case(id);
        Assert.Equal((1, Seriousness.OtherSerious), (registered.Version, registered.Seriousness));
        var open = Assert.Single(await fixture.Store.List<IntakeImport>(ImportService.IntakeKind, [Product]),
            i => i.Original.FileName == interrupted.FileName);
        Assert.Equal((IntakeImportStatus.Received, 1), (open.Status, open.Version));
        var pending = Assert.Single(open.Rows);
        Assert.Equal(IntakeRowOutcome.Pending, pending.Outcome);
        Assert.Null(pending.Case);

        // Same source, text and times but a more serious classification: the open row binds the registered facts.
        var serious = await Imports().ImportIntake(IntakeCommand(R01File(R02Row(1, "DeathOrSeriousDeterioration")),
            "ortho-intake-r02-schwerer.csv"), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal((IntakeImportStatus.Processed, new IntakeImportCounts(1, 0, 0, 0, 0, 1)), (serious.Status, serious.Counts));
        var held = Assert.Single(serious.Rows);
        Assert.Equal(("2:CorrectionRequired:source_changed", IntakeKnown.Source, id, 1),
            ($"{held.Line}:{held.Outcome}:{held.Code}", held.Known, held.Case!.Id, held.Case.Version));
        Assert.Contains("noch nicht abgeschlossenen Zeile 2 aus ortho-intake-r02-unterbrochen.csv", held.Reason, StringComparison.Ordinal);
        Assert.Contains("zur menschlichen Korrektur zurueckgestellt", held.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Vorfall unveraendert", held.Reason, StringComparison.Ordinal);
        Assert.Equal(Canonical.Hash(registered), Canonical.Hash(await Case(id)));

        // The interrupted import resumes with its own key and settles its own registration exactly once.
        var resumed = await Imports().ImportIntake(interrupted, SqlFixture.Pms, key);
        Assert.Equal((open.Id, 2, IntakeImportStatus.Processed), (resumed.Id, resumed.Version, resumed.Status));
        Assert.Equal(new[] { "2:Registered:registered" }, resumed.Rows.Select(r => $"{r.Line}:{r.Outcome}:{r.Code}"));
        Assert.Equal((id, 1), (resumed.Rows[0].Case!.Id, resumed.Rows[0].Case!.Version));
        Assert.Equal(new[] { "IntakeImportReceived", "IntakeImportProcessed" }, (await fixture.Store.Audit(open.Id)).Select(e => e.Action));
        Assert.Single(await fixture.Store.Audit(id), e => e.Action == "ComplaintRegistered");
        Assert.Equal(Canonical.Hash(registered), Canonical.Hash(await Case(id)));

        var repeat = await Imports().ImportIntake(IntakeCommand(R01File(recorded, recorded), "ortho-intake-r02-wiederholung.csv"),
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(new IntakeImportCounts(2, 0, 2, 0, 0, 0), repeat.Counts);
        Assert.Equal(new[] { "2:Duplicate:source_known", "3:Duplicate:row_duplicate" }, repeat.Rows.Select(r => $"{r.Line}:{r.Outcome}:{r.Code}"));
        Assert.All(repeat.Rows, r => Assert.Equal((id, 1), (r.Case!.Id, r.Case.Version)));
        Assert.Equal(Canonical.Hash(registered), Canonical.Hash(await Case(id)));
    }

    [Fact]
    public async Task InterleavedImportsOfOneChannelSourceHoldDifferingRowsAndRegisterIdenticalRowsOnce()
    {
        // A runs completely while B, already received, is about to register the same source with other facts.
        var conflictId = CaseIdentity.For(Product, "IMP-R02-2");
        IntakeImport? inner = null;
        var window = new HookStore(fixture.Store).BeforeGet("case", conflictId, async () => inner = await Imports().ImportIntake(
            IntakeCommand(R01File(R02Row(2, "DeathOrSeriousDeterioration")), "ortho-intake-r02-parallel-a.csv"),
            SqlFixture.Pms, CaseScenario.Key()));
        var outer = await Imports(window).ImportIntake(IntakeCommand(R01File(R02Row(2, "OtherSerious")), "ortho-intake-r02-parallel-b.csv"),
            SqlFixture.Pms, CaseScenario.Key());
        var parallel = inner ?? throw new InvalidOperationException("Paralleler Import lief nicht im Registrierungsfenster.");
        Assert.Equal((IntakeImportStatus.Processed, new IntakeImportCounts(1, 0, 0, 0, 0, 1)), (parallel.Status, parallel.Counts));
        var held = Assert.Single(parallel.Rows);
        Assert.Equal(("2:CorrectionRequired:source_changed", IntakeKnown.None), ($"{held.Line}:{held.Outcome}:{held.Code}", held.Known));
        Assert.Null(held.Case);
        Assert.Contains("noch nicht abgeschlossenen Zeile 2 aus ortho-intake-r02-parallel-b.csv", held.Reason, StringComparison.Ordinal);
        Assert.Contains("Kein Vorfall angelegt und keine Quelle uebernommen", held.Reason, StringComparison.Ordinal);
        Assert.Equal(new[] { "2:Registered:registered" }, outer.Rows.Select(r => $"{r.Line}:{r.Outcome}:{r.Code}"));
        var conflict = await Case(conflictId);
        Assert.Equal((1, Seriousness.OtherSerious), (conflict.Version, conflict.Seriousness));
        Assert.Single(conflict.Sources);
        Assert.Single(await fixture.Store.Audit(conflictId), e => e.Action == "ComplaintRegistered");

        // Identical rows: A registers inside B's registration window; B then links as an unchanged duplicate.
        var identicalId = CaseIdentity.For(Product, "IMP-R02-3");
        var same = R02Row(3, "OtherSerious");
        IntakeImport? first = null;
        var gate = new HookStore(fixture.Store).BeforeGet("case", identicalId, async () => first = await Imports().ImportIntake(
            IntakeCommand(R01File(same, same), "ortho-intake-r02-gleich-a.csv"), SqlFixture.Pms, CaseScenario.Key()));
        var late = await Imports(gate).ImportIntake(IntakeCommand(R01File(same), "ortho-intake-r02-gleich-b.csv"),
            SqlFixture.Pms, CaseScenario.Key());
        var registering = first ?? throw new InvalidOperationException("Identischer Import lief nicht im Registrierungsfenster.");
        Assert.Equal(new[] { "2:Registered:registered", "3:Duplicate:row_duplicate" },
            registering.Rows.Select(r => $"{r.Line}:{r.Outcome}:{r.Code}"));
        Assert.Equal(new[] { "2:Duplicate:source_known" }, late.Rows.Select(r => $"{r.Line}:{r.Outcome}:{r.Code}"));
        Assert.Equal((identicalId, 1), (late.Rows[0].Case!.Id, late.Rows[0].Case!.Version));
        Assert.Single(await fixture.Store.Audit(identicalId), e => e.Action == "ComplaintRegistered");
    }

    [Fact]
    public async Task ChannelRowIsComparedWithTheFirstRegistrationOfItsSourceAfterAMedicalReclassification()
    {
        var id = CaseIdentity.For(Product, "IMP-R02-4");
        var recorded = R02Row(4, "OtherSerious");
        // Registered outside any import, so no import row records its facts; Medical then reclassifies the case.
        var candidate = Assert.Single(ImportRules.EvaluateIntake(SyntheticCsv.Parse(R01File(recorded), ImportRules.IntakeHeader,
            ImportRules.MaximumIntakeRows), fixture.Policies.Device(Product), IntakeImportChannel.SyntheticDistributorFeedback,
            clock.GetUtcNow()).Candidates);
        var registered = await fixture.Service(clock).Register(new RegisterComplaint(candidate.IncidentKey, candidate.SourceId,
            candidate.Title, candidate.Description, Product, candidate.Udi, candidate.Batch, candidate.Country, candidate.Jurisdiction,
            candidate.Seriousness, candidate.OccurredAt, candidate.AwarenessAt, ImportService.Scenario, true), SqlFixture.Pms,
            CaseScenario.Key());
        await fixture.Service(clock).Reclassify(id, new ReclassifyCommand(registered.Version, Seriousness.DeathOrSeriousDeterioration,
            "Synthetische medizinische Pruefung: Lockerung als schwerwiegende Verschlechterung eingestuft."), SqlFixture.Medical,
            CaseScenario.Key());
        var reclassified = await Case(id);
        Assert.Equal((id, 1, 2, Seriousness.DeathOrSeriousDeterioration),
            (registered.Id, registered.Version, reclassified.Version, reclassified.Seriousness));

        // A row matching only the reclassified case states other facts than the first registration of its source.
        var severe = await Imports().ImportIntake(IntakeCommand(R01File(R02Row(4, "DeathOrSeriousDeterioration")),
            "ortho-intake-r02-reklassifiziert.csv"), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal((IntakeImportStatus.Processed, new IntakeImportCounts(1, 0, 0, 0, 0, 1)), (severe.Status, severe.Counts));
        var held = Assert.Single(severe.Rows);
        Assert.Equal(("2:CorrectionRequired:source_changed", IntakeKnown.Source, id, 2),
            ($"{held.Line}:{held.Outcome}:{held.Code}", held.Known, held.Case!.Id, held.Case.Version));
        Assert.StartsWith("Gleiche Kanalquelle mit abweichenden Angaben gegenueber der Erstregistrierung des Vorfalls: Schweregrad " +
            "DeathOrSeriousDeterioration statt erfasst OtherSerious.", held.Reason, StringComparison.Ordinal);
        Assert.Contains("zur menschlichen Korrektur zurueckgestellt", held.Reason, StringComparison.Ordinal);

        // The original row repeats the first registration unchanged; the human reclassification stays in force.
        var repeat = await Imports().ImportIntake(IntakeCommand(R01File(recorded), "ortho-intake-r02-erstfassung.csv"),
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(new[] { "2:Duplicate:source_known" }, repeat.Rows.Select(r => $"{r.Line}:{r.Outcome}:{r.Code}"));
        Assert.Equal((id, 2), (repeat.Rows[0].Case!.Id, repeat.Rows[0].Case!.Version));
        Assert.Equal(Canonical.Hash(reclassified), Canonical.Hash(await Case(id)));
    }

    [Fact]
    public async Task OpenCopyOfAnUnfinishedImportConfirmsNoSourceFactsAgainstAManualFirstRegistration()
    {
        // A stores two identical serious rows and stops right before the case read of its registration. In that window the
        // same source is registered manually as OtherSerious and B states A's serious row once. A's copy, never taken over,
        // confirms nothing: B and then A are compared with the first registration and held.
        var id = CaseIdentity.For(Product, "IMP-R02-5");
        var serious = R02Row(5, "DeathOrSeriousDeterioration");
        var candidate = Assert.Single(ImportRules.EvaluateIntake(SyntheticCsv.Parse(R01File(serious), ImportRules.IntakeHeader,
            ImportRules.MaximumIntakeRows), fixture.Policies.Device(Product), IntakeImportChannel.SyntheticDistributorFeedback,
            clock.GetUtcNow()).Candidates);
        IntakeImport? stored = null;
        SafetyCase? manual = null;
        IntakeImport? parallel = null;
        var window = new HookStore(fixture.Store).BeforeGet("case", id, async () =>
        {
            stored = Assert.Single(await fixture.Store.List<IntakeImport>(ImportService.IntakeKind, [Product]),
                i => i.Original.FileName == "ortho-intake-r04-a.csv");
            await fixture.Service(clock).Register(new RegisterComplaint(candidate.IncidentKey, candidate.SourceId, candidate.Title,
                candidate.Description, Product, candidate.Udi, candidate.Batch, candidate.Country, candidate.Jurisdiction,
                Seriousness.OtherSerious, candidate.OccurredAt, candidate.AwarenessAt, ImportService.Scenario, true), SqlFixture.Pms,
                CaseScenario.Key());
            manual = await Case(id);
            parallel = await Imports().ImportIntake(IntakeCommand(R01File(serious), "ortho-intake-r04-b.csv"), SqlFixture.Pms,
                CaseScenario.Key());
        });
        var resumed = await Imports(window).ImportIntake(IntakeCommand(R01File(serious, serious), "ortho-intake-r04-a.csv"),
            SqlFixture.Pms, CaseScenario.Key());
        var open = stored ?? throw new InvalidOperationException("Import A erreichte das Registrierungsfenster nicht.");
        Assert.Equal((IntakeImportStatus.Received, 1), (open.Status, open.Version));
        Assert.Equal(new[] { "2:Pending:pending", "3:Duplicate:row_duplicate" }, open.Rows.Select(r => $"{r.Line}:{r.Outcome}:{r.Code}"));
        Assert.All(open.Rows, r => Assert.Null(r.Case));
        var registered = manual ?? throw new InvalidOperationException("Manuelle Registrierung lief nicht im Registrierungsfenster.");
        Assert.Equal((1, Seriousness.OtherSerious, candidate.SourceId),
            (registered.Version, registered.Seriousness, Assert.Single(registered.Sources).ExternalId));

        // B is compared with the first registration instead of taking A's open copy as recorded facts.
        var b = parallel ?? throw new InvalidOperationException("Import B lief nicht im Registrierungsfenster.");
        Assert.Equal((IntakeImportStatus.Processed, new IntakeImportCounts(1, 0, 0, 0, 0, 1)), (b.Status, b.Counts));
        var held = Assert.Single(b.Rows);
        Assert.Equal(("2:CorrectionRequired:source_changed", IntakeKnown.Source, id, 1),
            ($"{held.Line}:{held.Outcome}:{held.Code}", held.Known, held.Case!.Id, held.Case.Version));
        Assert.StartsWith("Gleiche Kanalquelle mit abweichenden Angaben gegenueber der Erstregistrierung des Vorfalls: Schweregrad " +
            "DeathOrSeriousDeterioration statt erfasst OtherSerious.", held.Reason, StringComparison.Ordinal);
        Assert.Contains("zur menschlichen Korrektur zurueckgestellt", held.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Vorfall unveraendert", held.Reason, StringComparison.Ordinal);

        // A resumes against the manual registration and B's held row: no recorded fact confirms its rows either.
        Assert.Equal((open.Id, 2, IntakeImportStatus.Processed, new IntakeImportCounts(2, 0, 0, 0, 0, 2)),
            (resumed.Id, resumed.Version, resumed.Status, resumed.Counts));
        Assert.Equal(new[] { "2:CorrectionRequired:source_changed", "3:CorrectionRequired:source_changed" },
            resumed.Rows.Select(r => $"{r.Line}:{r.Outcome}:{r.Code}"));
        Assert.All(resumed.Rows, r => Assert.Equal((id, 1), (r.Case!.Id, r.Case.Version)));
        Assert.StartsWith("Gleiche Kanalquelle mit abweichenden Angaben gegenueber der Erstregistrierung des Vorfalls: Schweregrad " +
            "DeathOrSeriousDeterioration statt erfasst OtherSerious.", resumed.Rows[0].Reason, StringComparison.Ordinal);
        Assert.StartsWith("Identische Kopie von Zeile 2:", resumed.Rows[1].Reason, StringComparison.Ordinal);
        Assert.Equal(new[] { "IntakeImportReceived", "IntakeImportProcessed" }, (await fixture.Store.Audit(open.Id)).Select(e => e.Action));
        // The manual registration stays the only one and unchanged; no import corrects its facts or deadlines.
        Assert.Single(await fixture.Store.Audit(id), e => e.Action == "ComplaintRegistered");
        Assert.Equal(Canonical.Hash(registered), Canonical.Hash(await Case(id)));
    }

    [Fact]
    public async Task ParallelDenominatorUpdatesOfOnePeriodCannotBothPassAgainstStaleSnapshotsAndBoundTrendsKeepTheirVersion()
    {
        var basis = await Delivery(40, 60, "ortho-distribution-r03-basis.csv");
        var worldwide = await Imports().ApproveDenominator(Approval(basis.Id, 100, 0), SqlFixture.Quality, CaseScenario.Key());
        var eea = await Imports().ApproveDenominator(Eea(basis.Id, 40, 0), SqlFixture.Quality, CaseScenario.Key());
        Assert.Equal((ExposureId, 1, 100, EeaId, 1, 40), (worldwide.Id, worldwide.Version, worldwide.Count, eea.Id, eea.Version, eea.Count));
        var bound = await Analytics().Trend(new RunTrendCommand(Product, "Worldwide", Q3Start, Q3End, ExposureId),
            SqlFixture.Quality, CaseScenario.Key());
        var smaller = await Delivery(10, 40, "ortho-distribution-r03-kleiner.csv");
        var larger = await Delivery(80, 20, "ortho-distribution-r03-eea-groesser.csv");

        // Both approvals read Worldwide 100 and EEA 40; Worldwide 50 was only valid against the stale EEA 40.
        var committed = await Race(Approval(smaller.Id, 50, 1), Eea(larger.Id, 80, 1));
        Assert.Equal((EeaId, 2, 80, larger.Id), (committed.Id, committed.Version, committed.Count, committed.Source!.Import.Id));
        var kept = (await fixture.Store.Get<Exposure>("exposure", ExposureId))!;
        Assert.Equal((1, 100, basis.Id), (kept.Version, kept.Count, kept.Source!.Import.Id));
        Assert.Equal("exposure_subset", await Fails(() => Imports().ApproveDenominator(Approval(smaller.Id, 50, 1),
            SqlFixture.Quality, CaseScenario.Key())));
        var trend = (await fixture.Store.Get<TrendRun>("trend", bound.Id))!;
        Assert.Equal((ExposureId, 1, 100, bound.InputHash),
            (trend.Input.Exposure.Id, trend.Input.Exposure.Version, trend.Input.Exposure.Count, trend.InputHash));
    }

    [Fact]
    public async Task ParallelFirstDenominatorApprovalsOfOnePeriodBindTheAbsenceOfTheOtherRegion()
    {
        var smaller = await Delivery(10, 40, "ortho-distribution-r03-erst-kleiner.csv");
        var larger = await Delivery(80, 20, "ortho-distribution-r03-erst-eea.csv");

        // Neither region of the period exists when both approvals read; the EEA approval commits first.
        var committed = await Race(Approval(smaller.Id, 50, 0), Eea(larger.Id, 80, 0));
        Assert.Equal((EeaId, 1, 80, larger.Id), (committed.Id, committed.Version, committed.Count, committed.Source!.Import.Id));
        Assert.Null(await fixture.Store.Get<Exposure>("exposure", ExposureId));
        Assert.Equal("exposure_subset", await Fails(() => Imports().ApproveDenominator(Approval(smaller.Id, 50, 0),
            SqlFixture.Quality, CaseScenario.Key())));
        Assert.Null(await fixture.Store.Get<Exposure>("exposure", ExposureId));
    }

    private ImportService Imports() => new(fixture.Store, fixture.Objects, fixture.Policies, fixture.Service(clock), clock);

    // Same services over a decorated store, so a test can place another import or approval into an exact window.
    private ImportService Imports(IStateStore store) => new(store, fixture.Objects, fixture.Policies,
        new CaseService(store, fixture.Objects, fixture.Policies, new AgentRunner(new(null, null, null, null)),
            new KnowledgeSearch(fixture.Store, fixture.Objects, fixture.Store), clock, new MirComposer()), clock);

    // The stale approval reads first; the parallel one commits right before the stale commit and must win alone.
    private async Task<Exposure> Race(ApproveDenominatorCommand stale, ApproveDenominatorCommand parallel)
    {
        Exposure? committed = null;
        var window = new HookStore(fixture.Store).BeforeExecute("ExposureDenominatorApproved",
            async () => committed = await Imports().ApproveDenominator(parallel, SqlFixture.Quality, CaseScenario.Key()));
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            Imports(window).ApproveDenominator(stale, SqlFixture.Quality, CaseScenario.Key()));
        Assert.Equal(("dependency_version", 409), (error.Code, error.Status));
        Assert.Contains("anderer Nenner derselben Periode", error.Message, StringComparison.Ordinal);
        var result = committed ?? throw new InvalidOperationException("Parallele Nennerfreigabe lief nicht vor dem Commit.");
        var stored = (await fixture.Store.Get<Exposure>("exposure", result.Id))!;
        Assert.Equal((result.Version, Canonical.Hash(result)), (stored.Version, Canonical.Hash(stored)));
        return result;
    }

    private async Task<DistributionImport> Delivery(int ireland, int britain, string fileName)
    {
        var delivery = await Imports().ImportDistribution(DistributionCommand(ImportRules.DistributionHeader +
            $"\nL-0001,ortho,DEMO-OL-M3-DI,DEMO-OL-2026-A,DEMO-SITE-IE-001,IE,EU-IE,2026-07-08,{ireland},{ireland},{ireland}" +
            $"\nL-0002,ortho,DEMO-OL-M3-DI,DEMO-OL-2026-C,DEMO-SITE-GB-001,GB,GB,2026-07-15,{britain},{britain},{britain}", fileName),
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(DistributionImportStatus.Validated, delivery.Status);
        return delivery;
    }

    private AnalyticsService Analytics() => new(fixture.Store, new AgentRunner(new(null, null, null, null)), fixture.Policies, clock);

    private async Task<SafetyCase> Case(string id) => (await fixture.Store.Get<SafetyCase>("case", id))!;

    private static ImportIntakeCommand IntakeCommand(string content, string fileName = IntakeFile) =>
        new(Product, IntakeImportChannel.SyntheticDistributorFeedback, fileName, content, ImportReason, true);

    private static string R01Row(string awareness) =>
        "HF-R01-1,IMP-R01-1,ortho,DEMO-OL-M3-101,DEMO-OL-2026-A,IE,EU-IE,OtherSerious,2026-10-31T10:00:00Z," + awareness +
        ",Synthetische Lockerung R01,SYNTHETISCH: Gleicher Kanaltext zur Lockerung ohne Personenbezug.";

    private static string R01File(params string[] rows) => ImportRules.IntakeHeader + "\n" + string.Join('\n', rows);

    private static string R02Row(int n, string seriousness) =>
        $"HF-R02-{n},IMP-R02-{n},ortho,DEMO-OL-M3-101,DEMO-OL-2026-A,IE,EU-IE,{seriousness},2026-11-01T10:00:00Z,2026-11-10T08:00:00Z," +
        $"Synthetische Lockerung R02,SYNTHETISCH: Kanaltext R02-{n} zur Lockerung ohne Personenbezug.";

    private static ImportDistributionCommand DistributionCommand(string content, string fileName = DistributionFile) =>
        new(Product, fileName, content, ImportReason);

    private static ApproveDenominatorCommand Approval(string importId, int count, int expectedVersion) =>
        new(importId, 1, "Worldwide", Q3Start, Q3End, count, expectedVersion, ApprovalReason);

    private static ApproveDenominatorCommand Eea(string importId, int count, int expectedVersion) =>
        Approval(importId, count, expectedVersion) with { Region = EeaRegion };

    private static string Legal(Deadline d) => $"LEGAL:{d.Id}:Gesetzliche Hoechstfrist ueberschritten; keine Rueckdatierung.";

    private static string Safety(Deadline d) => $"SAFETY:{d.Id}:Interne SafetyDue ueberschritten; Stellvertretung alarmieren.";

    private static string[] Tuples(SafetyCase c) => c.Deadlines
        .Select(d => $"{d.Id}|{d.Jurisdiction}|{d.Seriousness}|{d.TriggerAt:O}|{d.LegalDue:O}|{d.SafetyDue:O}|{d.Status}").ToArray();

    private static async Task<string> Fails(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private async Task<(string, DateTimeOffset, DateTimeOffset, string)[]> Projection(string caseId)
    {
        await using var connection = await fixture.Store.Open();
        using var command = SqlStateStore.Command(connection, null,
            "SELECT Id, LegalDue, SafetyDue, Status FROM dbo.Deadlines WHERE CaseId=@id ORDER BY Id", ("id", caseId));
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string, DateTimeOffset, DateTimeOffset, string)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3)));
        return [.. rows];
    }

    private async Task<CalendarEvent[]> CalendarEvents(string kind)
    {
        var reader = new WorkspaceReadService(fixture.Store, clock);
        var events = new List<CalendarEvent>();
        for (var offset = 0; ;)
        {
            var page = await reader.Calendar(SqlFixture.Pms, Product, new DateOnly(2026, 11, 1), "UTC", kind, "all", null, offset);
            events.AddRange(page.Events);
            offset += page.PageSize;
            if (offset >= page.MatchingEvents || !page.Events.Any()) return [.. events];
        }
    }

    private static void AssertAggregate(ZipArchive archive, JsonElement[] aggregates, string kind, string id, int versions)
    {
        var aggregate = Assert.Single(aggregates, a => a.GetProperty("kind").GetString() == kind && a.GetProperty("id").GetString() == id);
        Assert.Equal(Product, aggregate.GetProperty("productId").GetString());
        var entries = aggregate.GetProperty("versions").EnumerateArray().ToArray();
        Assert.Equal(Enumerable.Range(1, versions), entries.Select(v => v.GetProperty("version").GetInt32()));
        Assert.All(entries, v => Assert.NotNull(archive.GetEntry(v.GetProperty("path").GetString()!)));
    }

    private static byte[] Read(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"Exporteintrag fehlt: {name}");
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Shipped(string fileName) => File.ReadAllText(Path.Combine(Home, "demo", "imports", fileName));

    private static string FindHome()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Cohors.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Cohors repository root was not found.");
    }

    // One-shot interleaving without timing assumptions: the hook runs once, before the matching call reaches the store.
    private sealed class HookStore(IStateStore inner) : IStateStore
    {
        private string? executeAction;
        private Func<Task>? executeHook;
        private string? readKind;
        private string? readId;
        private Func<Task>? readHook;

        public HookStore BeforeExecute(string action, Func<Task> hook)
        {
            (executeAction, executeHook) = (action, hook);
            return this;
        }

        public HookStore BeforeGet(string kind, string id, Func<Task> hook)
        {
            (readKind, readId, readHook) = (kind, id, hook);
            return this;
        }

        public async Task<T?> Get<T>(string kind, string key, CancellationToken ct = default)
        {
            if (kind == readKind && key == readId && Interlocked.Exchange(ref readHook, null) is { } hook) await hook();
            return await inner.Get<T>(kind, key, ct);
        }

        public async Task<T> Execute<T>(CommandContext c, Func<T?, T> change, CancellationToken ct = default)
        {
            if (c.Action == executeAction && Interlocked.Exchange(ref executeHook, null) is { } hook) await hook();
            return await inner.Execute(c, change, ct);
        }

        public Task<T[]> List<T>(string kind, string[] products, CancellationToken ct = default) => inner.List<T>(kind, products, ct);
        public Task<T?> GetVersion<T>(string kind, string key, int version, CancellationToken ct = default) => inner.GetVersion<T>(kind, key, version, ct);
        public Task<T[]> History<T>(string kind, string[] products, CancellationToken ct = default) => inner.History<T>(kind, products, ct);
        public Task<PortfolioState> Capture(string[] products, CancellationToken ct = default) => inner.Capture(products, ct);
        public Task<T?> Receipt<T>(CommandContext c, CancellationToken ct = default) => inner.Receipt<T>(c, ct);
        public Task<Psur> FreezePsur(CommandContext c, PsurCaptureScope scope, Func<PortfolioState, Psur> create, CancellationToken ct = default) => inner.FreezePsur(c, scope, create, ct);
        public Task<AuditEvent[]> Audit(string key, CancellationToken ct = default) => inner.Audit(key, ct);
        public Task<OutboxEvent[]> PendingEvents(int limit, CancellationToken ct = default) => inner.PendingEvents(limit, ct);
        public Task<OutboxEvent?> ClaimEvent(string eventId, string owner, CancellationToken ct = default) => inner.ClaimEvent(eventId, owner, ct);
        public Task CompleteEvent(string eventId, string consumer, string owner, string? error, CancellationToken ct = default) => inner.CompleteEvent(eventId, consumer, owner, error, ct);
        public Task<OutboxIssue[]> OutboxIssues(string[] products, CancellationToken ct = default) => inner.OutboxIssues(products, ct);
        public Task<bool> HasReceipt(string eventId, string consumer, CancellationToken ct = default) => inner.HasReceipt(eventId, consumer, ct);
    }
}
