using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cohors.Agents;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Cohors.Tests;

[CollectionDefinition("Local recovery", DisableParallelization = true)]
public sealed class RecoveryCollection : ICollectionFixture<RecoveryFixture>;

[Collection("Local recovery")]
[Trait("Category", "Sql")]
[Trait("Category", "Recovery")]
public sealed class RecoveryTests : IDisposable
{
    private readonly RecoveryFixture fixture;

    public RecoveryTests(RecoveryFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
    {
        this.fixture = fixture;
        fixture.DiagnosticOutput = output;
    }

    public void Dispose() => fixture.DiagnosticOutput = null;

    [Fact]
    public async Task T20_RestoresHistoricalBytesFrozenPackageUnknownDeadlinesReceiptsAndLocalSearchWithoutSource()
    {
        await using var target = await fixture.NewTarget();
        await fixture.Restore(target);
        await fixture.RequireSourceUnavailable();
        var sql = new SqlStateStore(target.Connection);
        var objects = new LocalObjectStore(target.ObjectRoot);
        var search = new KnowledgeSearch(sql, objects, sql);
        var clock = new TestClock(DateTimeOffset.Parse("2026-12-16T15:00:00Z"));
        var service = new CaseService(sql, objects, fixture.Policies,
            new AgentRunner(new(null, null, null, null)), search, clock, new MirComposer());
        var regulatory = new RegulatoryService(sql, objects, service, new MirComposer(), fixture.Policies, clock);

        Assert.Equal(fixture.SourceTables, await RecoveryFixture.Tables(target.Connection));
        foreach (var item in fixture.SourceBytes)
            Assert.Equal(item.Value, await File.ReadAllBytesAsync(Path.Combine(target.ObjectRoot, item.Key)));
        var retention = new RetentionService(sql, sql, objects, fixture.Policies, new(true), clock);
        var deletedRaw = await retention.Get(fixture.DeletedRaw.Id, SqlFixture.Quality);
        Assert.Equal(Canonical.Serialize(fixture.DeletedRaw), Canonical.Serialize(deletedRaw));
        Assert.Equal(fixture.ActiveRawText, await objects.ReadRaw(fixture.ActiveRaw.Object));
        Assert.False(File.Exists(Path.Combine(target.ObjectRoot, "raw-quarantine", deletedRaw.Object.Hash)));
        Assert.False(File.Exists(Path.Combine(target.ObjectRoot, deletedRaw.Object.Hash)));
        Assert.Equal(Canonical.Serialize(deletedRaw.Deletion), await File.ReadAllTextAsync(
            Path.Combine(target.ObjectRoot, ".deletion-receipts", deletedRaw.Object.Hash + ".json")));
        Assert.Equal("object_retired", (await Assert.ThrowsAsync<DomainException>(() =>
            objects.CreateSyntheticRaw(fixture.DeletedRawText))).Code);
        await sql.Manifest(deletedRaw.Id, kind: "retention");
        var restored = await service.Get(fixture.SourceCase.Id, SqlFixture.Ra);
        Assert.Equal(Canonical.Serialize(fixture.SourceCase), Canonical.Serialize(restored));
        Assert.Equal(Encoding.UTF8.GetBytes(fixture.SourceCase.CurrentReport!.FrozenJson!),
            Encoding.UTF8.GetBytes(restored.CurrentReport!.FrozenJson!));
        Assert.Equal(ReportState.TransmissionUnknown, restored.CurrentReport.State);
        Assert.Equal(fixture.UnknownCorrelation, restored.Submissions.Single(s => s.Status == "TransmissionUnknown").CorrelationId);
        Assert.Null(restored.Submissions.Single(s => s.Status == "TransmissionUnknown").ReceiptReference);
        Assert.Equal("Open", Assert.Single(restored.FollowUps!).Status);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T23:59:59.9999999Z"), restored.FollowUps![0].Schedule.DueAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T11:59:59.9999999Z"), restored.FollowUps[0].Schedule.SafetyDue);
        Assert.Contains(restored.Deadlines, d => d.Jurisdiction == "NI" && d.Status == "Open");
        Assert.Equal(Canonical.Serialize(fixture.SourceCase.Deadlines), Canonical.Serialize(restored.Deadlines));
        Assert.Equal(Canonical.Serialize(fixture.SourceCase.FollowUps), Canonical.Serialize(restored.FollowUps));
        Assert.Equal(fixture.SourceCase.FirstAwarenessAt, restored.FirstAwarenessAt);
        Assert.Equal(fixture.SourceCase.SystemReceivedAt, restored.SystemReceivedAt);
        Assert.Equal(fixture.SourceCase.CurrentReport.Mir!.Xml, (await regulatory.Export(restored.Id, SqlFixture.Ra)).Xml);
        Assert.NotEmpty(await sql.Audit(restored.Id));
        await sql.Manifest(restored.Id);
        await sql.Manifest(fixture.CurrentEvidence.Id, kind: "evidence");

        var historical = (await search.Retrieve("ortho", fixture.CaptureTime, "specification", SqlFixture.Pms))
            .Single(e => e.Id == fixture.OriginalEvidence.Id);
        var current = (await search.Retrieve("ortho", fixture.CurrentEvidence.ValidFrom.AddHours(1), "specification", SqlFixture.Pms))
            .Single(e => e.Id == fixture.OriginalEvidence.Id);
        Assert.Equal(fixture.OriginalEvidence, historical);
        Assert.Equal(fixture.CurrentEvidence, current);
        Assert.Equal(fixture.OriginalEvidence.Hash, restored.Evidence.Single(e => e.Id == historical.Id).Hash);
        Assert.Equal(historical.Content, await objects.Read(historical.Hash));
        Assert.Equal(current.Content, await objects.Read(current.Hash));
        Assert.All(restored.CurrentAssessment!.Content.Claims.SelectMany(c => c.Citations),
            citation => Assert.True(EvidenceRules.CitationMatches(citation, restored.Evidence)));
        await Assert.ThrowsAsync<DomainException>(() => search.Retrieve("ortho", clock.Now, "",
            SqlFixture.Pms with { Products = ["cardio"] }));
        await using (var connection = new SqlConnection(target.Connection))
        {
            await connection.OpenAsync();
            using var query = new SqlCommand("""
                SELECT COUNT(*) FROM dbo.SearchProjection p
                FULL JOIN (SELECT h.Id+'-revision-'+CONVERT(varchar(12),h.Version) Id,
                    h.Version,e.ProductId,e.Content,e.Hash FROM dbo.AggregateVersions h
                    CROSS APPLY OPENJSON(h.Json) WITH (ProductId nvarchar(40) '$.productId',
                        Content nvarchar(max) '$.content',Hash char(64) '$.hash',Approved bit '$.approved') e
                    WHERE h.Kind='evidence' AND e.Approved=1) h ON p.Id=h.Id
                WHERE p.Id IS NULL OR h.Id IS NULL OR p.Version<>h.Version OR p.ProductId<>h.ProductId
                    OR p.Content COLLATE Latin1_General_100_BIN2<>h.Content COLLATE Latin1_General_100_BIN2 OR p.Hash<>h.Hash
                """, connection);
            Assert.Equal(0, (int)(await query.ExecuteScalarAsync())!);
            query.CommandText = "SELECT COUNT(*) FROM dbo.SearchProjection WHERE Id=@old OR Hash=@hash";
            query.Parameters.AddWithValue("@old", fixture.PoisonedProjectionId);
            query.Parameters.AddWithValue("@hash", new string('0', 64));
            Assert.Equal(0, (int)(await query.ExecuteScalarAsync())!);
        }

        await fixture.Imports.Verify(fixture, target, sql, objects, service, clock);

        var duplicate = await service.Submit(restored.Id, fixture.UnknownCommand, SqlFixture.Ra, fixture.UnknownKey);
        Assert.Equal(Canonical.Serialize(fixture.UnknownReceipt), Canonical.Serialize(duplicate));
        var refused = await Assert.ThrowsAsync<DomainException>(() => service.Submit(restored.Id,
            new(restored.Version, "Unknown", "Keinen ungeklaerten simulierten Versand automatisch wiederholen."),
            SqlFixture.Ra, CaseScenario.Key()));
        Assert.Equal("reconciliation_required", refused.Code);
        var processor = new OutboxProcessor(sql, service, search);
        Assert.True(await sql.HasReceipt(fixture.CompletedEvent, RecoveryFixture.Consumer));
        Assert.Equal("Handled", (await processor.Process(fixture.CompletedEvent)).State);
        Assert.Equal(fixture.SourceTables, await RecoveryFixture.Tables(target.Connection));
        Assert.False(await sql.HasReceipt(fixture.PendingEvent, RecoveryFixture.Consumer));
        Assert.Equal("Handled", (await processor.Process(fixture.PendingEvent)).State);
        Assert.True(await sql.HasReceipt(fixture.PendingEvent, RecoveryFixture.Consumer));
        var afterPending = await RecoveryFixture.Tables(target.Connection);
        Assert.Equal("Handled", (await processor.Process(fixture.PendingEvent)).State);
        Assert.Equal(afterPending, await RecoveryFixture.Tables(target.Connection));
        Assert.Equal(Canonical.Serialize(fixture.SourceCase), Canonical.Serialize(await service.Get(restored.Id, SqlFixture.Ra)));
        await sql.Manifest(restored.Id);
        fixture.ImportRecoveryVerified = true;
        fixture.Passed("positive: source unavailable; native SQL, historical/current evidence, retained raw, deleted absence and exact journal, frozen MIR, deadlines, unknown and idempotent outbox; Block-1 intake/distribution imports with original CSV bytes, row outcomes, history/audit and approved exposure source");
    }

    [Fact]
    public async Task T20_RestoresGroupedPsurSeriesAndBsCutoffHistoryWithoutRebindingOrCompletingPendingReplies()
    {
        await using var target = await fixture.NewTarget();
        await fixture.Restore(target);
        await fixture.RequireSourceUnavailable();
        var sql = new SqlStateStore(target.Connection);
        var objects = new LocalObjectStore(target.ObjectRoot);
        var clock = new TestClock(DateTimeOffset.Parse("2028-12-16T15:00:00Z"));
        var analytics = new AnalyticsService(sql, new AgentRunner(new(null, null, null, null)), fixture.Policies, clock);
        var reviews = new PsurReviewService(sql, analytics, clock);
        Assert.Equal(fixture.SourceTables, await RecoveryFixture.Tables(target.Connection));
        Assert.Equal(fixture.SourcePsurHistory, await RecoveryFixture.PsurHistory(sql));

        var expectedReports = new[] { fixture.InitialPsur, fixture.OtherSeriesPsur, fixture.NextPsur };
        Assert.Equal(expectedReports.Select(p => p.Id).Order(StringComparer.Ordinal),
            (await analytics.ListPsurs(SqlFixture.Pms)).Select(p => p.Id).Order(StringComparer.Ordinal));
        Assert.Equal(Canonical.Serialize(fixture.CurrentPsurGroup),
            Canonical.Serialize((await analytics.ListGroups(SqlFixture.Pms)).Single(g => g.Id == fixture.CurrentPsurGroup.Id)));
        foreach (var expected in expectedReports)
        {
            var actual = await analytics.GetVerifiedPsur(expected.Id, SqlFixture.Ra);
            Assert.Equal(Canonical.Serialize(expected), Canonical.Serialize(actual));
            Assert.Equal(expected.SnapshotHash, Canonical.Hash(actual.Snapshot));
            Assert.Equal(expected.ScopeHash, PortfolioRules.PsurScope(actual));
            Assert.Equal(Encoding.UTF8.GetBytes(expected.Draft), Encoding.UTF8.GetBytes(actual.Draft));
            RequireSeparateApprovals(actual.Approvals, actual.ScopeHash);
            var group = Assert.IsType<PsurGrouping>(actual.Snapshot.Group);
            Assert.Equal(new[] { "cardio", "ortho" }, group.Members.Select(m => m.Product.ProductId).Order(StringComparer.Ordinal));
            foreach (var member in group.Members)
            {
                var original = expected.Snapshot.Group!.Members.Single(m => m.Product.ProductId == member.Product.ProductId);
                Assert.Equal(Canonical.Serialize(original), Canonical.Serialize(member));
                Assert.Equal(member.Product.ProductId, member.Snapshot.ProductId);
                Assert.Empty(member.Snapshot.RegulatoryHistory!.Reviews);
                foreach (var document in member.Snapshot.Documents)
                {
                    Assert.Equal(member.Product.ProductId, document.ProductId);
                    Assert.Equal(fixture.SourceBytes[document.ObjectKey],
                        await File.ReadAllBytesAsync(Path.Combine(target.ObjectRoot, document.ObjectKey)));
                    Assert.Equal(document.Content, await objects.Read(document.ObjectKey));
                }
            }
            await sql.Manifest(actual.Id, kind: "psur");
            Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
                analytics.GetVerifiedPsur(actual.Id, SqlFixture.Ra with { Products = [actual.ProductId] }))).Status);
        }
        Assert.Empty(await analytics.ListPsurs(SqlFixture.Pms with { Products = ["ortho"] }));
        Assert.Empty(await analytics.ListGroups(SqlFixture.Pms with { Products = ["cardio"] }));

        var initial = await analytics.GetVerifiedPsur(fixture.InitialPsur.Id, SqlFixture.Pms);
        var other = await analytics.GetVerifiedPsur(fixture.OtherSeriesPsur.Id, SqlFixture.Pms);
        var next = await analytics.GetVerifiedPsur(fixture.NextPsur.Id, SqlFixture.Pms);
        var initialSeries = Assert.IsType<PsurSeries>(initial.Snapshot.Series);
        var nextSeries = Assert.IsType<PsurSeries>(next.Snapshot.Series);
        var nextGroup = Assert.IsType<PsurGrouping>(next.Snapshot.Group);
        Assert.Equal(initialSeries.Reference, nextSeries.Reference);
        Assert.NotEqual(initialSeries.Reference, other.Snapshot.Series!.Reference);
        Assert.Equal(new VersionReference(initial.Id, initial.Version, Canonical.Hash(initial)), nextSeries.PreviousReport!.Source);
        Assert.Equal(initialSeries.ReportVersion + 1, nextSeries.ReportVersion);
        Assert.Equal(initial.Snapshot.End.AddDays(1), next.Snapshot.Start);
        Assert.Equal(initial.Snapshot.Group!.DefinitionSource.Version + 1, nextGroup.DefinitionSource.Version);
        Assert.Equal(Canonical.Serialize(fixture.OriginalEvidence), Canonical.Serialize(
            PsurGroupRules.Documents(initial.Snapshot).Single(e => e.Id == fixture.OriginalEvidence.Id)));
        Assert.Equal(Canonical.Serialize(fixture.CurrentEvidence), Canonical.Serialize(
            PsurGroupRules.Documents(next.Snapshot).Single(e => e.Id == fixture.CurrentEvidence.Id)));
        foreach (var product in new[] { "ortho", "cardio" })
        {
            var member = nextGroup.Members.Single(m => m.Product.ProductId == product);
            var exposure = member.Snapshot.Periods.Single(p => p.Label == "N").Regions.Single(r => r.Region == "Worldwide");
            var source = fixture.NextPsur.Snapshot.Group!.Members.Single(m => m.Product.ProductId == product)
                .Snapshot.Exposure.Single(e => e.Start == next.Snapshot.Start && e.Region == "Worldwide");
            Assert.Equal(source.Count, exposure.Exposure);
            Assert.Equal(source.Id, exposure.ExposureId);
            Assert.Equal(source.Version, exposure.ExposureVersion);
        }

        var closed = await reviews.Get(fixture.ClosedPsurReview.Id, SqlFixture.Pms);
        var pending = await reviews.Get(fixture.PendingPsurReview.Id, SqlFixture.Pms);
        Assert.Equal(Canonical.Serialize(fixture.ClosedPsurReview), Canonical.Serialize(closed));
        Assert.Equal(Canonical.Serialize(fixture.PendingPsurReview), Canonical.Serialize(pending));
        Assert.Equal("QuestionClosed", closed.Status);
        Assert.Equal(2, closed.Questions.Length);
        Assert.Equal(2, closed.Submissions.Length);
        Assert.Equal(new[] { "FurtherResponseRequired", "NoFurtherQuestion" }, closed.Assessments.Select(a => a.Outcome));
        foreach (var submission in closed.Submissions)
        {
            RequireSeparateApprovals(submission.Approvals, submission.ScopeHash);
            Assert.Equal(Canonical.Serialize(PsurReviewRules.Bind(initial)), Canonical.Serialize(submission.Reply.Report));
            foreach (var source in submission.Reply.Sources)
                Assert.Contains(PsurGroupRules.Snapshots(initial.Snapshot).SelectMany(s => s.Directory!.Entries), e => e == source);
        }
        Assert.Equal(new VersionReference(fixture.VerifiedPsurCapa.Id, fixture.VerifiedPsurCapa.Version,
            Canonical.Hash(fixture.VerifiedPsurCapa)), Assert.Single(closed.Assessments[^1].ActionEvidence).Source);
        Assert.Equal("MedicalApproved", pending.Status);
        Assert.Equal(Roles.Medical, Assert.Single(pending.Approvals).Role);
        Assert.Equal(pending.ScopeHash, PsurReviewRules.Scope(pending));
        Assert.Empty(pending.Submissions);
        Assert.Empty(pending.Assessments);
        var expectedReviewIds = new[] { closed.Id, pending.Id }.Order(StringComparer.Ordinal);
        Assert.Equal(expectedReviewIds, (await reviews.List(next.Id, SqlFixture.Ra)).Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.Empty(await reviews.List(other.Id, SqlFixture.Ra));
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            reviews.Get(closed.Id, SqlFixture.Pms with { Products = ["ortho"] }))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            reviews.List(next.Id, SqlFixture.Pms with { Products = ["ortho"] }))).Status);

        var earlier = Assert.Single(other.Snapshot.Group!.RegulatoryHistory!.Reviews);
        Assert.Equal("PostCutoffAddendum", earlier.Selection);
        Assert.Null(earlier.AtCutoff);
        Assert.Null(earlier.CutoffSource);
        Assert.Equal(Canonical.Serialize(fixture.PsurReviewAtCutoff), Canonical.Serialize(earlier.CurrentAtFreeze));
        var nextHistory = Assert.IsType<PsurRegulatoryHistory>(nextGroup.RegulatoryHistory);
        var historical = nextHistory.Reviews.Single(r => r.CurrentSource.Id == closed.Id);
        Assert.Equal("OpenInPeriod", historical.Selection);
        Assert.Equal("ReplySubmitted", historical.StatusAtCutoff);
        Assert.Equal(new VersionReference(fixture.PsurReviewAtCutoff.Id, fixture.PsurReviewAtCutoff.Version,
            Canonical.Hash(fixture.PsurReviewAtCutoff)), historical.CutoffSource);
        Assert.Equal(Canonical.Serialize(fixture.PsurReviewAtCutoff), Canonical.Serialize(historical.AtCutoff));
        Assert.Equal(Canonical.Serialize(closed), Canonical.Serialize(historical.CurrentAtFreeze));
        var postCutoff = nextHistory.Reviews.Single(r => r.CurrentSource.Id == pending.Id);
        Assert.Equal("PostCutoffAddendum", postCutoff.Selection);
        Assert.Equal("NotRecordedAtCutoff", postCutoff.StatusAtCutoff);
        Assert.Null(postCutoff.AtCutoff);
        Assert.Equal(Canonical.Serialize(pending), Canonical.Serialize(postCutoff.CurrentAtFreeze));
        var action = nextGroup.Members.Single(m => m.Product.ProductId == "cardio").Snapshot.Actions!.Capas
            .Single(c => c.CurrentSource.Id == fixture.VerifiedPsurCapa.Id);
        Assert.Equal("Open", action.StatusAtCutoff);
        Assert.Equal(Canonical.Serialize(fixture.PsurCapaAtCutoff), Canonical.Serialize(action.AtCutoff));
        Assert.Equal(Canonical.Serialize(fixture.VerifiedPsurCapa), Canonical.Serialize(action.CurrentAtFreeze));
        foreach (var review in new[] { closed, pending }) await sql.Manifest(review.Id, kind: "psurReview");
        foreach (var group in await analytics.ListGroups(SqlFixture.Pms)) await sql.Manifest(group.Id, kind: "psurGroup");
        await sql.Manifest(fixture.VerifiedPsurCapa.Id, kind: "capa");
        Assert.Equal(Canonical.Serialize(fixture.PsurReviewAtCutoff), Canonical.Serialize(
            await reviews.Simulate(closed.Id, fixture.PsurReplyCommand, SqlFixture.Ra, fixture.PsurReplyKey)));
        Assert.Equal(fixture.SourceTables, await RecoveryFixture.Tables(target.Connection));
        fixture.PsurRecoveryVerified = true;
        fixture.Passed("PSUR/BS: exact grouped series, approvals, historical/current sources and cutoff/addendum snapshots; pending reply preserved; replay made no writes");
    }

    private static void RequireSeparateApprovals(Approval[] approvals, string scope)
    {
        Assert.Equal(2, approvals.Length);
        var medical = Assert.Single(approvals, a => a.Role == Roles.Medical);
        var regulatory = Assert.Single(approvals, a => a.Role == Roles.Regulatory);
        Assert.NotEqual(medical.ActorId, regulatory.ActorId);
        Assert.All(approvals, a => Assert.Equal(scope, a.ScopeHash));
    }

    [Fact]
    public async Task T20_RawRestoreRequiresCurrentCatalogueAndRejectsARealPreDeletionBackup()
    {
        await using (var missing = await fixture.NewTarget())
        {
            var result = await fixture.Script("Restore-Local.ps1", missing.ConfigPath, fixture.BackupDirectory,
                "-ManifestSha256", fixture.ManifestHash);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("recovery_retention_checkpoint", result.Codes);
            await fixture.RequireNoTarget(missing);
        }
        await using (var older = await fixture.NewTarget())
        {
            var result = await fixture.RestoreResult(older, fixture.PreDeletionBackupDirectory, fixture.PreDeletionManifestHash);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("recovery_retention_stale_cut", result.Codes);
            await fixture.RequireNoTarget(older);
        }
        await fixture.RequireSentinel();
        fixture.Passed("retention freshness: missing current catalogue and real older native backup rejected before activation");
    }

    [Fact]
    public async Task T20_DeletedRawRequiresItsJournalAndCannotWaiveSharedReferencesOrResurrectBytes()
    {
        foreach (var mutation in new[] { "missing-journal", "raw-reappeared", "shared-reference" })
        {
            await using var target = await fixture.NewTarget();
            var copy = fixture.CopyBackup(mutation);
            var digest = fixture.ManifestHash;
            var expected = "recovery_object_missing";
            var objects = Path.Combine(copy, "objects");
            if (mutation == "missing-journal")
                File.Delete(Path.Combine(objects, ".deletion-receipts", fixture.DeletedRaw.Object.Hash + ".json"));
            else
            {
                var path = Path.Combine(copy, "manifest.json");
                var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
                if (mutation == "raw-reappeared")
                {
                    var bytes = Encoding.UTF8.GetBytes(fixture.DeletedRawText);
                    await File.WriteAllBytesAsync(Path.Combine(objects, "raw-quarantine", fixture.DeletedRaw.Object.Hash), bytes);
                    var inventory = manifest["objects"]!.AsArray();
                    var item = new JsonObject { ["dataClass"] = "RawQuarantine", ["objectKey"] = fixture.DeletedRaw.Object.ObjectKey,
                        ["hash"] = fixture.DeletedRaw.Object.Hash, ["length"] = bytes.LongLength };
                    var index = inventory.Select((entry, position) => (Key: entry!["objectKey"]?.GetValue<string>(), position))
                        .Where(entry => entry.Key?.StartsWith(RetentionRules.RawPrefix, StringComparison.Ordinal) == true &&
                            StringComparer.Ordinal.Compare(entry.Key, fixture.DeletedRaw.Object.ObjectKey) > 0)
                        .Select(entry => entry.position).DefaultIfEmpty(inventory.Count).First();
                    inventory.Insert(index, item);
                    expected = "recovery_retention_resurrection";
                }
                else
                {
                    manifest["references"]!.AsArray().Add(fixture.DeletedRaw.Object.Hash);
                    expected = "recovery_retention_reference";
                }
                await File.WriteAllTextAsync(path, manifest.ToJsonString());
                digest = RecoveryFixture.FileHash(path);
            }
            var result = await fixture.RestoreResult(target, copy, digest);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(expected, result.Codes);
            await fixture.RequireNoTarget(target);
            await fixture.RequireSentinel();
        }
        Assert.Equal(fixture.ManifestHash, RecoveryFixture.FileHash(Path.Combine(fixture.BackupDirectory, "manifest.json")));
        fixture.Passed("retention negative cuts: missing journal, reappeared raw bytes and independent shared reference all rejected");
    }

    [Theory]
    [InlineData("referenced-object-missing", "recovery_object_missing")]
    [InlineData("referenced-object-altered", "recovery_object_hash")]
    [InlineData("inconsistent-sql-object-cut", "recovery_reference_coverage")]
    public async Task T20_DamagedOrOlderObjectCutNeverActivatesTarget(string mutation, string expectedCode)
    {
        await using var target = await fixture.NewTarget();
        var copy = fixture.CopyBackup(mutation);
        var digest = fixture.ManifestHash;
        var objects = Path.Combine(copy, "objects");
        if (mutation == "referenced-object-missing")
            File.Delete(Path.Combine(objects, fixture.OriginalEvidence.Hash));
        else if (mutation == "referenced-object-altered")
        {
            var path = Path.Combine(objects, fixture.OriginalEvidence.Hash);
            var bytes = await File.ReadAllBytesAsync(path);
            bytes[0] ^= 1;
            await File.WriteAllBytesAsync(path, bytes);
        }
        else
        {
            // A self-consistent older inventory with valid bytes, but the newer SQL backup.
            File.Delete(Path.Combine(objects, fixture.CurrentEvidence.Hash));
            var path = Path.Combine(copy, "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            manifest["objects"] = new JsonArray(manifest["objects"]!.AsArray()
                .Where(o => o!["hash"]!.GetValue<string>() != fixture.CurrentEvidence.Hash).Select(o => o!.DeepClone()).ToArray());
            manifest["references"] = new JsonArray(manifest["references"]!.AsArray()
                .Where(o => o!.GetValue<string>() != fixture.CurrentEvidence.Hash).Select(o => o!.DeepClone()).ToArray());
            await File.WriteAllTextAsync(path, manifest.ToJsonString());
            digest = RecoveryFixture.FileHash(path);
        }
        var result = await fixture.RestoreResult(target, copy, digest);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedCode, result.Codes);
        await fixture.RequireNoTarget(target);
        await fixture.RequireSentinel();
        if (mutation == "referenced-object-altered")
        {
            var path = Path.Combine(objects, fixture.OriginalEvidence.Hash);
            await File.WriteAllBytesAsync(path, [0xef, 0xbb, 0xbf, .. fixture.SourceBytes[fixture.OriginalEvidence.Hash]]);
            Assert.Equal(fixture.OriginalEvidence.Content, await File.ReadAllTextAsync(path));
            result = await fixture.RestoreResult(target, copy, digest);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("recovery_object_hash", result.Codes);
            await fixture.RequireNoTarget(target);
            await fixture.RequireSentinel();
        }
        Assert.Equal(fixture.ManifestHash, RecoveryFixture.FileHash(Path.Combine(fixture.BackupDirectory, "manifest.json")));
        fixture.Passed(mutation + ": rejected without activation; unrelated sentinel unchanged");
    }

    [Theory]
    [InlineData("database")]
    [InlineData("objects")]
    public async Task T20_ExistingDatabaseOrObjectRootIsNeverReplaced(string existing)
    {
        await using var target = await fixture.NewTarget();
        const string sentinel = "synthetic-existing-target-must-survive";
        if (existing == "database")
        {
            await fixture.ExecuteMaster($"CREATE DATABASE [{target.Database}]");
            target.OwnsDatabase = true;
            await RecoveryFixture.Execute(target.Connection,
                "CREATE TABLE dbo.Preserved(Value nvarchar(100) NOT NULL); INSERT dbo.Preserved VALUES(N'" + sentinel + "')");
        }
        else
        {
            Directory.CreateDirectory(target.ObjectRoot);
            await File.WriteAllTextAsync(Path.Combine(target.ObjectRoot, "preserved.txt"), sentinel);
        }
        var result = await fixture.RestoreResult(target, fixture.BackupDirectory, fixture.ManifestHash);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("recovery_target_exists", result.Codes);
        Assert.False(File.Exists(target.ReadyPath));
        if (existing == "database")
        {
            Assert.Equal(sentinel, await RecoveryFixture.Scalar(target.Connection, "SELECT Value FROM dbo.Preserved"));
            Assert.False(Directory.Exists(target.ObjectRoot));
        }
        else
        {
            Assert.Equal(sentinel, await File.ReadAllTextAsync(Path.Combine(target.ObjectRoot, "preserved.txt")));
            Assert.False(await fixture.DatabaseExists(target.Database));
        }
        await fixture.RequireSentinel();
        fixture.Passed("existing-" + existing + ": rejected; existing bytes/SQL preserved");
    }

    [Fact]
    public async Task T20_BackupRequiresReadOnlyCutAndDoesNotOverwriteExistingBackup()
    {
        await using var target = await fixture.NewTarget();
        await fixture.Restore(target);
        var refusedBackup = Path.Combine(fixture.Directory, "writable-cut");
        var result = await fixture.Script("Backup-Local.ps1", target.ConfigPath, refusedBackup, "-Quiesced");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("recovery_quiescence", result.Codes);
        Assert.False(Directory.Exists(refusedBackup));
        var before = RecoveryFixture.FileHash(Path.Combine(fixture.BackupDirectory, "database.bak"));
        result = await fixture.Script("Backup-Local.ps1", target.ConfigPath, fixture.BackupDirectory, "-Quiesced");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("recovery_target_exists", result.Codes);
        Assert.Equal(before, RecoveryFixture.FileHash(Path.Combine(fixture.BackupDirectory, "database.bak")));
        Assert.Equal(fixture.ManifestHash, RecoveryFixture.FileHash(Path.Combine(fixture.BackupDirectory, "manifest.json")));
        fixture.Passed("backup guards: writable source and existing backup rejected");
    }

    [Fact]
    public async Task T20_FailedSqlStartRemovesOnlyItsIdentifiedContainerAndVolume()
    {
        await fixture.CheckFailedSqlStart();
        await fixture.RequireSentinel();
        fixture.Passed("failed SQL start: immutable ID and operation label captured; own residue removed; existing target preserved");
    }
}

[Trait("Category", "Recovery")]
public sealed class RecoveryPathTests
{
    [Fact]
    public async Task T20_PathBoundariesUseActualPlatformCasingWithoutCreatingForeignDirectories()
    {
        var home = RecoveryFixture.RepositoryRoot();
        var candidate = Path.Combine(home, ".LOCAL", "path-boundary-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
            Assert.Equal(candidate, RecoveryFixture.RequireNewDirectory(home, candidate));
        else
            Assert.Throws<InvalidOperationException>(() => RecoveryFixture.RequireNewDirectory(home, candidate));
        var canonical = Path.Combine(home, ".local", "path-boundary-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(canonical, RecoveryFixture.RequireNewDirectory(home, canonical + Path.DirectorySeparatorChar));
        Assert.False(Directory.Exists(candidate));

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("COHORS_RECOVERY_PWSH") ?? "pwsh")
        {
            WorkingDirectory = home, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["COHORS_RECOVERY_PATH_HOME"] = home;
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("""
            $ErrorActionPreference = 'Stop'
            . (Join-Path $env:COHORS_RECOVERY_PATH_HOME 'scripts\Backup-Local.ps1')
            $candidate = Join-Path $env:COHORS_RECOVERY_PATH_HOME ('.LOCAL\path-boundary-' + [Guid]::NewGuid().ToString('N'))
            $rejected = $false
            try { Assert-RecoveryPath $candidate -New | Out-Null }
            catch {
                if (-not $_.Exception.Message.StartsWith('recovery_path:')) { throw }
                $rejected = $true
            }
            if ($rejected -eq $IsWindows) { throw 'Incorrect platform-specific .local boundary.' }
            if (Test-Path -LiteralPath $candidate) { throw 'The path check created a foreign directory.' }
            $first = Join-Path $env:COHORS_RECOVERY_PATH_HOME '.local\caseobjects'
            $second = Join-Path $env:COHORS_RECOVERY_PATH_HOME '.local\CASEOBJECTS\child'
            $overlap = $false
            try { Assert-RecoverySeparatePaths $first $second }
            catch {
                if (-not $_.Exception.Message.StartsWith('recovery_path_overlap:')) { throw }
                $overlap = $true
            }
            if ($overlap -ne $IsWindows) { throw 'Incorrect platform-specific overlap boundary.' }
            $overlap = $false
            try { Assert-RecoverySeparatePaths ($first + [IO.Path]::DirectorySeparatorChar) (Join-Path $first 'child') }
            catch {
                if (-not $_.Exception.Message.StartsWith('recovery_path_overlap:')) { throw }
                $overlap = $true
            }
            if (-not $overlap) { throw 'A trailing separator bypassed the overlap boundary.' }
            $script:dockerInvoked = $false
            function Invoke-RecoveryDocker([string[]]$Arguments) {
                $script:dockerInvoked = $true
                throw 'No Docker operation is permitted by this scope-refusal test.'
            }
            $container = 'a' * 64
            $native = '/var/opt/mssql/data/cohors_recovery_' + ('b' * 32)
            foreach ($invalid in @(
                @{ container = 'not-an-owned-id'; path = "$native-backup/database.bak" },
                @{ container = $container; path = '/var/opt/mssql/data/master.mdf' },
                @{ container = $container; path = "$native.bak" },
                @{ container = $container; path = "$native-backup/../database.bak" }
            )) {
                $rejected = $false
                try { Set-RecoverySqlBackupAccess $invalid.container $invalid.path }
                catch {
                    if (-not $_.Exception.Message.StartsWith('recovery_ownership:')) { throw }
                    $rejected = $true
                }
                if (-not $rejected -or $script:dockerInvoked) {
                    throw 'Unowned backup metadata reached a privileged container operation.'
                }
            }
            Write-Output 'RECOVERY_PATH_BOUNDARY=Verified'
            """);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Path-boundary probe could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        Assert.Contains("RECOVERY_PATH_BOUNDARY=Verified", output);
        Assert.False(Directory.Exists(candidate));
    }
}

[Trait("Category", "RecoveryJsonContract")]
public sealed class RecoveryJsonTests
{
    [Fact]
    public void ReferenceNodesKeepNestedOriginalsAndOpaqueEmbeddedJsonButExcludeInlineBindings()
    {
        var citation = new { uri = "demo:original", hash = new string('a', 64) };
        var carrier = new Dictionary<string, object?>
        {
            ["pAyLoAd"] = Canonical.Serialize(new { attachmentHashes = new[] { new string('b', 64) } }),
            ["inputJson"] = null
        };
        var nodes = RecoveryJson.ReferenceNodes(Canonical.Serialize(new
        {
            nested = new object[] { new[] { citation }, new { deeper = carrier } },
            inline = new { id = "version-only", version = 3, hash = new string('c', 64) },
            signatures = new[] { new { scopeHash = new string('d', 64) } }
        }));
        Assert.Equal(2, nodes.Length);
        Assert.Contains(Canonical.Serialize(citation), nodes);
        Assert.Contains(Canonical.Serialize(carrier), nodes);
    }

    [Fact]
    public void ReferenceNodesSkipOnlyRetentionOwnedChildrenAndRetainIndependentAndOtherNestedBindings()
    {
        var owned = new { objectKey = "raw-quarantine/" + new string('a', 64), source = "retention-owned" };
        var receipt = new { xmlHash = new string('b', 64), source = "retention-receipt" };
        var other = new { attachmentHashes = new[] { new string('c', 64) } };
        var retention = new { contract = "cohors-retention-record-1", @object = owned, deletion = receipt, other };
        var independent = new { objectKey = owned.objectKey, source = "independent" };
        var nodes = RecoveryJson.ReferenceNodes(Canonical.Serialize(new object[] { retention, independent }));
        Assert.Equal(3, nodes.Length);
        Assert.Contains(Canonical.Serialize(retention), nodes);
        Assert.Contains(Canonical.Serialize(other), nodes);
        Assert.Contains(Canonical.Serialize(independent), nodes);
        Assert.DoesNotContain(Canonical.Serialize(owned), nodes);
        Assert.DoesNotContain(Canonical.Serialize(receipt), nodes);
    }

    [Fact]
    public async Task RestoreTimingSurvivesDotSourcedBackupParametersAndIsDurableBeforeRuntimeFailure()
    {
        var home = RecoveryFixture.RepositoryRoot();
        var directory = RecoveryFixture.RequireNewDirectory(home,
            Path.Combine(home, ".local", "restore-timing-contract-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            var timing = Path.Combine(directory, "phases.jsonl");
            var values = new Dictionary<string, string>
            {
                ["ConfigurationPath"] = Path.Combine(directory, "missing-config.json"),
                ["BackupDirectory"] = Path.Combine(directory, "missing-backup"),
                ["ManifestSha256"] = new string('a', 64),
                ["ApplicationDirectory"] = Path.Combine(directory, "missing-runtime"),
                ["TimingPath"] = timing
            };
            var parameters = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(values));
            var script = Path.Combine(home, "scripts", "Restore-Local.ps1").Replace("'", "''");
            var output = await RecoveryDiagnosticTests.Probe($$"""
                $parameters = ConvertFrom-Json -AsHashtable ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{parameters}}')))
                $failed = $false
                try { & '{{script}}' @parameters }
                catch {
                    if (-not $_.Exception.Message.StartsWith('recovery_runtime:', [StringComparison]::Ordinal)) { throw }
                    $failed = $true
                }
                if (-not $failed) { throw 'The deliberately absent runtime must fail before SQL or Docker.' }
                Write-Output 'RESTORE_TIMING_PRE_RUNTIME=Verified'
                """, Guid.NewGuid().ToString("N"));
            Assert.Contains("RESTORE_TIMING_PRE_RUNTIME=Verified", output);
            using var record = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(timing)));
            Assert.Equal("RecoveryRestore", record.RootElement.GetProperty("variant").GetString());
            Assert.Equal("timing-start", record.RootElement.GetProperty("phase").GetString());
            Assert.Equal("script-timing-initialization", record.RootElement.GetProperty("clockScope").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

[Trait("Category", "Recovery")]
public sealed class RecoveryRetentionContractTests : IDisposable
{
    private readonly string home = RecoveryFixture.RepositoryRoot();
    private readonly string directory;
    public RecoveryRetentionContractTests()
    {
        directory = RecoveryFixture.RequireNewDirectory(home,
            Path.Combine(home, ".local", "retention-recovery-contract-" + Guid.NewGuid().ToString("N")));
        System.IO.Directory.CreateDirectory(directory);
    }
    public void Dispose() => System.IO.Directory.Delete(directory, recursive: true);

    private async Task Prepare()
    {
        var now = DateTimeOffset.Parse("2026-11-16T15:00:00Z");
        var objects = new LocalObjectStore(Path.Combine(directory, "objects"));
        RetentionRecord Item(string id, RetentionObjectBinding binding) => new(id, 1, "ortho", binding,
            RetentionRules.PolicyVersion, "DEMO-RAW-" + id, now.AddDays(-8), now, true, false, "", "Registered",
            null, 0, null, null, null);
        const string deletedText = "SYNTHETIC DELETED RAW CONTRACT SAMPLE";
        var original = Item("raw-deleted", await objects.CreateSyntheticRaw(deletedText));
        var deleting = original with { Version = 2, Status = "Deleting", Attempts = 1, LastAttemptAt = now,
            Authorization = new(new string('b', 64), "quality-test", RetentionRules.Scope(original, null), now,
                "Explizite Freigabe synthetischer isolierter Testbytes.", true, "Keine unberuecksichtigten Kopien im isolierten Testbereich.") };
        var deleted = deleting with { Version = 3, Status = "Deleted", Deletion = await objects.DeleteRaw(deleting, now) };
        var active = Item("raw-active", await objects.CreateSyntheticRaw("SYNTHETIC ACTIVE RAW CONTRACT SAMPLE"));
        var text = "SYNTHETIC REGULATORY EVIDENCE CONTRACT SAMPLE";
        var hash = await objects.Put(text);
        var evidence = new Evidence("evidence-contract", 1, "ortho", "Synthetic source", "TechnicalFile", text, hash,
            "Synthetic", "DEMO-SOURCE", "Synthetic", "Internal", now, null, now, true, true, hash);
        var rows = new[] { active, deleted }.Select(value => new { Kind = "retention", value.Id, value.ProductId,
            value.Version, Json = Canonical.Serialize(value) }).ToArray();
        var payloads = new[] { Canonical.Serialize(original), Canonical.Serialize(deleting), Canonical.Serialize(deleted),
            Canonical.Serialize(active), Canonical.Serialize(evidence) };
        // SQL FOR JSON keeps column names; the serialized aggregate payloads remain canonical camel-case JSON.
        await File.WriteAllTextAsync(Path.Combine(directory, "fixture.json"), JsonSerializer.Serialize(new {
            rows, payloads, independent = Canonical.Serialize(deleted.Object), hash = deleted.Object.Hash, deletedText }));
    }

    [Fact]
    public async Task T20_RawClassesAndRealDeletionJournalRoundTripWithoutInventingRequiredDeletedBytes()
    {
        await Prepare();
        await Contract("""
            $cut = Get-RecoveryRetentionCut $fixture.rows
            $references = Get-RecoveryReferences $fixture.payloads $cut
            $state = @{ references = $references; retention = $cut }
            $objects = Get-RecoveryObjects $root
            Assert-RecoveryCoverage $state $objects
            if ($references.Count -ne 2 -or @($cut.rows | Where-Object { $_.status -ceq 'Deleted' }).Count -ne 1) {
                throw 'Unexpected typed raw/regulatory reference coverage.'
            }
            $copy = Join-Path $env:COHORS_RETENTION_CONTRACT 'copy'
            New-Item -ItemType Directory -Path $copy | Out-Null
            Copy-RecoveryObjects $root $copy $objects
            if ((Get-RecoveryHash (Get-RecoveryObjects $copy)) -cne (Get-RecoveryHash $objects)) {
                throw 'Typed raw/journal copy changed bytes or relative addresses.'
            }
            Assert-RecoveryCoverage $state (Get-RecoveryObjects $copy)
            Remove-RecoveryObjects $copy $objects
            if (Test-Path -LiteralPath $copy) { throw 'Known typed object cleanup is incomplete.' }
            """);
    }

    [Fact]
    public async Task T20_DeletionNeverWaivesIndependentReferencesFreshnessOrReappearedRawBytes()
    {
        await Prepare();
        await Contract("""
            function Require-Rejection([scriptblock]$Action, [string]$Code) {
                $rejected = $false
                try { & $Action }
                catch {
                    if (-not $_.Exception.Message.StartsWith($Code + ':')) { throw }
                    $rejected = $true
                }
                if (-not $rejected) { throw ('Missing rejection: ' + $Code) }
            }
            $cut = Get-RecoveryRetentionCut $fixture.rows
            $objects = Get-RecoveryObjects $root
            $state = @{ references = (Get-RecoveryReferences $fixture.payloads $cut); retention = $cut }
            $independent = @{ references = (Get-RecoveryReferences (@($fixture.payloads) + $fixture.independent) $cut); retention = $cut }
            Require-Rejection { Assert-RecoveryCoverage $independent $objects } 'recovery_retention_reference'
            $manifest = [ordered]@{
                format = 'cohors-local-recovery-2'; syntheticOnly = $true; schemaVersion = 2
                sourceDatabase = 'Cohors_Recovery_00000000000000000000000000000000'
                references = $state.references; retention = $cut; objects = $objects
            }
            Require-Rejection { Assert-RecoveryRetentionCheckpoint $manifest '' '' $false } 'recovery_retention_checkpoint'
            $path = Join-Path $env:COHORS_RETENTION_CONTRACT 'current-manifest.json'
            [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $manifest -Depth 100))
            $digest = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
            Assert-RecoveryRetentionCheckpoint $manifest $path $digest $true | Out-Null
            $old = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $manifest -Depth 100) -AsHashtable -Depth 100 -DateKind String
            ($old.retention.rows | Where-Object { $_.status -ceq 'Deleted' }).status = 'Registered'
            Require-Rejection { Assert-RecoveryRetentionCheckpoint $old $path $digest $true } 'recovery_retention_stale_cut'
            $returned = Join-Path (Join-Path $root 'raw-quarantine') $fixture.hash
            [IO.File]::WriteAllText($returned, $fixture.deletedText, [Text.UTF8Encoding]::new($false))
            Require-Rejection { Assert-RecoveryCoverage $state (Get-RecoveryObjects $root) } 'recovery_retention_resurrection'
            """);
    }

    private async Task Contract(string body)
    {
        var configuration = typeof(RecoveryFixture).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("COHORS_RECOVERY_PWSH") ?? "pwsh") {
            WorkingDirectory = home, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["COHORS_RETENTION_CONTRACT"] = directory;
        start.Environment["COHORS_RETENTION_HOME"] = home;
        start.Environment["COHORS_RETENTION_APP"] = Path.Combine(home, "src", "Cohors.Api", "bin", configuration, "net10.0");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("""
            $ErrorActionPreference = 'Stop'
            . (Join-Path $env:COHORS_RETENTION_HOME 'scripts\Backup-Local.ps1')
            Initialize-RecoveryRuntime $env:COHORS_RETENTION_APP
            $root = Join-Path $env:COHORS_RETENTION_CONTRACT 'objects'
            $fixture = Get-Content -LiteralPath (Join-Path $env:COHORS_RETENTION_CONTRACT 'fixture.json') -Raw |
                ConvertFrom-Json -AsHashtable -Depth 100 -DateKind String
            """ + "\n" + body + "\nWrite-Output 'RECOVERY_RETENTION_CONTRACT=Verified'");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Recovery contract test could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        Assert.Contains("RECOVERY_RETENTION_CONTRACT=Verified", output);
    }
}

public sealed partial class RecoveryFixture : IAsyncLifetime
{
    public const string Consumer = "cohors-domain-events-v1";
    public const string SqlImage = "mcr.microsoft.com/mssql/server@sha256:4525e3efac9f3e241cb003bb2ff2a82818b502f37e12049d788b1a9fa7e13b7f";
    private readonly string owner = Guid.NewGuid().ToString("N");
    private readonly List<OwnedContainer> containers = [];
    private readonly List<string> privateConfigs = [];
    private readonly List<string> checks = [];
    private string home = "";
    private string applicationDirectory = "";
    private string targetMaster = "";
    private string sourceDatabase = "";
    private string sourceObjects = "";
    private string sentinelDatabase = "";
    private string sentinelValue = "";
    private OwnedContainer? sourceContainer;
    private OwnedContainer? targetContainer;
    private int sourcePort;
    private int targetPort;
    private int logNumber;
    private readonly long setupStarted = Stopwatch.GetTimestamp();
    private int setupSequence;
    private bool sourceRemoved;
    public string Directory { get; private set; } = "";
    public string BackupDirectory => Path.Combine(Directory, "backup");
    public string PreDeletionBackupDirectory => Path.Combine(Directory, "pre-deletion-backup");
    public string ManifestHash { get; private set; } = "";
    public string PreDeletionManifestHash { get; private set; } = "";
    public RetentionRecord ActiveRaw { get; private set; } = null!;
    public RetentionRecord DeletedRaw { get; private set; } = null!;
    public string ActiveRawText { get; private set; } = "";
    public string DeletedRawText { get; private set; } = "";
    private readonly Dictionary<string, string> applicationAssemblies = new(StringComparer.Ordinal);
    public PolicyCatalog Policies { get; private set; } = null!;
    public SafetyCase SourceCase { get; private set; } = null!;
    public SafetyCase UnknownReceipt { get; private set; } = null!;
    public Evidence OriginalEvidence { get; private set; } = null!;
    public Evidence CurrentEvidence { get; private set; } = null!;
    public SubmissionCommand UnknownCommand { get; private set; } = null!;
    public string UnknownKey { get; private set; } = "";
    public string UnknownCorrelation { get; private set; } = "";
    public string CompletedEvent { get; private set; } = "";
    public string PendingEvent { get; private set; } = "";
    public string PoisonedProjectionId { get; } = "stale-projection-" + Guid.NewGuid().ToString("N");
    public DateTimeOffset CaptureTime { get; } = DateTimeOffset.Parse("2026-11-16T15:00:00Z");
    public Dictionary<string, byte[]> SourceBytes { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> SourceTables { get; private set; } = new(StringComparer.Ordinal);
    public Psur InitialPsur { get; private set; } = null!;
    public Psur OtherSeriesPsur { get; private set; } = null!;
    public Psur NextPsur { get; private set; } = null!;
    public PsurGroupDefinition CurrentPsurGroup { get; private set; } = null!;
    public PsurReview PsurReviewAtCutoff { get; private set; } = null!;
    public PsurReview ClosedPsurReview { get; private set; } = null!;
    public PsurReview PendingPsurReview { get; private set; } = null!;
    public Capa PsurCapaAtCutoff { get; private set; } = null!;
    public Capa VerifiedPsurCapa { get; private set; } = null!;
    public VersionCommand PsurReplyCommand { get; private set; } = null!;
    public string PsurReplyKey { get; private set; } = "";
    public SortedDictionary<string, string> SourcePsurHistory { get; private set; } = new(StringComparer.Ordinal);
    public bool PsurRecoveryVerified { get; set; }
    internal Xunit.Abstractions.ITestOutputHelper? DiagnosticOutput { get; set; }
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string RepositoryRoot()
    {
        var parent = new DirectoryInfo(AppContext.BaseDirectory);
        while (parent is not null && !File.Exists(Path.Combine(parent.FullName, "Cohors.slnx"))) parent = parent.Parent;
        return parent?.FullName ?? throw new InvalidOperationException("Run recovery from the Cohors worktree.");
    }

    internal static string RequireNewDirectory(string home, string candidate)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (!directory.StartsWith(Path.Combine(home, ".local") + Path.DirectorySeparatorChar, PathComparison)
            || System.IO.Directory.Exists(directory))
            throw new InvalidOperationException("Recovery requires a new private directory below this worktree .local.");
        return directory;
    }

    public async Task InitializeAsync()
    {
        home = RepositoryRoot();
        var configuration = typeof(RecoveryFixture).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        applicationDirectory = Path.Combine(home, "src", "Cohors.Api", "bin", configuration, "net10.0");
        foreach (var name in new[] { "Cohors.Domain.dll", "Cohors.Application.dll", "Cohors.Infrastructure.dll", "Cohors.Agents.dll", "Cohors.Api.dll" })
            applicationAssemblies.Add(name, FileHash(Path.Combine(applicationDirectory, name)));
        foreach (var type in new[] { typeof(RetentionRecord), typeof(RetentionService), typeof(SqlStateStore), typeof(AgentRunner) })
            Assert.Equal(applicationAssemblies[Path.GetFileName(type.Assembly.Location)], FileHash(type.Assembly.Location));
        Directory = RequireNewDirectory(home, Environment.GetEnvironmentVariable("COHORS_RECOVERY_DIRECTORY")
            ?? Path.Combine(home, ".local", "recovery-" + owner));
        System.IO.Directory.CreateDirectory(Directory);
        System.IO.Directory.CreateDirectory(Path.Combine(Directory, "private"));
        sourcePort = Port("COHORS_RECOVERY_SOURCE_PORT");
        do { targetPort = Port("COHORS_RECOVERY_TARGET_PORT"); } while (targetPort == sourcePort &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COHORS_RECOVERY_TARGET_PORT")));
        if (targetPort == sourcePort) throw new InvalidOperationException("Recovery SQL ports must be distinct.");
        var previousConnection = Environment.GetEnvironmentVariable("COHORS_TEST_CONNECTION");
        try
        {
            await RecordSetupPhase("source-sql-start");
            sourceContainer = await StartSql("source", sourcePort);
            await RecordSetupPhase("source-sql-ready");
            var sourceConnection = Connection(sourcePort, sourceContainer.Password, "master");
            Environment.SetEnvironmentVariable("COHORS_TEST_CONNECTION", sourceConnection);
            var source = new SqlFixture { SeedCases = false };
            try
            {
                await RecordSetupPhase("source-fixture-initialize-start");
                await source.InitializeAsync();
                await RecordSetupPhase("source-fixture-ready");
                Policies = source.Policies;
                sourceDatabase = new SqlConnectionStringBuilder(source.ConnectionString).InitialCatalog;
                sourceObjects = Path.Combine(home, ".local", sourceDatabase, "blobs");
                var s = new CaseScenario(source);
                await s.Create(); await s.Assess(); await s.Prepare();
                s.Case = await s.Regulatory.AttachMir(s.Case.Id, new(s.Case.Version,
                    s.MirFacts() with { NextReportDate = new(2026, 11, 22) },
                    "Festen synthetischen Sonntagstermin fuer die unabhaengige Wiederherstellungsreferenz zusagen."),
                    SqlFixture.Medical, CaseScenario.Key());
                await s.Approve();
                s.Case = await s.Service.Submit(s.Case.Id, new(s.Case.Version, "Accept",
                    "Nur simulierten Initialempfang bestaetigen; der zugesagte Folgebericht bleibt offen."),
                    SqlFixture.Ra, CaseScenario.Key());
                var ni = DeadlineCalculator.Calculate("NI", s.Case.Seriousness, s.Case.FirstAwarenessAt, source.Policies.Calendar("NI"));
                s.Case = await source.Store.Execute<SafetyCase>(new(s.Case.Id, "case", s.Case.ProductId, s.Case.Version,
                    CaseScenario.Key(), "RecoveryJurisdictionFixture",
                    "Unabhaengige offene NI-Pflicht im expliziten synthetischen Wiederherstellungs-Testfall.",
                    SqlFixture.Ra, ni, CaptureTime), current => current! with
                    { Version = current.Version + 1, Deadlines = [.. current.Deadlines, ni] });
                await s.Prepare("FollowUp"); await s.Approve();
                UnknownCommand = new(s.Case.Version, "Unknown",
                    "Ungeklaerten Empfang des eingefrorenen Folgepakets simulieren; keine Annahme bestaetigen.");
                UnknownKey = CaseScenario.Key();
                s.Case = await s.Service.Submit(s.Case.Id, UnknownCommand, SqlFixture.Ra, UnknownKey);
                UnknownReceipt = s.Case;
                UnknownCorrelation = s.Case.Submissions.Single(x => x.Status == "TransmissionUnknown").CorrelationId;
                await RecordSetupPhase("source-native-case-ready");
                OriginalEvidence = (await source.Store.Get<Evidence>("evidence", "ortho-spec-v3"))!;
                var quality = new QualityService(source.Store, source.Objects, s.Clock);
                var change = await quality.OpenChange(new(s.Case.Id, "ortho", OriginalEvidence.Id, OriginalEvidence.Version,
                    "Synthetischen Spezifikationsnachtrag ohne Aenderung historischer Belegbindungen veroeffentlichen.",
                    "Aktuelle und eingefroren gebundene Revision muessen im Wiederherstellungsfall nebeneinander existieren."),
                    SqlFixture.Pms, CaseScenario.Key());
                change = await quality.ApproveChange(change.Id, new(change.Version,
                    "Unabhaengiger QM-Review der konkreten synthetischen Spezifikationsfolgeversion.", null),
                    SqlFixture.Quality, CaseScenario.Key());
                CurrentEvidence = await new EvidenceService(source.Store, source.Objects, s.Clock).Revise(OriginalEvidence.Id,
                    new(OriginalEvidence.Version, change.Id, OriginalEvidence.Title + " / Recovery revision",
                        OriginalEvidence.Content + "\nSYNTHETISCH: Neuer Montagepruefhinweis; historische Evidenz bleibt unveraendert.",
                        CaptureTime.AddDays(1), "Genehmigte Folgeversion als aktuell fuehren; das Unknown-Paket nicht neu binden."),
                    SqlFixture.Quality, CaseScenario.Key());
                CompletedEvent = await Event(source.ConnectionString, s.Case.Id, "SimulatedSubmission", s.Case.Version);
                PendingEvent = await Event(source.ConnectionString, CurrentEvidence.Id, "EvidenceVersionPublished", CurrentEvidence.Version);
                var processor = new OutboxProcessor(source.Store, s.Service, new KnowledgeSearch(source.Store, source.Objects, source.Store));
                Assert.Equal("Handled", (await processor.Process(CompletedEvent)).State);
                Assert.True(await source.Store.HasReceipt(CompletedEvent, Consumer));
                Assert.False(await source.Store.HasReceipt(PendingEvent, Consumer));
                await Execute(source.ConnectionString, $"INSERT dbo.SearchProjection VALUES(N'{PoisonedProjectionId}',N'ortho',999,N'untrusted stale projection',SYSUTCDATETIME(),'{new string('0', 64)}')");
                SourceCase = await s.Service.Get(s.Case.Id, SqlFixture.Ra);
                Assert.Single(SourceCase.FollowUps!);
                Assert.Equal("Open", SourceCase.FollowUps![0].Status);
                Assert.Contains(SourceCase.Deadlines, d => d.Jurisdiction == "NI" && d.Status == "Open");
                await source.Store.Manifest(SourceCase.Id);
                await source.Store.Manifest(CurrentEvidence.Id, kind: "evidence");
                await RecordSetupPhase("source-evidence-outbox-ready");
                await RecordSetupPhase("source-psur-start");
                await PreparePsurRecovery(source);
                await RecordSetupPhase("source-psur-complete");
                await RecordSetupPhase("source-eudamed-start");
                Eudamed = await EudamedRecoveryData.Prepare(source);
                await RecordSetupPhase("source-eudamed-complete");
                await RecordSetupPhase("source-psur-history-start");
                SourcePsurHistory = await PsurHistory(source.Store);
                await RecordSetupPhase("source-psur-history-complete");
                await RecordSetupPhase("source-import-start");
                Imports = await ImportRecoveryData.Prepare(source, CaptureTime);
                await RecordSetupPhase("source-import-complete");
                const string retentionReason = "Menschlich kontrollierter isolierter T18/T20-Rohdaten-Test ohne Patienten oder produktive Loeschung.";
                var retention = new RetentionService(source.Store, source.Store, source.Objects, source.Policies,
                    new(true), new TestClock(CaptureTime));
                var raw = (IRawQuarantineStore)source.Objects;
                DeletedRaw = await retention.CreateRaw(new("ortho", "DEMO-RAW-RECOVERY-DELETED", CaptureTime.AddDays(-8),
                    retentionReason), SqlFixture.Quality, CaseScenario.Key());
                ActiveRaw = await retention.CreateRaw(new("ortho", "DEMO-RAW-RECOVERY-ACTIVE", CaptureTime.AddDays(-1),
                    retentionReason), SqlFixture.Quality, CaseScenario.Key());
                DeletedRawText = await raw.ReadRaw(DeletedRaw.Object);
                ActiveRawText = await raw.ReadRaw(ActiveRaw.Object);
                var config = await WriteConfig(source.ConnectionString, sourceObjects, sourceContainer, "source");
                await RecordSetupPhase("source-retention-ready");
                SqlConnection.ClearAllPools();
                await Execute(sourceConnection, $"ALTER DATABASE [{sourceDatabase}] SET READ_ONLY WITH NO_WAIT");
                await RecordSetupPhase("pre-deletion-backup-start");
                var beforeDeletion = await Script("Backup-Local.ps1", config, PreDeletionBackupDirectory, "-Quiesced");
                RequireSuccess(beforeDeletion, "pre-deletion native backup");
                await RecordSetupPhase("pre-deletion-backup-complete");
                PreDeletionManifestHash = FileHash(Path.Combine(PreDeletionBackupDirectory, "manifest.json"));
                await Execute(sourceConnection, $"ALTER DATABASE [{sourceDatabase}] SET READ_WRITE WITH NO_WAIT");
                DeletedRaw = await retention.Authorize(DeletedRaw.Id, new(DeletedRaw.Version, true,
                    "Die einzige alte Backupkopie bleibt explizit isoliertes Negativtestmaterial; ihr Restore wird durch den aktuellen Loeschkatalog gesperrt. Keine Behauptung ihrer physischen Vernichtung.",
                    retentionReason), SqlFixture.Quality, CaseScenario.Key());
                DeletedRaw = await retention.Delete(DeletedRaw.Id, new(DeletedRaw.Version, retentionReason),
                    SqlFixture.Quality, CaseScenario.Key());
                Assert.Equal("Deleted", DeletedRaw.Status);
                await RecordSetupPhase("retention-deletion-complete");
                foreach (var file in System.IO.Directory.EnumerateFiles(sourceObjects, "*", SearchOption.AllDirectories))
                    SourceBytes.Add(Path.GetRelativePath(sourceObjects, file), await File.ReadAllBytesAsync(file));
                await RecordSetupPhase("source-objects-read");
                await RecordSetupPhase("source-table-read-start");
                SourceTables = await Tables(source.ConnectionString);
                await RecordSetupPhase("source-table-read-complete");
                SqlConnection.ClearAllPools();
                await Execute(sourceConnection, $"ALTER DATABASE [{sourceDatabase}] SET READ_ONLY WITH NO_WAIT");
                await RecordSetupPhase("post-deletion-backup-start");
                var backup = await Script("Backup-Local.ps1", config, BackupDirectory, "-Quiesced");
                RequireSuccess(backup, "native backup");
                await RecordSetupPhase("post-deletion-backup-complete");
                ManifestHash = FileHash(Path.Combine(BackupDirectory, "manifest.json"));
                Assert.Contains("RECOVERY_MANIFEST_SHA256=" + ManifestHash, backup.Output);
            }
            finally { await source.DisposeAsync(); }
            Assert.Equal(0, (int)(await Scalar(sourceConnection, $"SELECT COUNT(*) FROM sys.databases WHERE name=N'{sourceDatabase}'"))!);
            Assert.False(System.IO.Directory.Exists(sourceObjects));
            sourceRemoved = true;
            await RequireDocker("stop-source", ["stop", sourceContainer.Id]);
            targetContainer = await StartSql("target", targetPort);
            targetMaster = Connection(targetPort, targetContainer.Password, "master");
            sentinelDatabase = "Cohors_Recovery_" + Guid.NewGuid().ToString("N") + "_sentinel";
            sentinelValue = Guid.NewGuid().ToString("N");
            await ExecuteMaster($"CREATE DATABASE [{sentinelDatabase}]");
            await Execute(WithDatabase(targetMaster, sentinelDatabase),
                $"CREATE TABLE dbo.Preserved(Value nvarchar(100) NOT NULL); INSERT dbo.Preserved VALUES(N'{sentinelValue}')");
            await RequireSourceUnavailable();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
        finally { Environment.SetEnvironmentVariable("COHORS_TEST_CONNECTION", previousConnection); }
    }

    private async Task PreparePsurRecovery(SqlFixture source)
    {
        const string reason = "SYNTHETISCH: Die getrennten Produktquellen, unveraenderten Berichtsfassungen und vereinbarten BS-Antworten werden ausschliesslich fuer den isolierten Recovery-Test nachvollziehbar geprueft; keine klinische Risikoakzeptanz.";
        const string response = "SYNTHETISCHE ANTWORT: Die beiden Basic-UDI-Familien und ihre belegten Nenner bleiben getrennt. Diese begruendete Praezisierung bindet die konkret ausgewaehlten historischen Dokumentfassungen, ohne eingefrorene Berichtsbytes oder fruehere Pflichten zu aendern.";
        var clock = new TestClock(CaptureTime);
        var analytics = new AnalyticsService(source.Store, new AgentRunner(new(null, null, null, null)), source.Policies, clock);
        var reviews = new PsurReviewService(source.Store, analytics, clock);
        var quality = new QualityService(source.Store, source.Objects, clock);
        RecordPsurGroupCommand Definition(string lead) => new(0,
            "DEMO-PSUR-GROUP-RECOVERY-" + lead.ToUpperInvariant() + "-" + owner.ToUpperInvariant(), lead,
            ["ortho", "cardio"], new(2025, 11, 15), reason,
            "Synthetischer Leitprodukt-Jahresplan mit expliziter Einreichungsvereinbarung; andere Serienpflichten bleiben getrennt bestehen.");
        FreezePsurCommand Initial(PsurGroupDefinition group) => new(group.ProductId, group.ProductId,
            group.FirstPeriodStart, group.FirstPeriodStart.AddYears(1).AddDays(-1), "Worldwide",
            Mode: AgentMode.Replay, GroupId: group.Id, GroupVersion: group.Version);
        async Task<Psur> FreezeAndApproveReport(FreezePsurCommand input, string phase)
        {
            await RecordSetupPhase(phase + "-freeze-start");
            var report = await analytics.Freeze(input, SqlFixture.Pms, CaseScenario.Key());
            await RecordSetupPhase(phase + "-frozen");
            report = await analytics.Approve(report.Id, new(report.Version, reason), SqlFixture.Medical, CaseScenario.Key());
            await RecordSetupPhase(phase + "-medical-approved");
            report = await analytics.Approve(report.Id, new(report.Version, reason), SqlFixture.Ra, CaseScenario.Key());
            await RecordSetupPhase(phase + "-ra-approved");
            report = await analytics.Simulate(report.Id, new(report.Version, reason), SqlFixture.Ra, CaseScenario.Key());
            await RecordSetupPhase(phase + "-submitted");
            return report;
        }
        OpenPsurReviewCommand Question(string suffix, bool action) => new(InitialPsur.Id, InitialPsur.Version,
            "DEMO-BS-RECOVERY-" + suffix + "-" + owner,
            "Die synthetische BS verlangt eine quellgebundene Praezisierung der getrennten Produktdaten und der angegebenen Massnahmen.",
            clock.Now.AddDays(4), action, "Ausdruecklich fuer diese synthetische Rueckfrage vereinbarter UTC-Termin, keine allgemeine Rechtsfrist.",
            "de", reason);
        DraftPsurReplyCommand Draft(PsurReview review, params PsurReviewSelection[] actions) => new(review.Version,
            InitialPsur.Id, InitialPsur.Version, response,
            PsurGroupRules.Snapshots(InitialPsur.Snapshot).Select(s => s.Directory!.Entries.First(e =>
                e.Kind == "evidence" && e.Selection == "CurrentAtFreeze"))
                .Select(e => new PsurReviewSelection(e.Kind, e.Source.Id, e.Source.Version)).ToArray(), actions,
            "Keine neue Massnahme fuer diese Klarstellung erfinden; bereits verknuepfte Massnahmen frueherer Runden bleiben gesondert nachweispflichtig.", reason);
        async Task<PsurReview> ApproveReply(PsurReview review, string phase)
        {
            review = await reviews.Approve(review.Id, new(review.Version, reason), SqlFixture.Medical, CaseScenario.Key());
            await RecordSetupPhase(phase + "-medical-approved");
            review = await reviews.Approve(review.Id, new(review.Version, reason), SqlFixture.Ra, CaseScenario.Key());
            await RecordSetupPhase(phase + "-ra-approved");
            return review;
        }

        var definition = Definition("ortho");
        CurrentPsurGroup = await analytics.RecordGroup(definition, SqlFixture.Pms, CaseScenario.Key());
        InitialPsur = await FreezeAndApproveReport(Initial(CurrentPsurGroup), "source-psur-initial");
        await RecordSetupPhase("source-psur-initial-ready");
        clock.Now = DateTimeOffset.Parse("2027-11-13T15:00:00Z");
        var cardioCase = await source.Service(clock).Register(new CaseScenario(source).Input with {
            ProductId = "cardio", OccurredAt = clock.Now.AddDays(-2), AwarenessAt = clock.Now.AddDays(-1)
        }, SqlFixture.Pms, CaseScenario.Key());
        PsurCapaAtCutoff = await quality.OpenCapa(new(cardioCase.Id, "cardio",
            "Synthetische BS-Dokumentationspruefung fuer Recovery",
            "Drei synthetische Soll/Ist-Dokumentationsproben einzeln gegen die vorgegebenen Kriterien pruefen.", reason,
            SqlFixture.Pms.Id, "Alle drei Vergleiche erfuellen das vorher dokumentierte Kriterium ohne die beanstandete Abweichung.",
            clock.Now.AddDays(10)), SqlFixture.Pms, CaseScenario.Key());
        var review = await reviews.Open(Question("CUTOFF", true), SqlFixture.Ra, CaseScenario.Key());
        await RecordSetupPhase("source-psur-first-question-opened");
        review = await reviews.Draft(review.Id, Draft(review, new PsurReviewSelection("capa", PsurCapaAtCutoff.Id, PsurCapaAtCutoff.Version)),
            SqlFixture.Pms, CaseScenario.Key());
        await RecordSetupPhase("source-psur-first-reply-drafted");
        review = await ApproveReply(review, "source-psur-first-reply");
        review = await reviews.Simulate(review.Id, new(review.Version, reason), SqlFixture.Ra, CaseScenario.Key());
        await RecordSetupPhase("source-psur-first-reply-submitted");
        review = await reviews.Assess(review.Id, new(review.Version, "FurtherResponseRequired", reason,
            "Die synthetische BS verlangt eine weitere Dokumentation der begruendeten Quellenausschluesse; die fruehere CAPA bleibt bestehen.",
            clock.Now.AddDays(6), NextScheduleBasis: "Gesonderter vereinbarter UTC-Termin ausschliesslich fuer die zweite synthetische Rueckfragerunde."),
            SqlFixture.Ra, CaseScenario.Key());
        await RecordSetupPhase("source-psur-second-question-opened");
        review = await reviews.Draft(review.Id, Draft(review), SqlFixture.Pms, CaseScenario.Key());
        await RecordSetupPhase("source-psur-second-reply-drafted");
        review = await ApproveReply(review, "source-psur-second-reply");
        PsurReplyCommand = new(review.Version, reason);
        PsurReplyKey = CaseScenario.Key();
        PsurReviewAtCutoff = await reviews.Simulate(review.Id, PsurReplyCommand, SqlFixture.Ra, PsurReplyKey);
        await RecordSetupPhase("source-psur-cutoff-reply-ready");

        var otherGroup = await analytics.RecordGroup(Definition("cardio"), SqlFixture.Pms, CaseScenario.Key());
        OtherSeriesPsur = await FreezeAndApproveReport(Initial(otherGroup), "source-psur-other-series");
        Assert.Equal(Canonical.Serialize(PsurReviewAtCutoff),
            Canonical.Serialize(Assert.Single(OtherSeriesPsur.Snapshot.Group!.RegulatoryHistory!.Reviews).CurrentAtFreeze));
        await RecordSetupPhase("source-psur-other-series-ready");

        clock.Now = DateTimeOffset.Parse("2027-11-16T15:00:00Z");
        VerifiedPsurCapa = await quality.Verify(PsurCapaAtCutoff.Id, new(PsurCapaAtCutoff.Version,
            "SYNTHETISCHER PRUEFBELEG: Drei getrennt dokumentierte Soll/Ist-Vergleiche erfuellen das vorab festgelegte Kriterium.",
            true, reason), SqlFixture.Quality, CaseScenario.Key());
        ClosedPsurReview = await reviews.Assess(PsurReviewAtCutoff.Id, new(PsurReviewAtCutoff.Version, "NoFurtherQuestion", reason),
            SqlFixture.Ra, CaseScenario.Key());
        PendingPsurReview = await reviews.Open(Question("POST-CUTOFF", false), SqlFixture.Ra, CaseScenario.Key());
        await RecordSetupPhase("source-psur-post-cutoff-question-opened");
        PendingPsurReview = await reviews.Draft(PendingPsurReview.Id, Draft(PendingPsurReview), SqlFixture.Pms, CaseScenario.Key());
        await RecordSetupPhase("source-psur-post-cutoff-reply-drafted");
        PendingPsurReview = await reviews.Approve(PendingPsurReview.Id, new(PendingPsurReview.Version, reason),
            SqlFixture.Medical, CaseScenario.Key());
        await RecordSetupPhase("source-psur-post-cutoff-reply-medical-approved");
        CurrentPsurGroup = await analytics.RecordGroup(definition with { ExpectedVersion = CurrentPsurGroup.Version,
            Rationale = reason + " Neue kontrollierte Gruppierungsbegruendung bei identischen getrennten Mitgliedern; die alte Version bleibt erhalten." },
            SqlFixture.Pms, CaseScenario.Key());
        var start = InitialPsur.Snapshot.End.AddDays(1);
        var end = InitialPsur.Snapshot.End.AddYears(1);
        foreach (var product in new[] { "ortho", "cardio" })
        {
            var exposure = new Exposure("recovery-next-exposure-" + owner + "-" + product, 1, product, "Worldwide", start, end,
                "Implants", product == "ortho" ? 1200 : 2300, "Explicit synthetic annual population for " + product,
                "DEMO-RECOVERY-NEXT-PERIOD-EXPOSURE", true);
            await source.Store.Execute<Exposure>(new(exposure.Id, "exposure", product, 0, CaseScenario.Key(),
                "SyntheticExposureRecorded", reason, SqlFixture.Quality, exposure, clock.Now), _ => exposure);
        }
        NextPsur = await FreezeAndApproveReport(new("ortho", "ortho", start, end, "Worldwide",
            Mode: AgentMode.Replay, PreviousReportId: InitialPsur.Id, PreviousReportVersion: InitialPsur.Version,
            SubmissionDate: new(2028, 1, 10), ScheduleAgreement: "DEMO-RECOVERY-EXPLICIT-NEXT-PERIOD-AGREEMENT",
            RevisionReason: reason, GroupId: CurrentPsurGroup.Id, GroupVersion: CurrentPsurGroup.Version),
            "source-psur-next-period");
        Assert.Equal("ReplySubmitted", NextPsur.Snapshot.Group!.RegulatoryHistory!.Reviews
            .Single(r => r.CurrentSource.Id == ClosedPsurReview.Id).StatusAtCutoff);
        Assert.Equal("MedicalApproved", PendingPsurReview.Status);
        await RecordSetupPhase("source-psur-next-period-ready");
        SourcePsurHistory = await PsurHistory(source.Store);
        await RecordSetupPhase("source-psur-initial-history-ready");
        Assert.Equal(Canonical.Serialize(SourceCase), Canonical.Serialize(await source.Store.Get<SafetyCase>("case", SourceCase.Id)));
    }

    public static async Task<SortedDictionary<string, string>> PsurHistory(IStateStore store) => new(StringComparer.Ordinal)
    {
        ["psur"] = Canonical.Serialize(await store.History<Psur>("psur", ["ortho", "cardio"])),
        ["psurGroup"] = Canonical.Serialize(await store.History<PsurGroupDefinition>("psurGroup", ["ortho", "cardio"])),
        ["psurReview"] = Canonical.Serialize(await store.History<PsurReview>("psurReview", ["ortho", "cardio"])),
        ["capa"] = Canonical.Serialize(await store.History<Capa>("capa", ["ortho", "cardio"]))
    };

    public async Task<RecoveryTarget> NewTarget()
    {
        var id = Guid.NewGuid().ToString("N");
        var targetHome = Path.Combine(Directory, "target-" + id);
        var database = "Cohors_Recovery_" + id;
        var objects = Path.Combine(targetHome, ".local", "blobs");
        var connection = WithDatabase(targetMaster, database);
        var config = await WriteConfig(connection, objects, targetContainer!, id);
        return new(this, targetHome, database, objects, connection, config);
    }

    public async Task Restore(RecoveryTarget target, [CallerMemberName] string test = "")
    {
        var result = await RestoreResult(target, BackupDirectory, ManifestHash, test);
        RequireSuccess(result, "isolated restore");
        target.OwnsDatabase = true;
        Assert.True(File.Exists(target.ReadyPath));
    }

    public Task<RecoveryProcess> RestoreResult(RecoveryTarget target, string backup, string digest,
        [CallerMemberName] string test = "") =>
        ScriptCore("Restore-Local.ps1", target.ConfigPath, backup, test, ["-ManifestSha256", digest,
            "-CurrentRetentionManifestPath", Path.Combine(BackupDirectory, "manifest.json"),
            "-CurrentRetentionManifestSha256", ManifestHash, "-CurrentRetentionConfirmed"]);

    public Task<RecoveryProcess> Script(string name, string configuration, string backup, params string[] extra) =>
        ScriptCore(name, configuration, backup, null, extra);

    private Task<RecoveryProcess> ScriptCore(string name, string configuration, string backup, string? test, string[] extra)
    {
        var invocation = RecoveryDiagnostics.Invocation(name, test);
        var parameters = new Dictionary<string, object> { ["ConfigurationPath"] = configuration,
            ["BackupDirectory"] = backup, ["ApplicationDirectory"] = applicationDirectory };
        var timingKind = name == "Backup-Local.ps1" ? "backup" : "restore";
        parameters.Add("TimingPath", Path.Combine(Directory, "private", invocation.Id + "-" + timingKind + "-phases.jsonl"));
        for (var index = 0; index < extra.Length; index++)
        {
            var parameter = extra[index];
            if (parameter is "-Quiesced" or "-CurrentRetentionConfirmed") parameters.Add(parameter[1..], true);
            else if (parameter is "-ManifestSha256" or "-CurrentRetentionManifestPath" or "-CurrentRetentionManifestSha256")
                parameters.Add(parameter[1..], extra[++index]);
            else throw new InvalidOperationException("Unsupported recovery test parameter.");
        }
        return Run("recovery-script", Environment.GetEnvironmentVariable("COHORS_RECOVERY_PWSH") ?? "pwsh",
            ["-NoProfile", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(RecoveryDiagnostics.Bootstrap))],
            new() {
                ["COHORS_RECOVERY_ENTRY"] = Path.Combine(home, "scripts", name),
                ["COHORS_RECOVERY_PARAMETERS"] = JsonSerializer.Serialize(parameters),
                ["COHORS_RECOVERY_INVOCATION"] = invocation.Id
            }, invocation);
    }

    public string CopyBackup(string label)
    {
        var destination = Path.Combine(Directory, label + "-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path.Combine(destination, "objects"));
        foreach (var file in System.IO.Directory.EnumerateFiles(BackupDirectory))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        var sourceObjects = Path.Combine(BackupDirectory, "objects");
        foreach (var file in System.IO.Directory.EnumerateFiles(sourceObjects, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, "objects", Path.GetRelativePath(sourceObjects, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
        return destination;
    }

    public async Task RequireNoTarget(RecoveryTarget target)
    {
        Assert.False(File.Exists(target.ReadyPath));
        Assert.False(System.IO.Directory.Exists(target.ObjectRoot));
        Assert.False(await DatabaseExists(target.Database));
        Assert.Equal(0, (int)(await Scalar(targetMaster, "SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'Cohors[_]Recovery[_]%[_]staging'"))!);
    }

    public async Task RequireSentinel() =>
        Assert.Equal(sentinelValue, await Scalar(WithDatabase(targetMaster, sentinelDatabase), "SELECT Value FROM dbo.Preserved"));

    public async Task RequireSourceUnavailable()
    {
        Assert.True(sourceRemoved);
        Assert.False(System.IO.Directory.Exists(sourceObjects));
        var state = await RequireDocker("source-stopped", ["inspect", "--format", "{{.State.Running}}", sourceContainer!.Id]);
        Assert.Equal("false", state.Output.Trim());
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, sourcePort).WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException("Source loopback port is unexpectedly reachable.");
        }
        catch (SocketException) { /* Refused connection is the required isolation evidence. */ }
        catch (TimeoutException) { /* A stopped source cannot accept a connection. */ }
        Assert.NotEqual(sourcePort, targetPort);
        Assert.NotEqual(sourceContainer.Id, targetContainer!.Id);
    }

    public Task ExecuteMaster(string command) => Execute(targetMaster, command);
    public async Task<bool> DatabaseExists(string database) =>
        (int)(await Scalar(targetMaster, $"SELECT COUNT(*) FROM sys.databases WHERE name=N'{database}'"))! != 0;
    public void Passed(string check) => checks.Add(check);

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(Directory)) return;
        try
        {
            foreach (var container in containers.AsEnumerable().Reverse())
                await RemoveOwnedContainer(container);
            foreach (var path in privateConfigs) File.Delete(path);
            await WriteOwnershipInventory(cleanupVerified: true);
            if (checks.Count == 11 && PsurRecoveryVerified && ImportRecoveryVerified)
            {
                foreach (var item in applicationAssemblies)
                    Assert.Equal(item.Value, FileHash(Path.Combine(applicationDirectory, item.Key)));
                var evidence = new
                {
                    scope = "Local native SQL/object recovery; direct existing application services, not HTTP/browser or Azure",
                    sourceDatabaseRemoved = sourceRemoved, sourceObjectsRemoved = !System.IO.Directory.Exists(sourceObjects),
                    sourceContainerStoppedBeforeTarget = true, separateSqlServers = sourcePort != targetPort,
                    manifestSha256 = ManifestHash, objectCount = SourceBytes.Count,
                    applicationAssemblies,
                    retention = new { activeRawHash = ActiveRaw.Object.Hash, deletedRawHash = DeletedRaw.Object.Hash,
                        deletedRawAbsent = true, exactDeletionJournalRestored = true, preDeletionManifestSha256 = PreDeletionManifestHash,
                        staleBackupRejected = true, independentReferencesNotWaived = true, automaticFreshnessProof = false },
                    psur = new {
                        sourceAndTargetContentVerified = PsurRecoveryVerified,
                        reports = new[] { InitialPsur, OtherSeriesPsur, NextPsur }.Select(p => new {
                            p.Id, p.Version, hash = Canonical.Hash(p), p.SnapshotHash, p.ScopeHash,
                            p.Snapshot.Series!.Reference, p.Snapshot.Series!.ReportVersion,
                            group = p.Snapshot.Group!.DefinitionSource,
                            products = PsurGroupRules.Products(p.Snapshot)
                        }).ToArray(),
                        historyHashes = SourcePsurHistory.ToDictionary(h => h.Key, h => Canonical.HashText(h.Value)),
                        cutoffReview = new { PsurReviewAtCutoff.Id, PsurReviewAtCutoff.Version,
                            hash = Canonical.Hash(PsurReviewAtCutoff), PsurReviewAtCutoff.Status },
                        currentReview = new { ClosedPsurReview.Id, ClosedPsurReview.Version,
                            hash = Canonical.Hash(ClosedPsurReview), ClosedPsurReview.Status },
                        pendingReview = new { PendingPsurReview.Id, PendingPsurReview.Version,
                            hash = Canonical.Hash(PendingPsurReview), PendingPsurReview.Status,
                            approvals = PendingPsurReview.Approvals.Length, submissions = PendingPsurReview.Submissions.Length }
                    },
                    imports = Imports.Evidence(ImportRecoveryVerified),
                    historicalEvidenceHash = OriginalEvidence.Hash, currentEvidenceHash = CurrentEvidence.Hash,
                    frozenPackageSha256 = Canonical.HashText(SourceCase.CurrentReport!.FrozenJson!),
                    transmissionStatus = SourceCase.CurrentReport.State.ToString(), correlationId = UnknownCorrelation,
                    promisedDueAt = SourceCase.FollowUps![0].Schedule.DueAt,
                    promisedSafetyDue = SourceCase.FollowUps[0].Schedule.SafetyDue,
                    searchAdapter = "KnowledgeSearch without SearchClient; SQL-derived local projection; no lexical ranking",
                    sqlImage = SqlImage, completedChecks = checks, ownedContainersAndVolumesRemoved = true,
                    externalLedgerWitness = false, azureRecovery = false
                };
                await File.WriteAllTextAsync(Path.Combine(Directory, "verified.json"), JsonSerializer.Serialize(evidence, Canonical.Json));
            }
        }
        finally { SqlConnection.ClearAllPools(); }
    }

    internal async Task Drop(RecoveryTarget target)
    {
        if (target.OwnsDatabase)
            await ExecuteMaster($"ALTER DATABASE [{target.Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{target.Database}]");
        if (System.IO.Directory.Exists(target.Home))
        {
            if (!target.Home.StartsWith(Directory + Path.DirectorySeparatorChar, PathComparison) ||
                new DirectoryInfo(target.Home).LinkTarget is not null)
                throw new InvalidOperationException("Target cleanup path is not the generated test directory.");
            System.IO.Directory.Delete(target.Home, recursive: true);
        }
        File.Delete(target.ConfigPath);
    }

    private async Task<string> WriteConfig(string connection, string objects, OwnedContainer container, string label)
    {
        var path = Path.Combine(Directory, "private", label + ".json");
        if (File.Exists(path)) throw new InvalidOperationException("Recovery configuration is never overwritten.");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            syntheticOnly = true, ownerId = owner, containerId = container.Id, connectionString = connection, objectRoot = objects
        }));
        privateConfigs.Add(path);
        return path;
    }

    internal async Task CheckFailedSqlStart()
    {
        var before = containers.Count;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartSql("failed-start", FreePort(), failStart: true));
        Assert.Contains("start owned SQL", failure.Message);
        var failed = Assert.Single(containers.Skip(before));
        Assert.True(failed.StartAttempted);
        Assert.Matches("^[a-f0-9]{64}$", failed.CreatedId);
        Assert.Empty(failed.Id);
        Assert.False(failed.VolumeCreated);
        var remaining = await RequireDocker("failed-start-absence", ["ps", "-a", "--no-trunc", "--filter",
            "label=cohors.recovery.instance=" + failed.OperationId, "--format", "{{.ID}}"]);
        Assert.Empty(remaining.Output.Trim());
        remaining = await RequireDocker("failed-start-volume-absence", ["volume", "ls", "--filter",
            "name=^" + failed.Volume + "$", "--format", "{{.Name}}"]);
        Assert.Empty(remaining.Output.Trim());
    }

    private async Task<OwnedContainer> StartSql(string role, int port, bool failStart = false)
    {
        RequireFreePort(port);
        var container = new OwnedContainer("cohors-recovery-" + owner + "-" + role, "Recovery!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        var exists = await RequireDocker("check-container", ["ps", "-a", "--filter", "name=^/" + container.Name + "$", "--format", "{{.ID}}"]);
        if (exists.Output.Trim().Length != 0) throw new InvalidOperationException("Generated recovery container already exists.");
        exists = await RequireDocker("check-volume", ["volume", "ls", "--filter", "name=^" + container.Volume + "$", "--format", "{{.Name}}"]);
        if (exists.Output.Trim().Length != 0) throw new InvalidOperationException("Generated recovery volume already exists.");
        containers.Add(container);
        try
        {
            container.VolumeCreationAttempted = true;
            await WriteOwnershipInventory();
            await RequireDocker("create-volume", ["volume", "create", "--label", "cohors.recovery.owner=" + owner,
                "--label", "cohors.recovery.instance=" + container.OperationId, container.Volume]);
            await CheckOwnedVolume(container);
            container.VolumeCreated = true;
            container.CreateAttempted = true;
            await WriteOwnershipInventory();
            string[] entrypoint = failStart ? ["--entrypoint", "/cohors-synthetic-missing-entrypoint"] : [];
            var created = await Run("create-sql", "docker",
                ["create", "--name", container.Name, "--label", "cohors.recovery.owner=" + owner,
                    "--label", "cohors.recovery.instance=" + container.OperationId,
                    "--publish", $"127.0.0.1:{port}:1433", "--memory", "3g", "--env", "ACCEPT_EULA=Y",
                    "--env", "MSSQL_PID=Developer", "--env", "MSSQL_MEMORY_LIMIT_MB=2048", "--env", "MSSQL_SA_PASSWORD",
                    "--volume", container.Volume + ":/var/opt/mssql", .. entrypoint, SqlImage],
                new Dictionary<string, string> { ["MSSQL_SA_PASSWORD"] = container.Password });
            await CaptureOwnedContainer(container);
            RequireSuccess(created, "create owned SQL");
            container.StartAttempted = true;
            await WriteOwnershipInventory();
            var started = await Run("start-sql", "docker", ["start", container.Id]);
            RequireSuccess(started, "start owned SQL");
            for (var attempt = 0; attempt < 90; attempt++)
            {
                try
                {
                    Assert.Equal(1, (int)(await Scalar(Connection(port, container.Password, "master"), "SELECT 1"))!);
                    return container;
                }
                catch (SqlException) when (attempt < 89) { await Task.Delay(TimeSpan.FromSeconds(2)); }
            }
            throw new InvalidOperationException("Owned SQL did not become ready.");
        }
        catch
        {
            await RemoveOwnedContainer(container);
            throw;
        }
    }

    private async Task CaptureOwnedContainer(OwnedContainer container)
    {
        // The per-create token identifies a residual instance even if the CLI returned no ID.
        var found = await RequireDocker("capture-created-sql", ["ps", "-a", "--no-trunc",
            "--filter", "label=cohors.recovery.owner=" + owner,
            "--filter", "label=cohors.recovery.instance=" + container.OperationId, "--format", "{{.ID}}"]);
        var ids = found.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length != 1 || !Regex.IsMatch(ids[0], "^[a-f0-9]{64}$"))
            throw new InvalidOperationException("SQL creation identity is unknown or ambiguous; cleanup is not confirmed.");
        container.Id = ids[0];
        await CheckOwnedContainer(container);
        container.CreatedId = container.Id;
        container.IdentityResolved = true;
        await WriteOwnershipInventory();
    }

    private async Task CheckOwnedContainer(OwnedContainer container)
    {
        var identity = await RequireDocker("container-identity", ["inspect", "--format",
            "{{.Id}}|{{.Name}}|{{index .Config.Labels \"cohors.recovery.owner\"}}|{{index .Config.Labels \"cohors.recovery.instance\"}}|{{range .Mounts}}{{if eq .Destination \"/var/opt/mssql\"}}{{.Name}}{{end}}{{end}}",
            container.Id]);
        if (identity.Output.Trim() != $"{container.Id}|/{container.Name}|{owner}|{container.OperationId}|{container.Volume}")
            throw new InvalidOperationException("Owned SQL ID, name, labels or volume changed; cleanup refused.");
    }

    private async Task CheckOwnedVolume(OwnedContainer container)
    {
        var identity = await RequireDocker("volume-identity", ["volume", "inspect", "--format",
            "{{.Name}}|{{index .Labels \"cohors.recovery.owner\"}}|{{index .Labels \"cohors.recovery.instance\"}}", container.Volume]);
        if (identity.Output.Trim() != $"{container.Volume}|{owner}|{container.OperationId}")
            throw new InvalidOperationException("Owned SQL volume identity changed; cleanup refused.");
    }

    private async Task RemoveOwnedContainer(OwnedContainer container)
    {
        try
        {
            if (container.CreateAttempted && !container.IdentityResolved) await CaptureOwnedContainer(container);
            if (container.Id.Length != 0)
            {
                await CheckOwnedContainer(container);
                await RequireDocker("cleanup-container", ["rm", "--force", container.Id]);
                container.Id = "";
            }
            if (container.VolumeCreationAttempted)
            {
                await CheckOwnedVolume(container);
                await RequireDocker("cleanup-volume", ["volume", "rm", container.Volume]);
                container.VolumeCreated = false;
                container.VolumeCreationAttempted = false;
            }
        }
        finally { await WriteOwnershipInventory(); }
    }

    private Task WriteOwnershipInventory(bool cleanupVerified = false) =>
        File.WriteAllTextAsync(Path.Combine(Directory, "owned-resources.json"), JsonSerializer.Serialize(new
        {
            scope = "Only this recovery fixture; verify live immutable IDs, labels and mounts before cleanup",
            cleanupVerified, owner,
            resources = containers.Select(container => new
            {
                container.Name, container.Id, container.CreatedId, container.OperationId, container.Volume,
                container.CreateAttempted, container.IdentityResolved, container.StartAttempted,
                container.VolumeCreationAttempted, container.VolumeCreated,
                labels = new Dictionary<string, string>
                {
                    ["cohors.recovery.owner"] = owner, ["cohors.recovery.instance"] = container.OperationId
                }
            })
        }, Canonical.Json));

    private async Task<RecoveryProcess> RequireDocker(string step, string[] arguments)
    {
        var result = await Run(step, "docker", arguments);
        RequireSuccess(result, step);
        return result;
    }

    private async Task<RecoveryProcess> Run(string step, string executable, string[] arguments,
        Dictionary<string, string>? environment = null, RecoveryInvocation? invocation = null)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = home, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var item in environment) start.Environment[item.Key] = item.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Recovery process could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var timedOut = false;
        try
        {
            if (invocation is not null) RecordScriptInvocation(invocation, process, terminal: false, timedOut: false);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(8));
        }
        catch (TimeoutException)
        {
            timedOut = true;
            throw new InvalidOperationException("Recovery step timed out; its exact process tree was stopped.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            if (invocation is not null) RecordScriptInvocation(invocation, process, terminal: true, timedOut);
        }
        var standardOutput = await stdout;
        var standardError = await stderr;
        var output = standardOutput + standardError;
        await File.WriteAllTextAsync(Path.Combine(Directory, "private", $"{++logNumber:D3}-{step}.log"), output);
        var diagnostic = invocation is not null && process.ExitCode != 0
            ? RecoveryDiagnostics.Capture(standardError, invocation, process.ExitCode) : null;
        if (diagnostic is not null) DiagnosticOutput?.WriteLine(diagnostic);
        return new(process.ExitCode, output, RecoveryProcess.ReadCodes(output)) { Diagnostic = diagnostic };
    }

    private static void RequireSuccess(RecoveryProcess result, string step)
    {
        if (result.ExitCode != 0)
        {
            var codes = result.Codes.Length == 0 ? "no controlled recovery code" : string.Join(",", result.Codes);
            throw new InvalidOperationException($"Recovery {step} failed (exit {result.ExitCode}; {codes}). Raw logs remain private." +
                (result.Diagnostic is null ? "" : Environment.NewLine + result.Diagnostic));
        }
    }

    private static int Port(string variable)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(configured))
        {
            var port = int.Parse(configured, System.Globalization.CultureInfo.InvariantCulture);
            if (port is < 1024 or > 65535) throw new InvalidOperationException("Recovery requires unprivileged loopback ports.");
            RequireFreePort(port);
            return port;
        }
        return FreePort();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static void RequireFreePort(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); }
        finally { listener.Stop(); }
    }

    private static string Connection(int port, string password, string database) =>
        $"Server=127.0.0.1,{port};Database={database};User Id=sa;Password={password};Encrypt=True;TrustServerCertificate=True;Pooling=False;Connect Timeout=15";
    private static string WithDatabase(string connection, string database) =>
        new SqlConnectionStringBuilder(connection) { InitialCatalog = database }.ConnectionString;
    public static string FileHash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 90 };
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<object?> Scalar(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    public static async Task<SortedDictionary<string, string>> Tables(string connection)
    {
        var tables = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await using var sql = new SqlConnection(connection);
        await sql.OpenAsync();
        foreach (var (table, order) in new[] { ("Aggregates", "Kind,Id"), ("AggregateVersions", "Kind,Id,Version"),
            ("AuditEvents", "Sequence"), ("Commands", "IdempotencyKey"), ("Deadlines", "Id"), ("Inbox", "EventId,Consumer"),
            ("Outbox", "Id"), ("SchemaVersions", "Version") })
        {
            using var command = new SqlCommand(
                $"SELECT COALESCE((SELECT * FROM dbo.[{table}] ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]')", sql) { CommandTimeout = 30 };
            // Preserve the complete oracle without asynchronous materialization of one multi-megabyte scalar.
            using var reader = command.ExecuteReader(System.Data.CommandBehavior.SequentialAccess);
            if (!reader.Read() || reader.IsDBNull(0)) throw new InvalidOperationException("Recovery table JSON is absent.");
            using var text = reader.GetTextReader(0);
            tables.Add(table, text.ReadToEnd());
            if (reader.Read()) throw new InvalidOperationException("Recovery table JSON is not a single complete result.");
        }
        return tables;
    }

    private Task RecordSetupPhase(string phase)
    {
        var timestamp = Stopwatch.GetTimestamp();
        var record = JsonSerializer.Serialize(new
        {
            variant = "RecoverySetup", phase, sequence = ++setupSequence,
            timestampTicks = timestamp, frequency = Stopwatch.Frequency,
            elapsedTicks = timestamp - setupStarted, clockScope = "fixture-construction",
            at = DateTimeOffset.UtcNow
        }, Canonical.Json);
        using var stream = new FileStream(Path.Combine(Directory, "private", "setup-phases.jsonl"),
            FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(Encoding.UTF8.GetBytes(record + Environment.NewLine));
        stream.Flush(flushToDisk: true);
        return Task.CompletedTask;
    }

    private void RecordScriptInvocation(RecoveryInvocation invocation, Process process, bool terminal, bool timedOut)
    {
        var timestamp = Stopwatch.GetTimestamp();
        var started = process.StartTime.ToUniversalTime();
        DateTime? ended = terminal ? process.ExitTime.ToUniversalTime() : null;
        var record = JsonSerializer.Serialize(new
        {
            variant = "RecoveryScript", phase = terminal ? "exited" : "started",
            invocation = invocation.Id, invocation.Operation, invocation.Test, entryScript = invocation.Script,
            timestampTicks = timestamp, frequency = Stopwatch.Frequency,
            elapsedTicks = timestamp - setupStarted, clockScope = "fixture-construction",
            processId = process.Id, processStartedUtc = started, processExitedUtc = ended,
            processDurationSeconds = ended is { } exit ? (double?)(exit - started).TotalSeconds : null,
            stepWaitLimitSeconds = 480, timedOut, at = DateTimeOffset.UtcNow
        }, Canonical.Json);
        using var stream = new FileStream(Path.Combine(Directory, "private", "script-invocations.jsonl"),
            FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(Encoding.UTF8.GetBytes(record + Environment.NewLine));
        stream.Flush(flushToDisk: true);
    }

    private static async Task<string> Event(string connectionString, string aggregate, string type, int version)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = SqlStateStore.Command(connection, null,
            "SELECT Id FROM dbo.Outbox WHERE AggregateId=@id AND Type=@type AND Version=@version",
            ("id", aggregate), ("type", type), ("version", version));
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed class OwnedContainer(string name, string password)
    {
        public string Name { get; } = name;
        public string Password { get; } = password;
        public string Volume => Name + "-data";
        public string OperationId { get; } = Guid.NewGuid().ToString("N");
        public bool VolumeCreationAttempted { get; set; }
        public bool VolumeCreated { get; set; }
        public bool CreateAttempted { get; set; }
        public bool IdentityResolved { get; set; }
        public bool StartAttempted { get; set; }
        public string CreatedId { get; set; } = "";
        public string Id { get; set; } = "";
    }
}

public sealed record RecoveryProcess(int ExitCode, string Output, string[] Codes)
{
    public string? Diagnostic { get; init; }
    private static readonly HashSet<string> ControlledCodes = new(StringComparer.Ordinal)
    {
        "recovery_configuration", "recovery_cut_changed", "recovery_database_hash", "recovery_database_shape",
        "recovery_docker", "recovery_manifest", "recovery_manifest_hash", "recovery_object_hash",
        "recovery_object_inventory", "recovery_object_missing", "recovery_object_shape", "recovery_ownership",
        "recovery_path", "recovery_path_overlap", "recovery_quiescence", "recovery_reference",
        "recovery_reference_coverage", "recovery_retention_authorization", "recovery_retention_binding",
        "recovery_retention_checkpoint", "recovery_retention_contract", "recovery_retention_incomplete",
        "recovery_retention_receipt", "recovery_retention_reference", "recovery_retention_resurrection",
        "recovery_retention_sql_cut", "recovery_retention_stale_cut", "recovery_retention_untracked",
        "recovery_runtime", "recovery_schema", "recovery_scope", "recovery_sql", "recovery_sql_changed",
        "recovery_sql_cut", "recovery_sql_integrity", "recovery_target_exists"
    };

    internal static string[] ReadCodes(string output) =>
        Regex.Matches(output, @"(?<![A-Za-z0-9_])recovery_[a-z_]+(?=:)")
            .Select(m => m.Value).Where(ControlledCodes.Contains).Distinct(StringComparer.Ordinal).ToArray();
}

internal sealed record RecoveryInvocation(string Id, string Operation, string Script, string? Test);
internal sealed record RecoverySqlFailure(int Number, byte State, byte Class);
internal sealed record RecoveryExceptionFailure(int? Parent, string? Type, int HResult,
    RecoverySqlFailure[]? SqlErrors, bool SqlTruncated, int? OsError);
internal sealed record RecoveryChildFailure(string Invocation, string? Script, int? Line,
    bool Truncated, RecoveryExceptionFailure[] Exceptions);

internal static class RecoveryDiagnostics
{
    internal const string Frame = "COHORS_RECOVERY_DIAGNOSTIC_V1=";
    internal const string EvidenceFrame = "COHORS_RECOVERY_FAILURE=";
    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "System.Exception", "System.AggregateException", "System.InvalidOperationException",
        "System.ArgumentException", "System.ArgumentNullException", "System.TimeoutException",
        "System.OperationCanceledException", "System.UnauthorizedAccessException", "System.IO.IOException",
        "System.IO.FileNotFoundException", "System.IO.DirectoryNotFoundException",
        "System.Text.Json.JsonException", "System.Text.DecoderFallbackException", "System.Text.EncoderFallbackException",
        "System.Reflection.TargetInvocationException", "System.TypeInitializationException",
        "System.ComponentModel.Win32Exception", "System.Net.Sockets.SocketException",
        "System.Management.Automation.RuntimeException", "System.Management.Automation.MethodInvocationException",
        "System.Management.Automation.ActionPreferenceStopException",
        "System.Management.Automation.CmdletInvocationException",
        "System.Management.Automation.ParameterBindingException", "Microsoft.Data.SqlClient.SqlException",
        "Cohors.Domain.DomainException", "Cohors.Application.ObjectStoreIoException"
    };
    private static readonly JsonSerializerOptions WireJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 12
    };

    internal static RecoveryInvocation Invocation(string script, string? test)
    {
        var operation = script switch
        {
            "Restore-Local.ps1" => "RestoreLocal",
            "Backup-Local.ps1" => "BackupLocal",
            _ => throw new InvalidOperationException("Unknown recovery test script.")
        };
        var method = test is null ? null : typeof(RecoveryTests).GetMethod(test) ?? typeof(EudamedRecoveryTests).GetMethod(test);
        return new(Guid.NewGuid().ToString("N"), operation, script,
            method?.GetCustomAttribute<FactAttribute>() is not null ? test : null);
    }

    internal static string Capture(string output, RecoveryInvocation invocation, int exitCode)
    {
        var frames = output.Split('\n').Where(line => line.StartsWith(Frame, StringComparison.Ordinal)).Take(2).ToArray();
        RecoveryChildFailure? failure = null;
        var status = frames.Length == 0 ? "Missing" : "Rejected";
        if (frames.Length == 1 && frames[0].Length <= 32768)
        {
            try
            {
                var candidate = JsonSerializer.Deserialize<RecoveryChildFailure>(frames[0][Frame.Length..], WireJson);
                if (candidate is not null && Valid(candidate, invocation))
                {
                    failure = candidate with { Exceptions = candidate.Exceptions.Select(error =>
                        error with { Type = error.Type is not null && KnownTypes.Contains(error.Type) ? error.Type : null }).ToArray() };
                    status = "Captured";
                }
            }
            catch (JsonException) { status = "Rejected"; }
        }
        return EvidenceFrame + JsonSerializer.Serialize(new
        {
            contract = "cohors-recovery-failure-1", invocation.Operation, invocation.Test,
            testCorrelation = "enclosing-xunit-record", invocation = invocation.Id,
            entryScript = invocation.Script, exitCode, capture = status,
            errorScript = failure?.Script, line = failure?.Line, truncated = failure?.Truncated,
            exceptions = failure?.Exceptions
        });
    }

    private static bool Valid(RecoveryChildFailure failure, RecoveryInvocation invocation)
    {
        if (failure.Invocation != invocation.Id || failure.Script is not (null or "Restore-Local.ps1" or "Backup-Local.ps1") ||
            (failure.Script is null ? failure.Line is not null : failure.Line is not > 0) ||
            failure.Exceptions is not { Length: > 0 and <= 16 }) return false;
        for (var index = 0; index < failure.Exceptions.Length; index++)
        {
            var error = failure.Exceptions[index];
            if (error is null || (index == 0 ? error.Parent is not null : error.Parent is null || error.Parent < 0 || error.Parent >= index) ||
                error.SqlErrors is { Length: > 16 } || error.SqlErrors?.Any(item => item is null) == true ||
                (error.SqlTruncated && error.SqlErrors is null) ||
                (error.Type != "Microsoft.Data.SqlClient.SqlException" && (error.SqlErrors is not null || error.SqlTruncated)) ||
                (error.Type is not ("System.ComponentModel.Win32Exception" or "System.Net.Sockets.SocketException") && error.OsError is not null))
                return false;
        }
        return true;
    }

    internal const string Projection = """
        function Get-CohorsRecoveryFailure([Management.Automation.ErrorRecord]$Record, [string]$Invocation) {
            $queue = [Collections.Generic.List[object]]::new()
            $queue.Add(@{ value = $Record.Exception; parent = $null })
            $nodes = [Collections.Generic.List[object]]::new()
            $truncated = $false
            $script = $null
            $line = $null
            for ($index = 0; $index -lt $queue.Count; $index++) {
                $item = $queue[$index]
                $exception = $item.value
                $type = $exception.GetType().FullName
                $records = @($Record)
                if ($exception -is [Management.Automation.RuntimeException] -and $exception.ErrorRecord) {
                    $records += $exception.ErrorRecord
                }
                foreach ($entry in $records) {
                    if (-not $script -and $entry.InvocationInfo) {
                        $name = [IO.Path]::GetFileName($entry.InvocationInfo.ScriptName)
                        if ($name -cin @('Restore-Local.ps1', 'Backup-Local.ps1') -and $entry.InvocationInfo.ScriptLineNumber -gt 0) {
                            $script = $name
                            $line = [int]$entry.InvocationInfo.ScriptLineNumber
                        }
                    }
                }
                $sql = $null
                $sqlTruncated = $false
                if ($type -ceq 'Microsoft.Data.SqlClient.SqlException') {
                    $sql = @($exception.Errors | Select-Object -First 16 | ForEach-Object {
                        [ordered]@{ Number = [int]$_.Number; State = [int]$_.State; Class = [int]$_.Class }
                    })
                    $sqlTruncated = $exception.Errors.Count -gt 16
                }
                $os = if ($exception -is [ComponentModel.Win32Exception]) { [int]$exception.NativeErrorCode } else { $null }
                $nodes.Add([ordered]@{ Parent = $item.parent; Type = $type; HResult = [int]$exception.HResult;
                    SqlErrors = $sql; SqlTruncated = $sqlTruncated; OsError = $os })
                $children = if ($exception -is [AggregateException]) { @($exception.InnerExceptions) }
                    elseif ($exception.InnerException) { @($exception.InnerException) } else { @() }
                foreach ($child in $children) {
                    if ($queue.Count -lt 16) { $queue.Add(@{ value = $child; parent = $index }) }
                    else { $truncated = $true }
                }
            }
            return [ordered]@{ Invocation = $Invocation; Script = $script; Line = $line;
                Truncated = $truncated; Exceptions = $nodes.ToArray() }
        }
        function Invoke-CohorsRecoveryChild([scriptblock]$Action, [string]$Invocation) {
            try { & $Action }
            catch {
                $failure = $_
                # Diagnostic or stream failures must not replace the script's original failure.
                try {
                    $projected = Get-CohorsRecoveryFailure $failure $Invocation
                    [Console]::Error.WriteLine([Environment]::NewLine + 'COHORS_RECOVERY_DIAGNOSTIC_V1=' +
                        (ConvertTo-Json -InputObject $projected -Depth 8 -Compress))
                } finally { throw $failure }
            }
        }
        """;

    internal const string Bootstrap = Projection + """

        $parameters = ConvertFrom-Json -InputObject $env:COHORS_RECOVERY_PARAMETERS -AsHashtable
        Invoke-CohorsRecoveryChild { & $env:COHORS_RECOVERY_ENTRY @parameters } $env:COHORS_RECOVERY_INVOCATION
        """;
}

[Trait("Category", "RecoveryDiagnostics")]
public sealed class RecoveryDiagnosticTests
{
    [Theory]
    [InlineData("f0123456789abcdef0123456789abcde")]
    [InlineData("cd0123456789abcdef0123456789abcd")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void T20_DiagnosticParserDoesNotTreatNativeBackupNamesAsCodes(string identifier)
    {
        var output = $"Native operation failed for /var/opt/mssql/data/cohors_recovery_{identifier}.bak.";
        Assert.Empty(RecoveryProcess.ReadCodes(output));
    }

    [Fact]
    public void T20_DiagnosticParserRetainsExactFailureCodesWithConsoleDecoration()
    {
        const string output = "\u001b[31;1mException: recovery_reference_coverage: SQL references absent bytes.\u001b[0m\n" +
            "recovery_reference_coverage: SQL references absent bytes.\n" +
            "recovery_object_hash: Object bytes differ from their address.\n" +
            "cohors_recovery_f0123456789abcdef0123456789abcde.bak\n" +
            "recovery_secret_token_do_not_publish: synthetic untrusted message";
        Assert.Equal(new[] { "recovery_reference_coverage", "recovery_object_hash" }, RecoveryProcess.ReadCodes(output));
    }

    [Fact]
    public async Task T20_DiagnosticsProjectNestedErrorsWithoutMessagesAndRethrowAfterCleanup()
    {
        var invocation = RecoveryDiagnostics.Invocation("Restore-Local.ps1",
            nameof(RecoveryTests.T20_RestoresHistoricalBytesFrozenPackageUnknownDeadlinesReceiptsAndLocalSearchWithoutSource));
        var output = await Probe(RecoveryDiagnostics.Projection + "\n" + NestedProbe, invocation.Id);
        Assert.DoesNotContain("SYNTHETIC_SECRET_", output);
        Assert.Contains("RECOVERY_PROBE_CLEANUP=1; ORIGINAL=True; SUCCESS=unchanged", output);
        Assert.Contains("RECOVERY_PROBE_DIAGNOSTIC_FAILURE=preserved; BOUNDED=16", output);
        var evidence = ReadEvidence(RecoveryDiagnostics.Capture(output, invocation, 1));
        Assert.Equal("Captured", evidence["capture"]!.GetValue<string>());
        Assert.Equal(invocation.Test, evidence["Test"]!.GetValue<string>());
        Assert.Equal("Restore-Local.ps1", evidence["errorScript"]!.GetValue<string>());
        Assert.True(evidence["line"]!.GetValue<int>() > 0);
        var errors = evidence["exceptions"]!.AsArray();
        Assert.Contains(errors, error => error!["Type"]!.GetValue<string>() == "System.AggregateException");
        Assert.Contains(errors, error => error!["Type"]!.GetValue<string>() == "System.IO.IOException");
        var native = Assert.Single(errors, error => error!["Type"]!.GetValue<string>() == "System.ComponentModel.Win32Exception");
        Assert.Equal(13, native!["OsError"]!.GetValue<int>());
        Assert.True(native["Parent"]!.GetValue<int>() >= 0);
        Assert.All(errors, error => Assert.Null(error!["SqlErrors"]));
    }

    [Fact]
    public void T20_DiagnosticsKeepTypedNumbersAndExplicitlyMarkUnavailableFields()
    {
        var invocation = RecoveryDiagnostics.Invocation("Restore-Local.ps1", "SYNTHETIC_SECRET_CALLER");
        var evidence = ReadEvidence(RecoveryDiagnostics.Capture(SyntheticFrame(invocation), invocation, 1));
        Assert.Equal("Captured", evidence["capture"]!.GetValue<string>());
        Assert.Equal("RestoreLocal", evidence["Operation"]!.GetValue<string>());
        Assert.Equal(invocation.Id, evidence["invocation"]!.GetValue<string>());
        Assert.Null(evidence["Test"]);
        Assert.Equal(138, evidence["line"]!.GetValue<int>());
        Assert.Equal(-2146232060, evidence["exceptions"]![1]!["HResult"]!.GetValue<int>());
        Assert.Equal(3201, evidence["exceptions"]![1]!["SqlErrors"]![0]!["Number"]!.GetValue<int>());
        Assert.Equal(1, evidence["exceptions"]![1]!["SqlErrors"]![0]!["State"]!.GetValue<int>());
        Assert.Equal(16, evidence["exceptions"]![1]!["SqlErrors"]![0]!["Class"]!.GetValue<int>());
        Assert.Null(evidence["exceptions"]![1]!["OsError"]);
        var missing = RecoveryDiagnostics.Capture("SYNTHETIC_SECRET_Password=x; no structured child frame", invocation, 1);
        Assert.DoesNotContain("SYNTHETIC_SECRET_", missing);
        evidence = ReadEvidence(missing);
        Assert.Equal("Missing", evidence["capture"]!.GetValue<string>());
        Assert.Null(evidence["errorScript"]);
        Assert.Null(evidence["line"]);
        Assert.Null(evidence["exceptions"]);
        var frame = JsonNode.Parse(SyntheticFrame(invocation)[RecoveryDiagnostics.Frame.Length..])!;
        frame["Exceptions"]![0]!["Type"] = "SYNTHETIC_SECRET_TYPE";
        var filtered = RecoveryDiagnostics.Capture(RecoveryDiagnostics.Frame + frame.ToJsonString(), invocation, 1);
        Assert.DoesNotContain("SYNTHETIC_SECRET_", filtered);
        Assert.Null(ReadEvidence(filtered)["exceptions"]![0]!["Type"]);
    }

    [Fact]
    public void T20_DiagnosticsRejectUncorrelatedMalformedAndSecretBearingFields()
    {
        var invocation = RecoveryDiagnostics.Invocation("Restore-Local.ps1", null);
        foreach (var mutation in new[] { "correlation", "message", "sql-text", "numeric-string", "parent", "duplicate", "malformed" })
        {
            var body = JsonNode.Parse(SyntheticFrame(invocation)[RecoveryDiagnostics.Frame.Length..])!;
            switch (mutation)
            {
                case "correlation": body["Invocation"] = new string('0', 32); break;
                case "message": body["Message"] = "SYNTHETIC_SECRET_Password=outer"; break;
                case "sql-text": body["Exceptions"]![1]!["SqlErrors"]![0]!["CommandText"] = "SYNTHETIC_SECRET_SQL"; break;
                case "numeric-string": body["Exceptions"]![1]!["SqlErrors"]![0]!["Number"] = "SYNTHETIC_SECRET_TOKEN"; break;
                case "parent": body["Exceptions"]![1]!["Parent"] = 1; break;
            }
            var frame = RecoveryDiagnostics.Frame + body.ToJsonString();
            if (mutation == "duplicate") frame += "\n" + frame;
            if (mutation == "malformed") frame = RecoveryDiagnostics.Frame + "{SYNTHETIC_SECRET_BROKEN";
            var filtered = RecoveryDiagnostics.Capture(frame, invocation, 1);
            Assert.DoesNotContain("SYNTHETIC_SECRET_", filtered);
            var evidence = ReadEvidence(filtered);
            Assert.Equal("Rejected", evidence["capture"]!.GetValue<string>());
            Assert.Equal(1, evidence["exitCode"]!.GetValue<int>());
            Assert.Null(evidence["exceptions"]);
        }
    }

    private static JsonObject ReadEvidence(string value) =>
        JsonNode.Parse(value[RecoveryDiagnostics.EvidenceFrame.Length..])!.AsObject();

    private static string SyntheticFrame(RecoveryInvocation invocation) =>
        RecoveryDiagnostics.Frame + JsonSerializer.Serialize(new RecoveryChildFailure(invocation.Id, "Backup-Local.ps1", 138, false,
        [
            new(null, "System.Management.Automation.MethodInvocationException", -2146233087, null, false, null),
            new(0, "Microsoft.Data.SqlClient.SqlException", -2146232060, [new(3201, 1, 16)], false, null)
        ]));

    internal static async Task<string> Probe(string script, string invocation)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("COHORS_RECOVERY_PWSH") ?? "pwsh")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        start.Environment["COHORS_RECOVERY_INVOCATION"] = invocation;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Synthetic diagnostic probe could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, "Synthetic diagnostic probe failed; unfiltered child output is not published.");
        return output;
    }

    internal const string NestedProbe = """
        $script:cleanupCount = 0
        $inner = [ComponentModel.Win32Exception]::new(13, 'SYNTHETIC_SECRET_NATIVE_PASSWORD')
        $middle = [IO.IOException]::new('SYNTHETIC_SECRET_FULL_PATH_AND_SQL', $inner)
        $script:original = [AggregateException]::new('SYNTHETIC_SECRET_OUTER_TOKEN',
            [Exception[]]@($middle, [InvalidOperationException]::new('SYNTHETIC_SECRET_SIBLING')))
        $script:original.Data['Password'] = 'SYNTHETIC_SECRET_DATA'
        $tokens = $null
        $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseInput(
            'try { throw $script:original } finally { $script:cleanupCount++ }',
            [IO.Path]::Combine([IO.Path]::GetTempPath(), 'SYNTHETIC_SECRET_DIRECTORY', 'Restore-Local.ps1'),
            [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw 'Synthetic probe syntax failed.' }
        $same = $false
        try { Invoke-CohorsRecoveryChild $ast.GetScriptBlock() $env:COHORS_RECOVERY_INVOCATION }
        catch { $same = [object]::ReferenceEquals($_.Exception, $script:original) }
        if (-not $same -or $script:cleanupCount -ne 1) { throw 'Synthetic failure or cleanup was changed.' }
        $success = Invoke-CohorsRecoveryChild { 'unchanged' } $env:COHORS_RECOVERY_INVOCATION
        if ($success -cne 'unchanged') { throw 'Synthetic success output was changed.' }
        Write-Output "RECOVERY_PROBE_CLEANUP=$script:cleanupCount; ORIGINAL=$same; SUCCESS=$success"
        $writer = [Console]::Error
        $closed = [IO.StringWriter]::new()
        $closed.Dispose()
        try {
            [Console]::SetError($closed)
            $preserved = $false
            try { Invoke-CohorsRecoveryChild { throw $script:original } $env:COHORS_RECOVERY_INVOCATION }
            catch { $preserved = [object]::ReferenceEquals($_.Exception, $script:original) }
            if (-not $preserved) { throw 'Diagnostic emission masked the original exception.' }
        } finally { [Console]::SetError($writer) }
        $deep = [Exception]::new('SYNTHETIC_SECRET_DEEP')
        for ($index = 0; $index -lt 20; $index++) { $deep = [Exception]::new('SYNTHETIC_SECRET_DEEP', $deep) }
        $record = [Management.Automation.ErrorRecord]::new($deep, 'SYNTHETIC_SECRET_ERROR_ID',
            [Management.Automation.ErrorCategory]::NotSpecified, 'SYNTHETIC_SECRET_TARGET')
        $bounded = Get-CohorsRecoveryFailure $record $env:COHORS_RECOVERY_INVOCATION
        if (-not $bounded.Truncated -or $bounded.Exceptions.Count -ne 16 -or $bounded.Script -or $bounded.Line) {
            throw 'Synthetic chain limit or explicit missing source changed.'
        }
        if ((ConvertTo-Json -InputObject $bounded -Depth 8 -Compress).Contains('SYNTHETIC_SECRET_')) {
            throw 'Synthetic metadata projection exposed forbidden fields.'
        }
        Write-Output 'RECOVERY_PROBE_DIAGNOSTIC_FAILURE=preserved; BOUNDED=16'
        """;
}

public sealed class RecoveryTarget(RecoveryFixture fixture, string home, string database, string objectRoot,
    string connection, string configPath) : IAsyncDisposable
{
    public string Home { get; } = home;
    public string Database { get; } = database;
    public string ObjectRoot { get; } = objectRoot;
    public string Connection { get; } = connection;
    public string ConfigPath { get; } = configPath;
    public string ReadyPath => ObjectRoot + ".recovery-ready.json";
    public bool OwnsDatabase { get; set; }
    public async ValueTask DisposeAsync() => await fixture.Drop(this);
}

public sealed partial class RecoveryFixture
{
    internal ImportRecoveryData Imports { get; private set; } = null!;
    public bool ImportRecoveryVerified { get; set; }
}

// Block-1 imports are created through the real import/approval services before the native backup;
// verification reads only the restored target while the source is unavailable.
internal sealed record ImportRecoveryData(IntakeImport Intake, string IntakeContent, DistributionImport Distribution,
    string DistributionContent, DenominatorPreview Preview, Exposure Exposure, SafetyCase RegisteredCase,
    SortedDictionary<string, string> History, SortedDictionary<string, string> Audit,
    SortedDictionary<string, string> Manifests)
{
    private const string Product = "ortho";
    private const string Region = "Worldwide";
    private const string Reason =
        "Synthetischer Kanal- und Lieferimport fuer den isolierten Wiederherstellungsnachweis; keine Echtdaten.";
    private const string ApprovalReason =
        "Lieferdatei, Region Worldwide und abgeschlossenes Q3 menschlich geprueft; synthetischer Recovery-Nenner.";
    private const string IntakeFile = "recovery-intake-2026-11.csv";
    private const string DistributionFile = "ortho-distribution-2026-q3.csv";
    private const string Row = "HF-RECOVERY-1,IMP-RECOVERY-1,ortho,DEMO-OL-M3-101,DEMO-OL-2026-A,IE,EU-IE,OtherSerious," +
        "2026-11-01T10:00:00Z,2026-11-10T08:00:00Z,Synthetische Lockerung Recovery," +
        "SYNTHETISCH: Kanaltext zur Lockerung ohne Personenbezug.";
    private const string Rejected = "HF-RECOVERY-2,IMP-RECOVERY-2,ortho,DEMO-OL-M3-104,DEMO-OL-2026-C,DE,EU-IE,OtherSerious,," +
        "2026-11-06T11:00:00Z,Widerspruechliche Laenderangabe," +
        "SYNTHETISCH: Land und Jurisdiktion widersprechen sich; keine stille Korrektur.";
    private static readonly DateOnly Start = new(2026, 7, 1);
    private static readonly DateOnly End = new(2026, 9, 30);
    private static readonly string[] Outcomes =
        ["2:Registered:registered", "3:Duplicate:row_duplicate", "4:Rejected:row_jurisdiction_country"];

    internal static async Task<ImportRecoveryData> Prepare(SqlFixture source, DateTimeOffset at)
    {
        var clock = new TestClock(at);
        var imports = new ImportService(source.Store, source.Objects, source.Policies, source.Service(clock), clock);

        var distributionContent = await File.ReadAllTextAsync(
            Path.Combine(RecoveryFixture.RepositoryRoot(), "demo", "imports", DistributionFile));
        var distribution = await imports.ImportDistribution(new(Product, DistributionFile, distributionContent, Reason),
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(DistributionImportStatus.Validated, distribution.Status);
        Assert.Equal(1, distribution.Version);
        Assert.Equal(Canonical.HashText(distributionContent), distribution.Original.Hash);
        Assert.Empty(distribution.Findings);
        Assert.Equal(new DistributionTotals(7, 7, 0, 225, 186, 181, 5), distribution.Totals);

        var preview = await imports.Proposal(distribution.Id, Region, Start, End, SqlFixture.Quality);
        Assert.Equal(ImportRules.ExposureId(Product, Region, Start, End), preview.TargetExposureId);
        Assert.Equal(0, preview.TargetVersion);
        Assert.Null(preview.CurrentCount);
        Assert.False(preview.Unchanged);
        Assert.Equal(178, preview.Proposal.Count);
        Assert.Equal(173, preview.Proposal.Recipients);
        Assert.Equal(5, preview.Proposal.AdditionalImplants);
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7 }, preview.Proposal.Lines);
        var exposure = await imports.ApproveDenominator(new(distribution.Id, distribution.Version, Region, Start, End,
            178, 0, ApprovalReason), SqlFixture.Quality, CaseScenario.Key());
        Assert.Equal(preview.TargetExposureId, exposure.Id);
        Assert.Equal(1, exposure.Version);
        Assert.Equal(178, exposure.Count);
        Assert.True(exposure.Approved);
        Assert.Equal(new VersionReference(distribution.Id, distribution.Version, Canonical.Hash(distribution)),
            exposure.Source!.Import);
        Assert.Equal(preview.Proposal.Hash, exposure.Source.ProposalHash);

        var intakeContent = ImportRules.IntakeHeader + "\n" + Row + "\n" + Row + "\n" + Rejected;
        var intake = await imports.ImportIntake(new(Product, IntakeImportChannel.SyntheticDistributorFeedback, IntakeFile,
            intakeContent, Reason, true), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(IntakeImportStatus.Processed, intake.Status);
        Assert.Equal(2, intake.Version);
        Assert.Equal(Canonical.HashText(intakeContent), intake.Original.Hash);
        Assert.Equal(new IntakeImportCounts(3, 1, 1, 1, 0, 0), intake.Counts);
        Assert.Equal(Outcomes, RowOutcomes(intake));
        var registered = intake.Rows.Single(r => r.Line == 2);
        var duplicate = intake.Rows.Single(r => r.Line == 3);
        Assert.Equal(CaseIdentity.For(Product, "IMP-RECOVERY-1"), registered.Case!.Id);
        Assert.Equal("SyntheticDistributorFeedback:HF-RECOVERY-1", registered.SourceId);
        Assert.Equal(2, duplicate.DuplicateOf);
        Assert.Equal(registered.Case, duplicate.Case);
        Assert.Null(intake.Rows.Single(r => r.Line == 4).Case);

        var registeredCase = await Current<SafetyCase>(source.Store, "case", registered.Case.Id);
        Assert.Equal(registered.Case, PortfolioRules.Reference(await source.Store.GetVersion<SafetyCase>("case",
            registered.Case.Id, registered.Case.Version) ?? throw new InvalidOperationException("Registered case version is missing.")));
        Assert.Single(await source.Store.Audit(registeredCase.Id), a => a.Action == "ComplaintRegistered");
        Assert.Equal(new[] { "IntakeImportReceived", "IntakeImportProcessed" },
            (await source.Store.Audit(intake.Id)).Select(a => a.Action));
        Assert.Equal(new[] { "DistributionImported" }, (await source.Store.Audit(distribution.Id)).Select(a => a.Action));
        Assert.Equal(new[] { "ExposureDenominatorApproved" }, (await source.Store.Audit(exposure.Id)).Select(a => a.Action));

        var (history, audit, manifests) = await Capture(source.Store, intake, distribution, exposure, registeredCase);
        return new(intake, intakeContent, distribution, distributionContent, preview, exposure, registeredCase,
            history, audit, manifests);
    }

    internal async Task Verify(RecoveryFixture fixture, RecoveryTarget target, SqlStateStore sql, IObjectStore objects,
        CaseService cases, TimeProvider clock)
    {
        foreach (var (original, content) in new (ImportOriginal Original, string Content)[]
            { (Intake.Original, IntakeContent), (Distribution.Original, DistributionContent) })
        {
            var path = Path.Combine(target.ObjectRoot, original.Hash);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(fixture.SourceBytes[original.Hash], bytes);
            Assert.Equal(Encoding.UTF8.GetBytes(content), bytes);
            Assert.Equal(original.Hash, RecoveryFixture.FileHash(path));
            Assert.Equal(original.Bytes, bytes.Length);
            Assert.Equal(content, await objects.Read(original.Hash));
        }

        var (history, audit, manifests) = await Capture(sql, Intake, Distribution, Exposure, RegisteredCase);
        Assert.Equal(History, history);
        Assert.Equal(Audit, audit);
        Assert.Equal(Manifests, manifests);
        var intake = await Current<IntakeImport>(sql, ImportService.IntakeKind, Intake.Id);
        var distribution = await Current<DistributionImport>(sql, ImportService.DistributionKind, Distribution.Id);
        var exposure = await Current<Exposure>(sql, "exposure", Exposure.Id);
        Assert.Equal(Canonical.Serialize(Intake), Canonical.Serialize(intake));
        Assert.Equal(Canonical.Serialize(Distribution), Canonical.Serialize(distribution));
        Assert.Equal(Canonical.Serialize(Exposure), Canonical.Serialize(exposure));
        Assert.Equal(Canonical.Serialize(RegisteredCase),
            Canonical.Serialize(await Current<SafetyCase>(sql, "case", RegisteredCase.Id)));
        Assert.Equal(new VersionReference(distribution.Id, distribution.Version, Canonical.Hash(distribution)),
            exposure.Source!.Import);
        Assert.Equal(Preview.Proposal.Hash, exposure.Source.ProposalHash);
        Assert.Equal(Preview.Proposal.Lines, exposure.Source.Lines);
        foreach (var row in intake.Rows.Where(r => r.Case is not null))
            Assert.Equal(row.Case, PortfolioRules.Reference(await sql.GetVersion<SafetyCase>("case", row.Case!.Id,
                row.Case.Version) ?? throw new InvalidOperationException("Bound case version is missing.")));

        var imports = new ImportService(sql, objects, fixture.Policies, cases, clock);
        Assert.Equal(Canonical.Serialize(Intake), Canonical.Serialize(await imports.GetIntake(Intake.Id, SqlFixture.Pms)));
        Assert.Equal(Canonical.Serialize(Distribution),
            Canonical.Serialize(await imports.GetDistribution(Distribution.Id, SqlFixture.Quality)));
        var preview = await imports.Proposal(Distribution.Id, Region, Start, End, SqlFixture.Quality);
        Assert.Equal(Exposure.Id, preview.TargetExposureId);
        Assert.Equal(Exposure.Version, preview.TargetVersion);
        Assert.Equal(Exposure.Count, preview.CurrentCount);
        Assert.True(preview.Unchanged);
        Assert.Equal(Canonical.Serialize(Preview.Proposal), Canonical.Serialize(preview.Proposal));
        // Fresh-key replays of the same originals are refused before any object, row or SQL write.
        var intakeReplay = await Assert.ThrowsAsync<DomainException>(() => imports.ImportIntake(new(Intake.ProductId,
            Intake.Channel, IntakeFile, IntakeContent, Reason, true), SqlFixture.Pms, CaseScenario.Key()));
        Assert.Equal("import_duplicate_file", intakeReplay.Code);
        var distributionReplay = await Assert.ThrowsAsync<DomainException>(() => imports.ImportDistribution(new(
            Distribution.ProductId, DistributionFile, DistributionContent, Reason), SqlFixture.Pms, CaseScenario.Key()));
        Assert.Equal("import_duplicate_file", distributionReplay.Code);
    }

    internal object Evidence(bool verified) => new
    {
        sourceAndTargetContentVerified = verified,
        intake = new { Intake.Id, Intake.Version, hash = Canonical.Hash(Intake), originalHash = Intake.Original.Hash,
            originalBytes = Intake.Original.Bytes, status = Intake.Status.ToString(), counts = Intake.Counts,
            rows = RowOutcomes(Intake).ToArray() },
        distribution = new { Distribution.Id, Distribution.Version, hash = Canonical.Hash(Distribution),
            originalHash = Distribution.Original.Hash, originalBytes = Distribution.Original.Bytes,
            status = Distribution.Status.ToString(), totals = Distribution.Totals },
        exposure = new { Exposure.Id, Exposure.Version, hash = Canonical.Hash(Exposure), Exposure.Count,
            sourceImport = Exposure.Source!.Import, proposalHash = Exposure.Source.ProposalHash },
        registeredCase = new { RegisteredCase.Id, RegisteredCase.Version, hash = Canonical.Hash(RegisteredCase) },
        historyHashes = History.ToDictionary(item => item.Key, item => Canonical.HashText(item.Value)),
        auditHashes = Audit.ToDictionary(item => item.Key, item => Canonical.HashText(item.Value)),
        manifestHashes = Manifests.ToDictionary(item => item.Key, item => Canonical.HashText(item.Value))
    };

    private static IEnumerable<string> RowOutcomes(IntakeImport import) =>
        import.Rows.Select(row => $"{row.Line}:{row.Outcome}:{row.Code}");

    private static async Task<T> Current<T>(SqlStateStore store, string kind, string id) where T : class =>
        await store.Get<T>(kind, id) ?? throw new InvalidOperationException($"Current {kind} state is missing.");

    private static async Task<(SortedDictionary<string, string> History, SortedDictionary<string, string> Audit,
        SortedDictionary<string, string> Manifests)> Capture(SqlStateStore store, IntakeImport intake,
        DistributionImport distribution, Exposure exposure, SafetyCase registeredCase)
    {
        var history = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var audit = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var manifests = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await Versions<IntakeImport>(store, history, audit, manifests, ImportService.IntakeKind, intake.Id, intake.Version);
        await Versions<DistributionImport>(store, history, audit, manifests, ImportService.DistributionKind,
            distribution.Id, distribution.Version);
        await Versions<Exposure>(store, history, audit, manifests, "exposure", exposure.Id, exposure.Version);
        await Versions<SafetyCase>(store, history, audit, manifests, "case", registeredCase.Id, registeredCase.Version);
        return (history, audit, manifests);
    }

    private static async Task Versions<T>(SqlStateStore store, SortedDictionary<string, string> history,
        SortedDictionary<string, string> audit, SortedDictionary<string, string> manifests, string kind, string id,
        int current) where T : class
    {
        for (var version = 1; version <= current; version++)
            history[$"{kind}/{id}/{version}"] = Canonical.Serialize(await store.GetVersion<T>(kind, id, version)
                ?? throw new InvalidOperationException($"Historical {kind} version {version} is missing."));
        audit[$"{kind}/{id}"] = Canonical.Serialize(await store.Audit(id));
        var manifest = await store.Manifest(id, kind: kind);
        Assert.True(manifest.CurrentStateVerified);
        Assert.True(manifest.HashChainVerified);
        Assert.Equal(current, manifest.CurrentVersion);
        manifests[$"{kind}/{id}"] = Canonical.Serialize(manifest);
    }
}
