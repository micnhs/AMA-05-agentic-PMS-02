using System.Globalization;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

[Trait("Category", "ImportRules")]
public sealed class ImportRulesTests
{
    private static readonly string Home = FindHome();
    private static readonly PolicyCatalog Policies = DemoBaseline.LoadPolicies(Path.Combine(Home, "policies"));
    private static readonly Device Ortho = Policies.Device("ortho");
    private static readonly Device Cardio = Policies.Device("cardio");
    private static readonly DateTimeOffset ReceivedAt = DateTimeOffset.Parse("2026-11-19T12:00:00Z");
    private static readonly DateOnly Today = new(2026, 11, 19);
    private static readonly DateOnly Q3Start = new(2026, 7, 1);
    private static readonly DateOnly Q3End = new(2026, 9, 30);
    private const IntakeImportChannel Channel = IntakeImportChannel.SyntheticDistributorFeedback;
    private const string IntakeFile = "ortho-intake-distributor-2026-11.csv";
    private const string DistributionFile = "ortho-distribution-2026-q3.csv";
    private const string Reason = "Synthetische Nennerfreigabe nach Pruefung von Lieferdatei, Region und Periode.";
    private const string ValidIntake = "HF-T-1,IMP-T-1,ortho,DEMO-OL-M3-101,DEMO-OL-2026-A,IE,EU-IE,OtherSerious," +
        "2026-11-02T09:15:00Z,2026-11-03T08:30:00Z,Synthetischer Testtitel,SYNTHETISCH: Synthetische Testbeschreibung ohne Personenbezug.";
    private const string ValidDelivery = "R-1,ortho,DEMO-OL-M3-DI,DEMO-OL-T-A,DEMO-SITE-IE-001,IE,EU-IE,2026-07-08,10,5,5";

    [Fact]
    public void ShippedChannelFileKeepsEveryRowWithOutcomeAndReason()
    {
        var content = Shipped(IntakeFile);
        var evaluation = ImportRules.EvaluateIntake(
            SyntheticCsv.Parse(content, ImportRules.IntakeHeader, ImportRules.MaximumIntakeRows), Ortho, Channel, ReceivedAt);

        Assert.Equal(["2:Pending:pending", "3:Duplicate:row_duplicate", "4:Pending:pending",
            "5:Rejected:row_awareness_missing", "6:Rejected:row_jurisdiction_country", "7:Pending:pending"],
            evaluation.Rows.Select(r => $"{r.Line}:{r.Outcome}:{r.Code}"));
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.All(evaluation.Rows, r =>
        {
            Assert.Equal(Canonical.HashText(lines[r.Line - 1]), r.RowHash);
            Assert.False(string.IsNullOrWhiteSpace(r.Reason));
            Assert.Null(r.Case);
        });
        Assert.Equal(2, evaluation.Rows.Single(r => r.Line == 3).DuplicateOf);
        Assert.Equal([2, 4, 7], evaluation.Candidates.Select(c => c.Line));
        Assert.Equal(new IntakeImportCounts(6, 0, 1, 2, 3, 0), ImportRules.Count(evaluation.Rows));

        var known = evaluation.Candidates.Single(c => c.Line == 4);
        Assert.Equal("DEMO-07", known.IncidentKey);
        Assert.Null(known.OccurredAt);
        var late = evaluation.Candidates.Single(c => c.Line == 7);
        Assert.Equal("HF-2026-1105", late.ExternalRef);
        Assert.Equal("IMP-ORTHO-04", late.IncidentKey);
        Assert.Equal("SyntheticDistributorFeedback:HF-2026-1105", late.SourceId);
        Assert.Null(late.Udi);
        Assert.Equal("DEMO-OL-2026-C", late.Batch);
        Assert.Equal("GB", late.Country);
        Assert.Equal("NI", late.Jurisdiction);
        Assert.Equal(Seriousness.DeathOrSeriousDeterioration, late.Seriousness);
        Assert.Equal<DateTimeOffset?>(DateTimeOffset.Parse("2026-11-07T16:00:00Z"), late.OccurredAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-09T09:00:00Z"), late.AwarenessAt);
    }

    [Theory]
    [InlineData(0, "HF 1", "row_external_ref")]
    [InlineData(1, "X", "row_incident_key")]
    [InlineData(2, "cardio", "row_product")]
    [InlineData(3, "DEMO-CF-V2-101", "row_udi")]
    [InlineData(4, "LOT-2026-A", "row_batch")]
    [InlineData(5, "ie", "row_country")]
    [InlineData(6, "EU-ES", "row_market")]
    [InlineData(5, "DE", "row_jurisdiction_country")]
    [InlineData(7, "Serious", "row_seriousness")]
    [InlineData(7, "2", "row_seriousness")]
    [InlineData(8, "2026-11-02", "row_date")]
    [InlineData(9, "", "row_awareness_missing")]
    [InlineData(9, "2026-11-03T08:30Z", "row_date")]
    [InlineData(9, "2026-11-20T08:30:00Z", "row_awareness_future")]
    [InlineData(8, "2026-11-04T09:15:00Z", "row_awareness_order")]
    [InlineData(10, "Kurz", "row_text")]
    [InlineData(11, "Synthetische Beschreibung ohne Kennzeichnung am Anfang.", "row_synthetic")]
    public void InvalidChannelRowsAreRejectedWithReasonAndNoCandidate(int field, string value, string code)
    {
        var evaluation = Intake(With(field, value));

        var row = Assert.Single(evaluation.Rows);
        Assert.Equal($"Rejected:{code}", Outcome(row));
        Assert.False(string.IsNullOrWhiteSpace(row.Reason));
        Assert.Null(row.Case);
        Assert.Empty(evaluation.Candidates);
    }

    [Fact]
    public void OptionalUdiBatchAndEventDateStayEmptyInsteadOfBeingGuessed()
    {
        var valid = Assert.Single(Intake(ValidIntake).Candidates);
        Assert.Equal("DEMO-OL-M3-101", valid.Udi);
        Assert.Equal("DEMO-OL-2026-A", valid.Batch);
        Assert.Equal("SyntheticDistributorFeedback:HF-T-1", valid.SourceId);

        var fields = ValidIntake.Split(',');
        fields[3] = "";
        fields[4] = " ";
        fields[5] = "GB";
        fields[6] = "NI";
        fields[8] = "";
        fields[9] = "2026-11-03T09:30:00+01:00";
        var evaluation = Intake(string.Join(',', fields));

        Assert.Equal("Pending:pending", Outcome(Assert.Single(evaluation.Rows)));
        var candidate = Assert.Single(evaluation.Candidates);
        Assert.Null(candidate.Udi);
        Assert.Null(candidate.Batch);
        Assert.Null(candidate.OccurredAt);
        Assert.Equal("GB", candidate.Country);
        Assert.Equal("NI", candidate.Jurisdiction);
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T08:30:00Z"), candidate.AwarenessAt);
        Assert.Equal(TimeSpan.Zero, candidate.AwarenessAt.Offset);
    }

    [Fact]
    public void MalformedRowsAreRejectedIndividuallyWhileValidRowsContinue()
    {
        var evaluation = Intake(
            "HF-T-2,\"IMP-T-2,ortho",
            "HF-T-3,IMP\"T-3,ortho",
            "\"HF-T-4\"x,IMP-T-4,ortho",
            "",
            "a,b,c",
            With(10, "\"Titel, mit \"\"Zitat\"\"\"", "HF-T-7"));

        Assert.Equal(["2:Rejected:row_csv", "3:Rejected:row_csv", "4:Rejected:row_csv", "5:Rejected:row_empty",
            "6:Rejected:row_columns", "7:Pending:pending"], evaluation.Rows.Select(r => $"{r.Line}:{Outcome(r)}"));
        Assert.All(evaluation.Rows.Where(r => r.Outcome == IntakeRowOutcome.Rejected), r =>
        {
            Assert.Null(r.ExternalRef);
            Assert.False(string.IsNullOrWhiteSpace(r.Reason));
        });
        Assert.Equal("Titel, mit \"Zitat\"", Assert.Single(evaluation.Candidates).Title);
    }

    [Fact]
    public void SameReferenceWithDifferentContentIsNeverResolvedSilently()
    {
        var conflict = Intake(ValidIntake, With(10, "Geaenderter Testtitel"), ValidIntake);
        Assert.Equal(["2:Rejected:row_ref_conflict", "3:Rejected:row_ref_conflict", "4:Rejected:row_ref_conflict"],
            conflict.Rows.Select(r => $"{r.Line}:{Outcome(r)}"));
        Assert.Empty(conflict.Candidates);

        var invalidVariant = Intake(ValidIntake, With(9, ""));
        Assert.Equal(["2:Rejected:row_ref_conflict", "3:Rejected:row_awareness_missing"],
            invalidVariant.Rows.Select(r => $"{r.Line}:{Outcome(r)}"));

        var invalidCopies = Intake(With(9, ""), With(9, ""));
        Assert.Equal(["2:Rejected:row_awareness_missing", "3:Rejected:row_awareness_missing"],
            invalidCopies.Rows.Select(r => $"{r.Line}:{Outcome(r)}"));
        Assert.All(invalidCopies.Rows, r => Assert.Null(r.DuplicateOf));
    }

    [Fact]
    public void FileLevelViolationsImportNothing()
    {
        string[][] cases =
        [
            ["externalRef,incidentKey\nHF-1,IMP-1", "csv_header"],
            ["", "csv_empty"],
            ["\uFEFF", "csv_empty"],
            [ImportRules.IntakeHeader + "\r\n", "csv_empty"],
            [ImportRules.IntakeHeader + "\nHF-1,a\rb", "csv_line_ending"],
            [ImportRules.IntakeHeader + "\r\nHF-1\u0001", "csv_encoding"],
            [ImportRules.IntakeHeader + "\n" + string.Join('\n', Enumerable.Repeat(ValidIntake, ImportRules.MaximumIntakeRows + 1)), "csv_rows"]
        ];

        Assert.All(cases, c => Assert.Equal(c[1], Assert.Throws<DomainException>(() =>
            SyntheticCsv.Parse(c[0], ImportRules.IntakeHeader, ImportRules.MaximumIntakeRows)).Code));

        var records = SyntheticCsv.Parse("\uFEFF" + ImportRules.IntakeHeader + "\r\n" + ValidIntake + "\r\n" +
            With(0, "HF-T-2") + "\r\n", ImportRules.IntakeHeader, ImportRules.MaximumIntakeRows);
        Assert.Equal([2, 3], records.Select(r => r.Line));
        Assert.Equal(ValidIntake, records[0].Raw);
        Assert.All(records, r => Assert.Null(r.Error));
        Assert.Equal(ImportRules.MaximumIntakeRows, SyntheticCsv.Parse(ImportRules.IntakeHeader + "\n" +
            string.Join('\n', Enumerable.Repeat(ValidIntake, ImportRules.MaximumIntakeRows)),
            ImportRules.IntakeHeader, ImportRules.MaximumIntakeRows).Length);
    }

    [Fact]
    public void OriginalBindsFileNameUtf8SizeAndContentHash()
    {
        var original = ImportRules.Original("intake", "intake.csv", "\u00C4");
        Assert.Equal("intake.csv", original.FileName);
        Assert.Equal("cohors:synthetic-import:intake:intake.csv", original.Uri);
        Assert.Equal(Canonical.HashText("\u00C4"), original.Hash);
        Assert.Equal(2, original.Bytes);
        Assert.Equal(ImportRules.MaximumBytes,
            ImportRules.Original("intake", new string('a', 116) + ".csv", new string('a', ImportRules.MaximumBytes)).Bytes);

        Assert.Equal("import_file_name", Refused(() => ImportRules.Original("intake", "../intake.csv", "x")).Code);
        Assert.Equal("import_file_name", Refused(() => ImportRules.Original("intake", "intake.txt", "x")).Code);
        Assert.Equal("import_file_name", Refused(() => ImportRules.Original("intake", new string('a', 117) + ".csv", "x")).Code);
        Assert.Equal("csv_empty", Refused(() => ImportRules.Original("intake", "intake.csv", null!)).Code);
        Assert.Equal("csv_encoding", Refused(() => ImportRules.Original("intake", "intake.csv", "a\uD800b")).Code);
        Assert.Equal("csv_size", Refused(() =>
            ImportRules.Original("intake", "intake.csv", new string('a', ImportRules.MaximumBytes + 1))).Code);
    }

    [Fact]
    public void ClassificationLinksKnownIncidentsAndNeverReassignsSources()
    {
        var evaluation = Intake(ValidIntake);
        var row = Assert.Single(evaluation.Rows);
        var candidate = Assert.Single(evaluation.Candidates);
        var id = CaseIdentity.For("ortho", "IMP-T-1");
        var same = Same(candidate);
        var changed = Source(candidate.SourceId, Canonical.HashText("SYNTHETISCH: Andere Fassung derselben Kanalreferenz."));
        var other = Source("SyntheticClinicFeedback:K-1", new string('b', 64));

        Assert.Equal("Pending:pending:None", Classified(Classify(row, candidate)));
        Assert.Equal("Pending:pending:Case", Classified(Classify(row, candidate, Case(id, "ortho", other))));
        Assert.Equal("Pending:pending:Source", Classified(Classify(row, candidate, Recorded(Case(id, "ortho", same), candidate))));
        Assert.Equal("Rejected:source_revision:None", Classified(Classify(row, candidate, Case(id, "ortho", changed))));
        Assert.Equal("Rejected:source_incident_conflict:None",
            Classified(Classify(row, candidate, Case("other-incident", "ortho", same), Case(id, "ortho"))));
        Assert.Equal("Pending:pending:None", Classified(Classify(row, candidate, Case("other-incident", "cardio", same))));

        var state = Refused(() => Classify(row with { Outcome = IntakeRowOutcome.Duplicate }, candidate));
        Assert.Equal("import_state", state.Code);
        Assert.Equal(409, state.Status);
        Assert.Equal("import_state", Refused(() => Classify(row, candidate with { Line = 3 })).Code);
    }

    [Fact]
    public void SettlementRecordsRegistrationOrLinkWithCaseVersionAndResumesInterruptedRuns()
    {
        var evaluation = Intake(ValidIntake);
        var row = Assert.Single(evaluation.Rows);
        var candidate = Assert.Single(evaluation.Candidates);
        var id = CaseIdentity.For("ortho", "IMP-T-1");
        var source = Same(candidate);
        var earlier = Source("SyntheticClinicFeedback:K-1", new string('b', 64));
        var created = Recorded(Case(id, "ortho", source), candidate);

        var registered = ImportRules.Settle(row, candidate, created, [], own: true);
        Assert.Equal("Registered:registered", Outcome(registered));
        Assert.Equal(PortfolioRules.Reference(created), registered.Case);
        var linked = ImportRules.Settle(row, candidate, Case(id, "ortho", earlier, source), [], own: true);
        Assert.Equal("Duplicate:case_linked", Outcome(linked));
        Assert.Equal("Duplicate:case_known",
            Outcome(ImportRules.Settle(row with { Known = IntakeKnown.Case }, candidate, created, [], own: true)));
        Assert.Equal("Duplicate:source_known",
            Outcome(ImportRules.Settle(row with { Known = IntakeKnown.Source }, candidate, created, [], own: false)));
        // A run whose snapshot predates another import's registration never reports that registration as its own.
        Assert.Equal("Duplicate:source_known", Outcome(ImportRules.Settle(row, candidate, created, [], own: false)));
        Assert.NotEqual(registered.Reason, linked.Reason);
        Assert.Equal("import_state", Refused(() => ImportRules.Settle(row, candidate, Case(id, "ortho", earlier), [], own: true)).Code);
        Assert.Equal("import_state", Refused(() => ImportRules.Settle(registered, candidate, created, [], own: true)).Code);
        Assert.Equal("import_state", Refused(() =>
            ImportRules.Settle(row, candidate with { RowHash = new string('c', 64) }, created, [], own: true)).Code);

        var resumed = ImportRules.Reclassify(row, candidate, "ortho", [created], []);
        Assert.Equal(IntakeKnown.None, resumed.Known);
        Assert.Equal("Registered:registered", Outcome(ImportRules.Settle(resumed, candidate, created, [], own: true)));
        var caseAtReceipt = ImportRules.Reclassify(row with { Known = IntakeKnown.Case }, candidate, "ortho", [created], []);
        Assert.Equal("Duplicate:case_known", Outcome(ImportRules.Settle(caseAtReceipt, candidate, created, [], own: true)));
        var sourceAtReceipt = ImportRules.Reclassify(row with { Known = IntakeKnown.Source }, candidate, "ortho", [created], []);
        Assert.Equal("Duplicate:source_known", Outcome(ImportRules.Settle(sourceAtReceipt, candidate, created, [], own: false)));
        Assert.Equal("Rejected:source_incident_conflict",
            Outcome(ImportRules.Reclassify(row, candidate, "ortho", [Case("other-incident", "ortho", source)], [])));
    }

    [Fact]
    public void SameSourceWithChangedFactsIsHeldForHumanCorrectionInsteadOfAnIdenticalDuplicate()
    {
        var evaluation = Intake(ValidIntake);
        var row = Assert.Single(evaluation.Rows);
        var candidate = Assert.Single(evaluation.Candidates);
        var id = CaseIdentity.For("ortho", "IMP-T-1");
        var identical = Recorded(Case(id, "ortho", Same(candidate)), candidate) with
        {
            Evidence = [Occurrence(Same(candidate), candidate.OccurredAt!.Value)]
        };

        Assert.Equal("Pending:pending:Source", Classified(Classify(row, candidate, identical)));
        var repeat = ImportRules.Settle(row with { Known = IntakeKnown.Source }, candidate, identical, [], own: false);
        Assert.Equal("Duplicate:source_known", Outcome(repeat));
        Assert.Equal("Kanalquelle mit identischer Zeile (Titel, Text, Ereignis- und Kenntniszeitpunkt, Schweregrad, Rechtsraum, Land, " +
            "UDI, Charge) bereits erfasst; Vorfall unveraendert.", repeat.Reason);

        // Recorded with a later awareness than the row states: the earlier knowledge must stay visible.
        var recordedLater = Case(id, "ortho", Same(candidate) with { AwarenessAt = DateTimeOffset.Parse("2026-11-10T08:00:00Z") });
        var held = Classify(row, candidate, recordedLater);
        Assert.Equal("CorrectionRequired:source_changed:Source", Classified(held));
        Assert.Equal(PortfolioRules.Reference(recordedLater), held.Case);
        Assert.StartsWith("Gleiche Kanalquelle mit abweichenden Angaben: erste Kenntnis 2026-11-03T08:30:00Z statt erfasst " +
            "2026-11-10T08:00:00Z. Keine identische Dublette: zur menschlichen Korrektur zurueckgestellt.", held.Reason, StringComparison.Ordinal);
        Assert.Contains("der Import nimmt keine Fristkorrektur vor", held.Reason, StringComparison.Ordinal);
        Assert.Contains("nur Medical oder RA", held.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Vorfall unveraendert", held.Reason, StringComparison.Ordinal);

        var recordedEarlier = Case(id, "ortho", Same(candidate) with { AwarenessAt = DateTimeOffset.Parse("2026-11-01T08:00:00Z") });
        Assert.Equal("CorrectionRequired:source_changed:Source", Classified(Classify(row, candidate, recordedEarlier)));
        Assert.Equal("CorrectionRequired:source_changed:Source", Classified(Classify(row, candidate,
            Case(id, "ortho", Same(candidate) with { Title = "Andere synthetische Ueberschrift" }))));
        var occurred = Classify(row, candidate,
            identical with { Evidence = [Occurrence(Same(candidate), DateTimeOffset.Parse("2026-10-30T09:15:00Z"))] });
        Assert.Equal("CorrectionRequired:source_changed:Source", Classified(occurred));
        Assert.Contains("Ereigniszeitpunkt 2026-11-02T09:15:00Z statt erfasst 2026-10-30T09:15:00Z", occurred.Reason, StringComparison.Ordinal);
        Assert.Equal("CorrectionRequired:source_changed",
            Outcome(ImportRules.Reclassify(row with { Known = IntakeKnown.Source }, candidate, "ortho", [recordedLater], [])));

        // Registration returns an already recorded source unchanged, so settlement checks the facts again.
        var raced = ImportRules.Settle(row, candidate, recordedLater, [], own: false);
        Assert.Equal("CorrectionRequired:source_changed", Outcome(raced));
        Assert.Equal(PortfolioRules.Reference(recordedLater), raced.Case);
        Assert.Equal("CorrectionRequired:source_changed",
            Outcome(ImportRules.Settle(row with { Known = IntakeKnown.Source }, candidate, recordedLater, [], own: false)));
    }

    [Fact]
    public void SameSourceRowDifferingFromAnOpenOrAcceptedImportRowIsHeldWhileHeldRowsAreIgnored()
    {
        var first = Intake(ValidIntake);
        var original = Assert.Single(first.Candidates);
        var target = Recorded(Case(CaseIdentity.For("ortho", "IMP-T-1"), "ortho", Same(original)), original);
        var accepted = first.Rows[0] with { Outcome = IntakeRowOutcome.Registered, Code = "registered", Case = PortfolioRules.Reference(target) };
        var interrupted = Earlier(first.Rows[0]) with { Status = IntakeImportStatus.Received, ProcessedAt = null };
        var changed = Intake(With(7, "DeathOrSeriousDeterioration"));
        var row = Assert.Single(changed.Rows);
        var candidate = Assert.Single(changed.Candidates);
        Assert.Equal(original.SourceId, candidate.SourceId);
        Assert.NotEqual(original.RowHash, candidate.RowHash);

        var held = ImportRules.Classify(row, candidate, "ortho", [target], [Earlier(accepted)]);
        Assert.Equal("CorrectionRequired:source_changed:Source", Classified(held));
        Assert.Equal(accepted.Case, held.Case);
        Assert.StartsWith("Gleiche Kanalquelle, aber die Zeile weicht von der bereits uebernommenen Zeile 2 aus earlier.csv ab",
            held.Reason, StringComparison.Ordinal);
        Assert.Equal("CorrectionRequired:source_changed",
            Outcome(ImportRules.Reclassify(row with { Known = IntakeKnown.Source }, candidate, "ortho", [target], [Earlier(accepted)])));
        Assert.Equal("Pending:pending:Source", Classified(ImportRules.Classify(first.Rows[0], original, "ortho", [target], [Earlier(accepted)])));

        // An interrupted or concurrent import whose row is still open binds the source as well, registered or not.
        var open = ImportRules.Classify(row, candidate, "ortho", [target], [interrupted]);
        Assert.Equal("CorrectionRequired:source_changed:Source", Classified(open));
        Assert.StartsWith("Gleiche Kanalquelle, aber die Zeile weicht von der noch nicht abgeschlossenen Zeile 2 aus earlier.csv ab",
            open.Reason, StringComparison.Ordinal);
        Assert.Contains("Fall, Quellen und Fristen bleiben unveraendert", open.Reason, StringComparison.Ordinal);
        var unregistered = ImportRules.Classify(row, candidate, "ortho", [], [interrupted]);
        Assert.Equal("CorrectionRequired:source_changed:None", Classified(unregistered));
        Assert.Null(unregistered.Case);
        Assert.Contains("Kein Vorfall angelegt und keine Quelle uebernommen", unregistered.Reason, StringComparison.Ordinal);
        Assert.Equal("Pending:pending:None", Classified(ImportRules.Classify(first.Rows[0], original, "ortho", [], [interrupted])));
        Assert.Equal("CorrectionRequired:source_changed:Source", Classified(ImportRules.Classify(first.Rows[0], original, "ortho", [target],
            [Earlier(accepted), Earlier(row) with { Id = "intake-open", Status = IntakeImportStatus.Received, ProcessedAt = null }])));

        // Held rows and other products bind nothing; the first registration of the case still does.
        var heldRow = Earlier(row with { Outcome = IntakeRowOutcome.CorrectionRequired, Code = "source_changed" });
        Assert.Equal("Pending:pending:Source", Classified(ImportRules.Classify(first.Rows[0], original, "ortho", [target], [heldRow])));
        Assert.Equal("Pending:pending:Source", Classified(ImportRules.Classify(first.Rows[0], original, "ortho", [target],
            [Earlier(row with { Outcome = IntakeRowOutcome.Registered, Code = "registered" }) with { ProductId = "cardio" }])));
        var byCase = ImportRules.Classify(row, candidate, "ortho", [target], [heldRow]);
        Assert.Equal("CorrectionRequired:source_changed:Source", Classified(byCase));
        Assert.StartsWith("Gleiche Kanalquelle mit abweichenden Angaben gegenueber der Erstregistrierung des Vorfalls: Schweregrad " +
            "DeathOrSeriousDeterioration statt erfasst OtherSerious.", byCase.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnRegistrationSettlesWhileAnotherRegistrationNeedsMatchingRecordedFacts()
    {
        var evaluation = Intake(ValidIntake);
        var row = Assert.Single(evaluation.Rows);
        var candidate = Assert.Single(evaluation.Candidates);
        var id = CaseIdentity.For("ortho", "IMP-T-1");
        var created = Recorded(Case(id, "ortho", Same(candidate)), candidate);
        var later = Earlier(Assert.Single(Intake(With(7, "DeathOrSeriousDeterioration")).Rows)) with
        {
            Id = "intake-later", ReceivedAt = ReceivedAt.AddHours(1), Status = IntakeImportStatus.Received, ProcessedAt = null
        };

        // A resumed run settles the registration it recorded itself, whatever a later import states about the source.
        Assert.Equal("Registered:registered", Outcome(ImportRules.Settle(row, candidate, created, [later], own: true)));
        var contested = ImportRules.Settle(row, candidate, created, [later], own: false);
        Assert.Equal("CorrectionRequired:source_changed:Source", Classified(contested));
        Assert.Contains("noch nicht abgeschlossenen Zeile 2 aus earlier.csv", contested.Reason, StringComparison.Ordinal);

        // Without a settled import row only the first registration of the case binds the row facts.
        Assert.Equal("Duplicate:source_known", Outcome(ImportRules.Settle(row, candidate, created, [], own: false)));
        foreach (var (recorded, finding) in new (SafetyCase, string)[]
        {
            (created with { Seriousness = Seriousness.DeathOrSeriousDeterioration },
                "Schweregrad OtherSerious statt erfasst DeathOrSeriousDeterioration"),
            (created with { Country = "DE", Jurisdiction = "EU-DE" }, "Land/Rechtsraum IE/EU-IE statt erfasst DE/EU-DE"),
            (created with { Udi = "DEMO-OL-M3-102" }, "abweichende UDI"),
            (created with { Batch = null }, "abweichende Charge")
        })
        {
            var mismatch = ImportRules.Settle(row, candidate, recorded, [], own: false);
            Assert.Equal("CorrectionRequired:source_changed:Source", Classified(mismatch));
            Assert.StartsWith("Gleiche Kanalquelle mit abweichenden Angaben gegenueber der Erstregistrierung des Vorfalls: " + finding,
                mismatch.Reason, StringComparison.Ordinal);
            Assert.Equal("CorrectionRequired:source_changed:Source", Classified(Classify(row, candidate, recorded)));
        }

        // A source added to an existing case keeps no row facts of its own: only a settled import row confirms them.
        var appended = Recorded(Case(id, "ortho", Source("SyntheticClinicFeedback:K-1", new string('b', 64)), Same(candidate)), candidate);
        var unconfirmed = ImportRules.Settle(row, candidate, appended, [], own: false);
        Assert.Equal("CorrectionRequired:source_changed:Source", Classified(unconfirmed));
        Assert.StartsWith("Gleiche Kanalquelle bereits erfasst, aber ihre Zeilenangaben", unconfirmed.Reason, StringComparison.Ordinal);
        Assert.Equal("CorrectionRequired:source_changed:Source", Classified(Classify(row, candidate, appended)));
        Assert.Equal("CorrectionRequired:source_changed", Outcome(ImportRules.Settle(row, candidate, appended,
            [Earlier(row) with { Status = IntakeImportStatus.Received, ProcessedAt = null }], own: false)));
        var confirmed = Earlier(row with { Outcome = IntakeRowOutcome.Duplicate, Code = "case_linked", Case = PortfolioRules.Reference(appended) });
        Assert.Equal("Duplicate:source_known", Outcome(ImportRules.Settle(row, candidate, appended, [confirmed], own: false)));
        Assert.Equal("Pending:pending:Source", Classified(ImportRules.Classify(row, candidate, "ortho", [appended], [confirmed])));
    }

    [Fact]
    public void RowFactsAreComparedWithTheFirstRegistrationOfTheSourceNotWithALaterHumanReclassification()
    {
        var evaluation = Intake(ValidIntake);
        var row = Assert.Single(evaluation.Rows);
        var candidate = Assert.Single(evaluation.Candidates);
        var severe = Intake(With(7, "DeathOrSeriousDeterioration"));
        var severeRow = Assert.Single(severe.Rows);
        var severeCandidate = Assert.Single(severe.Candidates);
        var id = CaseIdentity.For("ortho", "IMP-T-1");
        var created = Recorded(Case(id, "ortho", Same(candidate)), candidate);
        // Medical reclassified the case after its first registration; the source itself recorded OtherSerious.
        var reclassified = created with { Version = 2, Seriousness = Seriousness.DeathOrSeriousDeterioration };

        Assert.Equal("Pending:pending:Source", Classified(ImportRules.Classify(row, candidate, "ortho", [reclassified], [], [created])));
        Assert.Equal("Duplicate:source_known",
            Outcome(ImportRules.Settle(row, candidate, reclassified, [], own: false, registration: created)));
        var changed = ImportRules.Classify(severeRow, severeCandidate, "ortho", [reclassified], [], [created]);
        Assert.Equal("CorrectionRequired:source_changed:Source", Classified(changed));
        Assert.StartsWith("Gleiche Kanalquelle mit abweichenden Angaben gegenueber der Erstregistrierung des Vorfalls: Schweregrad " +
            "DeathOrSeriousDeterioration statt erfasst OtherSerious.", changed.Reason, StringComparison.Ordinal);
        Assert.Equal("CorrectionRequired:source_changed",
            Outcome(ImportRules.Settle(severeRow, severeCandidate, reclassified, [], own: false, registration: created)));

        // Without its version 1 a changed case confirms no row facts, in either direction.
        var stranger = Recorded(Case(id, "ortho", Source("SyntheticClinicFeedback:K-1", new string('b', 64)), Same(candidate)), candidate);
        foreach (var (unconfirmedRow, unconfirmedCandidate) in new[] { (row, candidate), (severeRow, severeCandidate) })
        {
            var held = Classify(unconfirmedRow, unconfirmedCandidate, reclassified);
            Assert.Equal("CorrectionRequired:source_changed:Source", Classified(held));
            Assert.StartsWith("Gleiche Kanalquelle bereits erfasst, aber ihre Zeilenangaben", held.Reason, StringComparison.Ordinal);
            Assert.Equal("CorrectionRequired:source_changed:Source", Classified(ImportRules.Classify(unconfirmedRow, unconfirmedCandidate,
                "ortho", [reclassified], [], [created with { Version = 2 }, created with { Id = "other-case" }])));
            foreach (var registration in new SafetyCase?[] { null, created with { Version = 2 }, created with { Id = "other-case" }, stranger })
                Assert.Equal("CorrectionRequired:source_changed", Outcome(ImportRules.Settle(unconfirmedRow, unconfirmedCandidate,
                    reclassified, [], own: false, registration)));
        }
    }

    [Fact]
    public void IdenticalCopiesFollowTheirPrimaryRow()
    {
        var evaluation = Intake(ValidIntake, ValidIntake);
        var primary = evaluation.Rows[0];
        var copy = evaluation.Rows[1];
        var candidate = Assert.Single(evaluation.Candidates);
        var created = Case(CaseIdentity.For("ortho", "IMP-T-1"), "ortho", Same(candidate));

        var settled = ImportRules.Propagate([ImportRules.Settle(primary, candidate, created, [], own: true), copy]);
        Assert.Equal(["Registered:registered", "Duplicate:row_duplicate"], settled.Select(Outcome));
        Assert.Equal(PortfolioRules.Reference(created), settled[1].Case);
        Assert.Equal(new IntakeImportCounts(2, 1, 1, 0, 0, 0), ImportRules.Count(settled));

        var moved = Case(CaseIdentity.For("ortho", "IMP-T-1"), "ortho",
            Same(candidate) with { AwarenessAt = DateTimeOffset.Parse("2026-11-10T08:00:00Z") });
        var held = ImportRules.Propagate([ImportRules.Settle(primary, candidate, moved, [], own: false), copy]);
        Assert.Equal(["CorrectionRequired:source_changed", "CorrectionRequired:source_changed"], held.Select(Outcome));
        Assert.StartsWith("Identische Kopie von Zeile 2:", held[1].Reason, StringComparison.Ordinal);
        Assert.Equal(PortfolioRules.Reference(moved), held[1].Case);
        Assert.Equal(new IntakeImportCounts(2, 0, 0, 0, 0, 2), ImportRules.Count(held));

        var refused = ImportRules.Propagate(
            [ImportRules.Refuse(primary, new DomainException("awareness_order", "Kenntniszeitpunkt liegt nach dem Eingang.")), copy]);
        Assert.Equal(["Rejected:awareness_order", "Rejected:awareness_order"], refused.Select(Outcome));
        Assert.StartsWith("Identische Kopie von Zeile 2:", refused[1].Reason, StringComparison.Ordinal);
        Assert.Null(refused[1].Case);
        Assert.Equal(new IntakeImportCounts(2, 0, 0, 2, 0, 0), ImportRules.Count(refused));
    }

    [Fact]
    public void IdenticalCopyOfAPendingRowConfirmsNoSourceFactsUntilItsPrimaryIsTakenOver()
    {
        // Import A as stored when received: primary row pending, its identical copy not taken over, neither bound to a case.
        var severe = With(7, "DeathOrSeriousDeterioration");
        var received = Earlier(ImportRules.Propagate([.. Intake(severe, severe).Rows])) with
        {
            Version = 1, Status = IntakeImportStatus.Received, ProcessedAt = null
        };
        Assert.Equal(["Pending:pending", "Duplicate:row_duplicate"], received.Rows.Select(Outcome));
        Assert.Equal(2, received.Rows[1].DuplicateOf);
        Assert.All(received.Rows, r => Assert.Null(r.Case));
        // Import B states A's row once; meanwhile the same source was registered manually as OtherSerious.
        var evaluation = Intake(severe);
        var row = Assert.Single(evaluation.Rows);
        var candidate = Assert.Single(evaluation.Candidates);
        Assert.All(received.Rows, r => Assert.Equal(candidate.RowHash, r.RowHash));
        var first = Intake(ValidIntake);
        var original = Assert.Single(first.Candidates);
        var id = CaseIdentity.For("ortho", "IMP-T-1");
        var manual = Recorded(Case(id, "ortho", Same(original)), original);

        var held = ImportRules.Classify(row, candidate, "ortho", [manual], [received]);
        Assert.Equal("CorrectionRequired:source_changed:Source", Classified(held));
        Assert.Equal(PortfolioRules.Reference(manual), held.Case);
        Assert.StartsWith("Gleiche Kanalquelle mit abweichenden Angaben gegenueber der Erstregistrierung des Vorfalls: Schweregrad " +
            "DeathOrSeriousDeterioration statt erfasst OtherSerious.", held.Reason, StringComparison.Ordinal);
        Assert.Equal("CorrectionRequired:source_changed", Outcome(ImportRules.Settle(row, candidate, manual, [received], own: false)));

        // Identical original facts still make an unchanged duplicate; an open differing row still holds.
        var matching = Recorded(Case(id, "ortho", Same(candidate)), candidate);
        Assert.Equal("Pending:pending:Source", Classified(ImportRules.Classify(row, candidate, "ortho", [matching], [received])));
        Assert.Equal("Duplicate:source_known", Outcome(ImportRules.Settle(row, candidate, matching, [received], own: false)));
        var open = ImportRules.Classify(first.Rows[0], original, "ortho", [manual], [received]);
        Assert.Equal("CorrectionRequired:source_changed:Source", Classified(open));
        Assert.StartsWith("Gleiche Kanalquelle, aber die Zeile weicht von der noch nicht abgeschlossenen Zeile 2 aus earlier.csv ab",
            open.Reason, StringComparison.Ordinal);

        // A source appended to a case of another source records no facts of its own: the open copy cannot confirm them,
        // whereas the copy of a primary row taken over and bound to the case confirms them like its primary.
        var appended = Recorded(Case(id, "ortho", Source("SyntheticClinicFeedback:K-1", new string('b', 64)), Same(candidate)), candidate);
        Assert.Equal("CorrectionRequired:source_changed", Outcome(ImportRules.Settle(row, candidate, appended, [received], own: false)));
        var linked = PortfolioRules.Reference(appended);
        var processed = Earlier(ImportRules.Propagate(
            [received.Rows[0] with { Outcome = IntakeRowOutcome.Duplicate, Code = "case_linked", Case = linked }, received.Rows[1]]));
        Assert.Equal(["Duplicate:case_linked", "Duplicate:row_duplicate"], processed.Rows.Select(Outcome));
        Assert.Equal(linked, processed.Rows[1].Case);
        Assert.Equal("Duplicate:source_known", Outcome(ImportRules.Settle(row, candidate, appended, [processed], own: false)));
        Assert.Equal("Pending:pending:Source", Classified(ImportRules.Classify(row, candidate, "ortho", [appended], [processed])));
    }

    [Fact]
    public void DeliveryFindingsRejectRowsWithCodesAndKeepConsistentRows()
    {
        var import = DistributionImportOf(ImportRules.DistributionHeader + "\n" + string.Join('\n',
            "R-1,ortho,DEMO-OL-M3-DI,DEMO-OL-T-A,DEMO-SITE-IE-001,IE,EU-IE,2026-07-01,10,5,5",
            "R-1,ortho,DEMO-OL-M3-DI,DEMO-OL-T-A,DEMO-SITE-IE-001,IE,EU-IE,2026-07-02,10,5,5",
            "R-3,ortho,DEMO-OL-M3-DI,DEMO-OL-T-B,DEMO-SITE-IE-001,IE,EU-IE,2026-07-03,10,5,5",
            "R-4,ortho,DEMO-OL-M3-DI2,DEMO-OL-T-B,DEMO-SITE-IE-001,IE,EU-IE,2026-07-03,10,5,5",
            "R-5,ortho,DEMO-OL-M3-DI,DEMO-OL-T-C,DEMO-SITE-FR-001,IE,EU-IE,2026-07-04,10,5,5",
            "R-6,ortho,DEMO-OL-M3-DI,DEMO-OL-T-D,DEMO-SITE-IE-001,IE,EU-IE,2026-11-20,10,5,5",
            "R-7,ortho,DEMO-OL-M3-DI,DEMO-OL-T-E,DEMO-SITE-IE-001,IE,EU-IE,2026-07-05,10,11,5",
            "R-8,ortho,DEMO-OL-M3-DI,DEMO-OL-T-F,DEMO-SITE-IE-001,IE,EU-IE,2026-07-05,10,5,6",
            "R-9,ortho,DEMO-OL-M3-DI,DEMO-OL-T-G,DEMO-SITE-IE-001,IE,EU-IE,2026-07-05,10,0,1",
            "R-10,ortho,DEMO-OL-M3-DI,DEMO-OL-T-H,DEMO-SITE-GB-001,GB,EU-IE,2026-07-05,10,5,5",
            "R-11,ortho,DEMO-OL-M3-DI,DEMO-OL-T-I,DEMO-SITE-IE-001,IE,EU-IE,2026-07-05,+10,5,5",
            "R-12,ortho,DEMO-OL-M3-DI,DEMO-OL-T-J,DEMO-SITE-IE-001,IE,EU-IE,2026-07-06,10,0,0"));

        Assert.Equal(["2:row_ref_duplicate", "3:row_ref_duplicate", "4:batch_udi_conflict", "5:batch_udi_conflict",
            "6:row_site_country", "7:row_delivery_future", "8:row_units_implanted", "9:row_recipients", "10:row_recipients",
            "11:row_jurisdiction_country", "12:row_units_delivered"], import.Findings.Select(f => $"{f.Line}:{f.Code}"));
        Assert.All(import.Findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Message)));
        Assert.Equal([13], import.Rows.Select(r => r.Line));
        Assert.Equal(new DistributionTotals(12, 1, 11, 10, 0, 0, 0), import.Totals);
        Assert.Equal(DistributionImportStatus.Rejected, import.Status);
        Assert.Equal("import_rejected", Refused(() =>
            ImportRules.Propose(import, Reference(import), "Worldwide", Q3Start, Q3End, Today)).Code);
        Assert.Equal("import_rejected", Refused(() =>
            ImportRules.Affected(import, Reference(import), Ortho, ["DEMO-OL-T-J"], [])).Code);
    }

    [Theory]
    [InlineData(0, "R 1", "row_ref")]
    [InlineData(1, "cardio", "row_product")]
    [InlineData(2, "DEMO-CF-V2-DI", "row_udi_di")]
    [InlineData(3, "LOT-A", "row_batch")]
    [InlineData(4, "DEMO-SITE-IE-1", "row_site")]
    [InlineData(5, "ie", "row_country")]
    [InlineData(6, "EU-ES", "row_market")]
    [InlineData(7, "2026-7-8", "row_date")]
    [InlineData(8, "0", "row_units_delivered")]
    [InlineData(8, "100001", "row_units_delivered")]
    [InlineData(9, "-1", "row_units_implanted")]
    [InlineData(10, "", "row_recipients")]
    public void InvalidDeliveryFieldsBecomeFindings(int field, string value, string code)
    {
        var fields = ValidDelivery.Split(',');
        fields[field] = value;
        var evaluation = ImportRules.EvaluateDistribution(SyntheticCsv.Parse(
            ImportRules.DistributionHeader + "\n" + string.Join(',', fields), ImportRules.DistributionHeader,
            ImportRules.MaximumDistributionRows), Ortho, Today);

        Assert.Empty(evaluation.Rows);
        var finding = Assert.Single(evaluation.Findings);
        Assert.Equal($"2:{code}", $"{finding.Line}:{finding.Code}");
        Assert.False(string.IsNullOrWhiteSpace(finding.Message));
    }

    [Fact]
    public void ShippedDeliveryFileProposesRegionalDenominatorsWithLineProvenance()
    {
        var import = ShippedDistribution();
        var reference = Reference(import);
        Assert.Equal(DistributionImportStatus.Validated, import.Status);
        Assert.Equal(new DistributionTotals(7, 7, 0, 225, 186, 181, 5), import.Totals);
        Assert.Equal(1, import.Rows[0].AdditionalImplants);

        var worldwide = ImportRules.Propose(import, reference, "Worldwide", Q3Start, Q3End, Today);
        Assert.Equal(reference, worldwide.Import);
        Assert.Equal(ImportRules.Unit, worldwide.Unit);
        Assert.Equal([178, 173, 5], new[] { worldwide.Count, worldwide.Recipients, worldwide.AdditionalImplants });
        Assert.Equal([2, 3, 4, 5, 6, 7], worldwide.Lines);
        Assert.Equal(new DateOnly(2026, 7, 8), worldwide.CoveredFrom);
        Assert.Equal(new DateOnly(2026, 10, 6), worldwide.CoveredThrough);
        Assert.Equal(ImportRules.DateBasis, worldwide.DateBasis);
        Assert.Equal(Canonical.Hash(worldwide with { Hash = "" }), worldwide.Hash);
        Assert.Equal(worldwide.Hash, ImportRules.Propose(import, reference, "Worldwide", Q3Start, Q3End, Today).Hash);

        var eea = ImportRules.Propose(import, reference, "EEA-Turkey-NI", Q3Start, Q3End, Today);
        Assert.Equal([148, 143, 5], new[] { eea.Count, eea.Recipients, eea.AdditionalImplants });
        Assert.Equal([2, 3, 4, 5, 7], eea.Lines);
        Assert.NotEqual(worldwide.Hash, eea.Hash);

        Assert.Equal("import_version", Refused(() =>
            ImportRules.Propose(import, reference with { Version = 2 }, "Worldwide", Q3Start, Q3End, Today)).Code);
        var altered = import with { Reason = "Abweichende Begruendung derselben synthetischen Lieferdatei." };
        var stale = Refused(() => ImportRules.Propose(altered, reference, "Worldwide", Q3Start, Q3End, Today));
        Assert.Equal("import_version", stale.Code);
        Assert.Equal(409, stale.Status);
    }

    [Theory]
    [InlineData("Europe", "2026-07-01", "2026-09-30", "region_scope")]
    [InlineData("Worldwide", "2026-09-30", "2026-07-01", "denominator_period")]
    [InlineData("Worldwide", "2026-07-01", "2026-11-19", "denominator_period_open")]
    [InlineData("Worldwide", "2026-01-01", "2026-06-30", "denominator_empty")]
    public void DenominatorProposalNeedsClosedNonEmptyPeriodAndKnownRegion(string region, string start, string end, string code)
    {
        var import = ShippedDistribution();
        Assert.Equal(code, Refused(() => ImportRules.Propose(import, Reference(import), region,
            DateOnly.Parse(start, CultureInfo.InvariantCulture), DateOnly.Parse(end, CultureInfo.InvariantCulture), Today)).Code);
    }

    [Fact]
    public void AffectedDeliveriesUseExactBatchAndJurisdictionMatchesWithMarketScopes()
    {
        var import = ShippedDistribution();
        var reference = Reference(import);

        var affected = ImportRules.Affected(import, reference, Ortho,
            ["DEMO-OL-2026-C", " DEMO-OL-2026-Z ", "DEMO-OL-2026-C"], []);
        Assert.Equal(["DEMO-OL-2026-C", "DEMO-OL-2026-Z"], affected.Batches);
        Assert.Equal(Ortho.Markets.Order(StringComparer.Ordinal).ToArray(), affected.Jurisdictions);
        Assert.Equal(["DEMO-OL-2026-Z"], affected.UnknownBatches);
        Assert.Equal([6, 7, 8], affected.Lines);
        var batch = Assert.Single(affected.Matched);
        Assert.Equal("DEMO-OL-2026-C", batch.Batch);
        Assert.Equal("DEMO-OL-M3-DI", batch.UdiDi);
        Assert.Equal([3, 70, 50, 49], new[] { batch.Rows, batch.UnitsDelivered, batch.UnitsImplanted, batch.Recipients });
        Assert.Equal(["EU-IE", "GB", "NI"], batch.Jurisdictions);
        Assert.Equal(new DateOnly(2026, 9, 2), batch.FirstDelivery);
        Assert.Equal(new DateOnly(2026, 10, 6), batch.LastDelivery);
        Assert.Equal(["EU-IE:IE:1:20:8:8", "GB:GB:1:35:30:30", "NI:GB:1:15:12:11"], affected.Markets.Select(m =>
            $"{m.Jurisdiction}:{m.Country}:{m.Rows}:{m.UnitsDelivered}:{m.UnitsImplanted}:{m.Recipients}"));
        Assert.Equal(["EU-Mitgliedstaat: MDR-Meldeprofil", "GB: Drittland mit eigenem Meldeprofil",
            "NI: eigenes Meldeprofil, getrennt von GB"], affected.Markets.Select(m => m.Scope));
        Assert.Equal(ImportRules.AffectedMethod, affected.Method);
        Assert.Equal(Canonical.Hash(affected with { Hash = "" }), affected.Hash);

        var gb = ImportRules.Affected(import, reference, Ortho, ["DEMO-OL-2026-C"], ["GB"]);
        Assert.Equal(["GB"], gb.Jurisdictions);
        Assert.Equal([6], gb.Lines);
        var prefix = ImportRules.Affected(import, reference, Ortho, ["DEMO-OL-2026"], []);
        Assert.Empty(prefix.Lines);
        Assert.Empty(prefix.Matched);
        Assert.Equal(["DEMO-OL-2026"], prefix.UnknownBatches);

        Assert.Equal("affected_batches", Refused(() => ImportRules.Affected(import, reference, Ortho, ["Charge C"], [])).Code);
        Assert.Equal("affected_batches", Refused(() => ImportRules.Affected(import, reference, Ortho, [], [])).Code);
        Assert.Equal("affected_batches", Refused(() => ImportRules.Affected(import, reference, Ortho, [" "], [])).Code);
        Assert.Equal("affected_batches", Refused(() => ImportRules.Affected(import, reference, Ortho,
            Enumerable.Range(1, 21).Select(i => $"DEMO-OL-T-{i}"), [])).Code);
        Assert.Equal("affected_jurisdiction", Refused(() =>
            ImportRules.Affected(import, reference, Ortho, ["DEMO-OL-2026-C"], ["EU-ES"])).Code);
        Assert.Equal("device_unknown", Refused(() =>
            ImportRules.Affected(import, reference, Cardio, ["DEMO-OL-2026-C"], [])).Code);
        Assert.Equal("import_version", Refused(() =>
            ImportRules.Affected(import, reference with { Version = 2 }, Ortho, ["DEMO-OL-2026-C"], [])).Code);
    }

    [Fact]
    public void ApprovalVersionsTheDenominatorAndKeepsEarlierVersionsAndImportSource()
    {
        var import = ShippedDistribution();
        var proposal = ImportRules.Propose(import, Reference(import), "Worldwide", Q3Start, Q3End, Today);
        var id = ImportRules.ExposureId("ortho", "Worldwide", Q3Start, Q3End);
        Assert.Equal("exposure-ortho-Worldwide-20260701-20260930", id);

        var changed = Refused(() => ImportRules.Approve(null, id, proposal, 177, [], "test-quality", ReceivedAt, Reason));
        Assert.Equal("denominator_changed", changed.Code);
        Assert.Equal(409, changed.Status);
        var first = ImportRules.Approve(null, id, proposal, 178, [], "test-quality", ReceivedAt, Reason);
        Assert.Equal(id, first.Id);
        Assert.Equal(1, first.Version);
        Assert.True(first.Approved);
        Assert.Equal(178, first.Count);
        Assert.Equal(ImportRules.Unit, first.Unit);
        Assert.Equal("Worldwide", first.Region);
        Assert.Equal(Q3Start, first.Start);
        Assert.Equal(Q3End, first.End);
        var source = Assert.IsType<ExposureSource>(first.Source);
        Assert.Equal(ImportRules.SourceContract, source.Contract);
        Assert.Equal(proposal.Import, source.Import);
        Assert.Equal(proposal.Hash, source.ProposalHash);
        Assert.Equal([2, 3, 4, 5, 6, 7], source.Lines);
        Assert.Equal([173, 5], new[] { source.Recipients, source.AdditionalImplants });
        Assert.Equal("test-quality", source.ApprovedBy);
        Assert.Equal(ReceivedAt, source.ApprovedAt);
        Assert.Equal(Reason, source.Reason);
        Assert.DoesNotContain("\"source\"", Canonical.Serialize(first with { Source = null }), StringComparison.Ordinal);
        Assert.Equal(source.Import, Canonical.Read<Exposure>(Canonical.Serialize(first)).Source!.Import);

        Assert.Equal("denominator_unchanged", Refused(() =>
            ImportRules.Approve(first, id, proposal, 178, [first], "test-quality", ReceivedAt, Reason)).Code);
        var eea = ImportRules.Propose(import, Reference(import), "EEA-Turkey-NI", Q3Start, Q3End, Today);
        Assert.Equal("denominator_target", Refused(() =>
            ImportRules.Approve(first, id, eea, 148, [], "test-quality", ReceivedAt, Reason)).Code);
        Assert.Equal("denominator_target", Refused(() =>
            ImportRules.Approve(first, "exposure-ortho-other", proposal, 178, [], "test-quality", ReceivedAt, Reason)).Code);

        var corrected = DistributionImportOf(Shipped(DistributionFile)
            .Replace("2026-07-08,40,36,35", "2026-07-08,40,35,35", StringComparison.Ordinal), "ortho-distribution-2026-q3-corrected.csv");
        var revised = ImportRules.Propose(corrected, Reference(corrected), "Worldwide", Q3Start, Q3End, Today);
        var second = ImportRules.Approve(first, id, revised, 177, [first], "test-quality", ReceivedAt.AddHours(1), Reason);
        Assert.Equal(2, second.Version);
        Assert.Equal(177, second.Count);
        Assert.Equal(corrected.Id, second.Source!.Import.Id);
        Assert.Equal(4, second.Source.AdditionalImplants);
        Assert.NotEqual(import.Id, corrected.Id);
        Assert.Equal(import.Id, first.Source!.Import.Id);
        Assert.Equal(178, first.Count);
    }

    [Fact]
    public void SubregionDenominatorCannotExceedWorldwideBaseOfTheSamePeriod()
    {
        var import = ShippedDistribution();
        var reference = Reference(import);
        var worldwide = ImportRules.Propose(import, reference, "Worldwide", Q3Start, Q3End, Today);
        var eea = ImportRules.Propose(import, reference, "EEA-Turkey-NI", Q3Start, Q3End, Today);
        var worldwideId = ImportRules.ExposureId("ortho", "Worldwide", Q3Start, Q3End);
        var eeaId = ImportRules.ExposureId("ortho", "EEA-Turkey-NI", Q3Start, Q3End);
        Exposure Approve(DenominatorProposal proposal, string id, params Exposure[] others) =>
            ImportRules.Approve(null, id, proposal, proposal.Count, others, "test-quality", ReceivedAt, Reason);

        Assert.Equal("exposure_subset", Refused(() => Approve(eea, eeaId, Other("Worldwide", 100))).Code);
        Assert.Equal("exposure_subset", Refused(() => Approve(worldwide, worldwideId, Other("EEA-Turkey-NI", 200))).Code);
        Assert.Equal("exposure_subset", Refused(() =>
            Approve(worldwide, worldwideId, Other("EEA-Turkey-NI", 10, unit: "DeviceYears"))).Code);

        Assert.Equal(148, Approve(eea, eeaId, Other("Worldwide", 178)).Count);
        Assert.Equal(178, Approve(worldwide, worldwideId,
            Other("EEA-Turkey-NI", 200, approved: false),
            Other("EEA-Turkey-NI", 200, start: Q3Start.AddDays(1)),
            Other("Worldwide", 500, id: "exposure-ortho-other"),
            Other("EEA-Turkey-NI", 200, product: "cardio")).Count);
    }

    [Fact]
    public void ApprovalBindsTheOtherDenominatorsOfThePeriodIncludingAbsentRegions()
    {
        var import = ShippedDistribution();
        var worldwide = ImportRules.Propose(import, Reference(import), "Worldwide", Q3Start, Q3End, Today);
        var eea = ImportRules.Propose(import, Reference(import), "EEA-Turkey-NI", Q3Start, Q3End, Today);
        var worldwideId = ImportRules.ExposureId("ortho", "Worldwide", Q3Start, Q3End);
        var eeaId = ImportRules.ExposureId("ortho", "EEA-Turkey-NI", Q3Start, Q3End);

        // A region without a denominator is reserved as absent, so a parallel first approval cannot slip past the comparison.
        Assert.Equal([new VersionReference(eeaId, 0, "")], ImportRules.PeriodDependencies([], worldwide, worldwideId));
        Assert.Equal([new VersionReference(worldwideId, 0, "")], ImportRules.PeriodDependencies([], eea, eeaId));
        var current = Other("EEA-Turkey-NI", 40) with { Version = 3 };
        Assert.Equal([new VersionReference(eeaId, 3, Canonical.Hash(current))],
            ImportRules.PeriodDependencies([Other("Worldwide", 100), current], worldwide, worldwideId));

        // Pending and differently identified denominators of the period are bound; other periods and products are not compared.
        var pending = Other("EEA-Turkey-NI", 40, approved: false, id: "exposure-ortho-seeded");
        Assert.Equal([new VersionReference(pending.Id, 1, Canonical.Hash(pending))], ImportRules.PeriodDependencies(
            [pending, Other("EEA-Turkey-NI", 40, start: Q3Start.AddDays(1)), Other("EEA-Turkey-NI", 40, product: "cardio")],
            worldwide, worldwideId));
        Assert.Equal(["exposure-a", "exposure-b"], ImportRules.PeriodDependencies(
            [Other("Worldwide", 90, id: "exposure-b"), Other("Worldwide", 100, id: "exposure-a"), Other("EEA-Turkey-NI", 40)], eea, eeaId)
            .Select(d => d.Id));
    }

    private static IntakeEvaluation Intake(params string[] rows) =>
        ImportRules.EvaluateIntake(SyntheticCsv.Parse(ImportRules.IntakeHeader + "\n" + string.Join('\n', rows),
            ImportRules.IntakeHeader, ImportRules.MaximumIntakeRows), Ortho, Channel, ReceivedAt);

    private static string With(int field, string value, string reference = "HF-T-1")
    {
        var fields = ValidIntake.Split(',');
        fields[0] = reference;
        fields[field] = value;
        return string.Join(',', fields);
    }

    private static string Outcome(IntakeImportRow row) => $"{row.Outcome}:{row.Code}";

    private static string Classified(IntakeImportRow row) => $"{row.Outcome}:{row.Code}:{row.Known}";

    private static IntakeImportRow Classify(IntakeImportRow row, IntakeCandidate candidate, params SafetyCase[] cases) =>
        ImportRules.Classify(row, candidate, "ortho", cases, []);

    private static DomainException Refused(Action action) => Assert.Throws<DomainException>(action);

    private static SourceReference Source(string externalId, string hash) =>
        new("src-" + externalId, externalId, "SyntheticComplaint", "Synthetische Kanalquelle", "cohors:synthetic:" + externalId,
            hash, ReceivedAt, ReceivedAt, "Synthetischer Testbezug");

    // The source as registration records it for this candidate: same title, text hash and first awareness.
    private static SourceReference Same(IntakeCandidate candidate) =>
        Source(candidate.SourceId, Canonical.HashText(candidate.Description)) with { Title = candidate.Title, AwarenessAt = candidate.AwarenessAt };

    private static Evidence Occurrence(SourceReference source, DateTimeOffset validFrom) =>
        new(source.Id, 1, "ortho", source.Title, "Complaint", "SYNTHETISCH: Importtestquelle ohne Personenbezug.", source.Hash, source.Uri,
            "Beschwerde: Originaltext", "Synthetic demo, no patient", "Product", validFrom, null, ReceivedAt, true, true, source.Hash);

    private static IntakeImport Earlier(params IntakeImportRow[] rows) =>
        new("intake-earlier", 2, "ortho", Channel, new ImportOriginal("earlier.csv", "cohors:import:earlier", new string('e', 64), 100),
            ReceivedAt.AddDays(-1), "test-pms", Reason, "synthetic-intake-test", "Synthetische Testgrenze", IntakeImportStatus.Processed,
            rows, ImportRules.Count(rows), ReceivedAt.AddDays(-1));

    private static SafetyCase Case(string id, string productId, params SourceReference[] sources) =>
        new SafetyCase(id, 1, "COH-" + id, productId, "Synthetischer Importtestfall", "SYNTHETISCH: Importtestfall ohne Personenbezug.",
            null, null, "IE", "EU-IE", Seriousness.OtherSerious, null, ReceivedAt, ReceivedAt,
            null, "channel-import", true, CaseStage.Intake, Disposition.Undetermined,
            [], [], [], [], [], [], [], [], [], [], [], []) with { Sources = sources };

    // The row facts that registration copies into a case it creates for this candidate.
    private static SafetyCase Recorded(SafetyCase target, IntakeCandidate candidate) =>
        target with
        {
            Udi = candidate.Udi, Batch = candidate.Batch, Country = candidate.Country, Jurisdiction = candidate.Jurisdiction,
            Seriousness = candidate.Seriousness
        };

    private static Exposure Other(string region, int count, string unit = ImportRules.Unit, bool approved = true,
        DateOnly? start = null, string? id = null, string product = "ortho")
    {
        var from = start ?? Q3Start;
        return new(id ?? ImportRules.ExposureId(product, region, from, Q3End), 1, product, region, from, Q3End, unit, count,
            "Synthetische Vergleichsbasis", "Bestehender Testnenner", approved);
    }

    private static DistributionImport ShippedDistribution() => DistributionImportOf(Shipped(DistributionFile), DistributionFile);

    private static DistributionImport DistributionImportOf(string content, string fileName = "deliveries.csv")
    {
        var original = ImportRules.Original("distribution", fileName, content);
        var records = SyntheticCsv.Parse(content, ImportRules.DistributionHeader, ImportRules.MaximumDistributionRows);
        var evaluation = ImportRules.EvaluateDistribution(records, Ortho, Today);
        return new(ImportRules.DistributionId("ortho", original.Hash), 1, "ortho", original, ReceivedAt, "test-pms",
            "Synthetischer Lieferimport fuer die Nennerpruefung.", ImportRules.DistributionContract, ImportRules.DistributionBoundary,
            evaluation.Findings.Length == 0 ? DistributionImportStatus.Validated : DistributionImportStatus.Rejected,
            evaluation.Rows, evaluation.Findings, ImportRules.Totals(records.Length, evaluation));
    }

    private static VersionReference Reference(DistributionImport import) => new(import.Id, import.Version, Canonical.Hash(import));

    private static string Shipped(string fileName) => File.ReadAllText(Path.Combine(Home, "demo", "imports", fileName));

    private static string FindHome()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Cohors.slnx"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("Run from the Cohors worktree.");
    }
}
