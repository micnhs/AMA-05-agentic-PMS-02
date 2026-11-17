using Cohors.Domain;
using System.Text.Json;

namespace Cohors.Tests;

[Trait("Category", "SqlFixtureContract")]
public sealed class SqlFixtureResourceTests
{
    [Fact]
    public async Task NestedFixturesKeepEveryOwnerAndIncompleteInitializationInTheDurableJournal()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cohors-fixtures-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "fresh", "resources.jsonl");
        var first = new SqlFixtureResource("Cohors_Test_first", "first-blobs", Environment.ProcessId,
            "synthetic-checkout", "CreateRequested", DateTimeOffset.UtcNow);
        var second = first with { Database = "Cohors_Test_second", ObjectDirectory = "second-blobs" };
        try
        {
            await SqlFixtureResources.Append(path, first);
            await SqlFixtureResources.Append(path, first with { Stage = "DatabaseCreated" });
            await SqlFixtureResources.Append(path, second);
            await SqlFixtureResources.Append(path, second with { Stage = "DatabaseCreated" });
            await SqlFixtureResources.Append(path, second with { Stage = "Disposed" });
            var records = (await File.ReadAllLinesAsync(path)).Select(Canonical.Read<SqlFixtureResource>).ToArray();
            Assert.Equal(5, records.Length);
            Assert.Equal(first, records[0]);
            Assert.Equal("DatabaseCreated", records.Last(r => r.Database == first.Database).Stage);
            Assert.Equal(second, records.Single(r => r.Database == second.Database && r.Stage == "CreateRequested"));
            Assert.Equal("Disposed", records.Last(r => r.Database == second.Database).Stage);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentOwnersAppendCompleteRecordsRatherThanReplacingAnotherFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cohors-fixtures-{Guid.NewGuid():N}.jsonl");
        var records = Enumerable.Range(0, 12).Select(i => new SqlFixtureResource($"Cohors_Test_{i}", $"blobs-{i}",
            Environment.ProcessId, "synthetic-checkout", "CreateRequested", DateTimeOffset.UtcNow)).ToArray();
        try
        {
            await Task.WhenAll(records.Select(record => SqlFixtureResources.Append(path, record)));
            var actual = (await File.ReadAllLinesAsync(path)).Select(Canonical.Read<SqlFixtureResource>).ToArray();
            Assert.Equal(records.OrderBy(r => r.Database), actual.OrderBy(r => r.Database));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CleanupStillRemovesOwnedResourcesWhenItsStartJournalFails()
    {
        var failure = new IOException("Synthetic journal I/O failure.");
        List<string> steps = [];
        var observed = await Assert.ThrowsAsync<IOException>(() => SqlFixtureResources.Cleanup(
            stage =>
            {
                steps.Add(stage);
                return stage == "CleanupRequested" ? Task.FromException(failure) : Task.CompletedTask;
            },
            () => { steps.Add("own cleanup"); return Task.CompletedTask; }));
        Assert.Same(failure, observed);
        Assert.Equal(["CleanupRequested", "own cleanup", "Disposed"], steps);
    }

    [Fact]
    public async Task JournalAndCleanupFailuresBothRemainVisibleWithoutAFalseDisposedRecord()
    {
        var journalFailure = new UnauthorizedAccessException("Synthetic journal access failure.");
        var cleanupFailure = new IOException("Synthetic owned object cleanup failure.");
        List<string> stages = [];
        var observed = await Assert.ThrowsAsync<AggregateException>(() => SqlFixtureResources.Cleanup(
            stage => { stages.Add(stage); return Task.FromException(journalFailure); },
            () => Task.FromException(cleanupFailure)));
        Assert.Equal([journalFailure, cleanupFailure], observed.InnerExceptions);
        Assert.Equal(["CleanupRequested"], stages);
    }

    [Fact]
    public async Task CompletionJournalFailureDoesNotTurnCleanupIntoAReportedSuccess()
    {
        var failure = new IOException("Synthetic completion recording failure.");
        var removed = false;
        var observed = await Assert.ThrowsAsync<IOException>(() => SqlFixtureResources.Cleanup(
            stage => stage == "Disposed" ? Task.FromException(failure) : Task.CompletedTask,
            () => { removed = true; return Task.CompletedTask; }));
        Assert.True(removed);
        Assert.Same(failure, observed);
    }

    [Fact]
    public void PhaseTimingIsImmediatelyReadableAndKeepsVariantsSeparateWithoutOriginalData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cohors-phase-{Guid.NewGuid():N}.jsonl");
        try
        {
            var first = new TestPhaseTiming("Comment", path);
            first.Record(CorrespondencePhase.CaseStarted);
            Assert.Single(File.ReadAllLines(path));
            first.Record(CorrespondencePhase.DossierBuilt);
            new TestPhaseTiming("InformationRequest", path).Record(CorrespondencePhase.CaseStarted);
            var rows = File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Assert.Equal(3, rows.Length);
            Assert.All(rows, row => Assert.Equal(
                ["elapsedTicks", "frequency", "phase", "sequence", "timestampTicks", "utc", "variant"],
                row.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
            Assert.Equal("Comment", rows[0].GetProperty("variant").GetString());
            Assert.Equal("DossierBuilt", rows[1].GetProperty("phase").GetString());
            Assert.Equal(2, rows[1].GetProperty("sequence").GetInt32());
            Assert.Equal("InformationRequest", rows[2].GetProperty("variant").GetString());
            Assert.Equal(1, rows[2].GetProperty("sequence").GetInt32());
            Assert.True(rows[1].GetProperty("timestampTicks").GetInt64() >= rows[0].GetProperty("timestampTicks").GetInt64());
            Assert.True(rows[1].GetProperty("elapsedTicks").GetInt64() >= rows[0].GetProperty("elapsedTicks").GetInt64());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PhaseTimingIoFailureRemainsVisible()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"cohors-phase-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(parent, "Not a directory.");
        try
        {
            var timing = new TestPhaseTiming("Comment", Path.Combine(parent, "phases.jsonl"));
            Assert.ThrowsAny<IOException>(() => timing.Record(CorrespondencePhase.CaseStarted));
        }
        finally { File.Delete(parent); }
    }
}
