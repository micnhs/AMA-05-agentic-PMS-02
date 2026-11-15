using Cohors.Agents;
using Cohors.Application;
using Cohors.Domain;
using Microsoft.Data.SqlClient;

namespace Cohors.Tests;

[Collection("SQL pilot")]
[Trait("Category", "Sql")]
public sealed class RetentionTests(Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private readonly SqlFixture fixture = new() { SeedCases = false };
    private readonly TestClock clock = new(DateTimeOffset.Parse("2026-11-16T15:00:00Z"));
    private const string Reason = "Explizite menschliche Pruefung des isolierten synthetischen Retention-Testprofils; keine reale Loeschfreigabe.";
    private const string CopyReview = "Der isolierte Testbereich enthaelt keine ungeloesten Export-, Backup-, Cache- oder Agentenkopien; nur die benannten Testobjekte.";
    private RetentionService Service(IObjectStore? objects = null, bool enabled = true) =>
        new(fixture.Store, fixture.Store, objects ?? fixture.Objects, fixture.Policies, new(enabled), clock);
    private IRawQuarantineStore Raw => (IRawQuarantineStore)fixture.Objects;
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();
    private Task<RetentionRecord> RawItem(RetentionService service) => service.CreateRaw(
        new("ortho", "DEMO-RAW-SQL-" + Guid.NewGuid().ToString("N"), clock.Now.AddDays(-7), Reason),
        SqlFixture.Quality, CaseScenario.Key());
    private Task<RetentionRecord> Authorize(RetentionService service, RetentionRecord value) =>
        service.Authorize(value.Id, new(value.Version, true, CopyReview, Reason), SqlFixture.Quality, CaseScenario.Key());

    private async Task Projection(RetentionRecord value)
    {
        var text = await Raw.ReadRaw(value.Object);
        await using var connection = await fixture.Store.Open();
        using var command = new SqlCommand("""
            INSERT dbo.SearchProjection(Id,ProductId,Version,Content,IndexedAt,Hash)
            VALUES(@id,'ortho',1,@content,@at,@hash);
            """, connection);
        command.Parameters.AddWithValue("@id", value.Id);
        command.Parameters.AddWithValue("@content", text);
        command.Parameters.AddWithValue("@at", clock.Now);
        command.Parameters.AddWithValue("@hash", value.Object.Hash);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> Projected(RetentionRecord value)
    {
        await using var connection = await fixture.Store.Open();
        using var command = new SqlCommand("SELECT COUNT(*) FROM dbo.SearchProjection WHERE Hash=@hash", connection);
        command.Parameters.AddWithValue("@hash", value.Object.Hash);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task T18_RolesScopeHoldAndAuthorizationPrecedeActualRemovalProjectionPurgeAndAtomicAudit()
    {
        var service = Service();
        var value = await RawItem(service);
        Assert.True((await service.Evaluate(value.Id, SqlFixture.Quality)).Evaluation.MinimumIntervalElapsed);
        Assert.Equal("retention_blocked", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Delete(value.Id, new(value.Version, Reason), SqlFixture.Quality, CaseScenario.Key()))).Code);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.Authorize(value.Id, new(value.Version, true, CopyReview, Reason), SqlFixture.Pms, CaseScenario.Key()))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.Get(value.Id, SqlFixture.Quality with { Products = ["cardio"] }))).Status);
        Assert.Equal("retention_profile_disabled", (await Assert.ThrowsAsync<DomainException>(() =>
            Service(enabled: false).Delete(value.Id, new(value.Version, Reason), SqlFixture.Quality, CaseScenario.Key()))).Code);
        value = await service.Hold(value.Id, new(value.Version, true, Reason), SqlFixture.Quality, CaseScenario.Key());
        Assert.Contains("legal_hold", (await service.Evaluate(value.Id, SqlFixture.Quality)).Evaluation.Blockers);
        await Assert.ThrowsAsync<DomainException>(() => Authorize(service, value));
        value = await service.Hold(value.Id, new(value.Version, false, Reason), SqlFixture.Quality, CaseScenario.Key());
        Assert.Null(value.Authorization);
        await Projection(value);
        value = await Authorize(service, value);
        var key = new string('k', 120);
        var command = new VersionCommand(value.Version, Reason);
        var deleted = await service.Delete(value.Id, command, SqlFixture.Quality, key);
        Assert.Equal("Deleted", deleted.Status);
        Assert.Equal(value.Authorization!.Id, deleted.Deletion!.OperationId);
        Assert.Equal(0, await Projected(value));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Raw.ReadRaw(value.Object));
        var audit = await fixture.Store.Audit(value.Id);
        Assert.Equal(deleted.Version, audit.Length);
        Assert.Single(audit, a => a.Action == "RawDeletionCompleted");
        Assert.DoesNotContain("No patient, medical finding", Canonical.Serialize(audit));
        var repeated = await service.Delete(value.Id, command, SqlFixture.Quality, key);
        Assert.Equal(Canonical.Hash(deleted), Canonical.Hash(repeated));
        Assert.Equal(audit.Length, (await fixture.Store.Audit(value.Id)).Length);
    }

    [Fact]
    public async Task T18_OldEvidenceAndFrozenSnapshotSharedHashRemainDeletionBlockersAfterTheCurrentVersionChanges()
    {
        var service = Service();
        var value = await RawItem(service);
        var text = await Raw.ReadRaw(value.Object);
        var hash = await fixture.Objects.Put(text);
        var template = (await fixture.Store.List<Evidence>("evidence", ["ortho"])).First(e => e.Approved && e.Synthetic);
        var evidence = template with { Id = "shared-retention-evidence", Version = 1, Content = text, Hash = hash,
            ObjectKey = hash, RecordedAt = clock.Now, ValidFrom = clock.Now.AddDays(-1) };
        await fixture.Store.Execute<Evidence>(new(evidence.Id, "evidence", "ortho", 0, CaseScenario.Key(),
            "RetentionReferenceFixture", Reason, SqlFixture.Quality, evidence, clock.Now), _ => evidence);
        var analytics = new AnalyticsService(fixture.Store, new AgentRunner(new(null, null, null, null)), fixture.Policies, clock);
        var report = await analytics.Freeze(new("ortho", "ortho", new(2025, 11, 15), new(2026, 11, 14), "Worldwide"),
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Contains(report.Snapshot.Documents, e => e.Hash == hash && e.Id == evidence.Id);
        var snapshot = Canonical.Serialize(report.Snapshot);
        const string revisedContent = "SYNTHETIC REVISED EVIDENCE, OLD SOURCE VERSION REMAINS BOUND";
        var newHash = await fixture.Objects.Put(revisedContent);
        var revision = evidence with { Version = 2, Content = revisedContent, Hash = newHash, ObjectKey = newHash };
        await fixture.Store.Execute<Evidence>(new(evidence.Id, "evidence", "ortho", 1, CaseScenario.Key(),
            "RetentionReferenceRevisionFixture", Reason, SqlFixture.Quality, revision, clock.Now), _ => revision);
        var view = await service.Evaluate(value.Id, SqlFixture.Quality);
        Assert.Contains(view.References, r => r.Kind == "evidence" && r.Id == evidence.Id && r.Version == 1);
        Assert.Contains(view.References, r => r.Kind == "psur" && r.Id == report.Id);
        Assert.DoesNotContain(view.References, r => r.Kind == "evidence" && r.Id == evidence.Id && r.Version == 2);
        Assert.False(view.Evaluation.EligibleForAuthorization);
        Assert.Equal("retention_blocked", (await Assert.ThrowsAsync<DomainException>(() => Authorize(service, value))).Code);
        Assert.Equal(text, await Raw.ReadRaw(value.Object));
        Assert.Equal(snapshot, Canonical.Serialize((await analytics.GetPsur(report.Id, SqlFixture.Pms)).Snapshot));
    }

    [Fact]
    public async Task T18_CrossStoreFailureIsNotCompletedAndReconcilesTheSameAuthorizedPhysicalRemoval()
    {
        var service = Service();
        var value = await RawItem(service);
        await Projection(value);
        value = await Authorize(service, value);
        await using var connection = await fixture.Store.Open();
        using (var create = new SqlCommand("""
            CREATE TRIGGER dbo.CohorsRetentionFailure ON dbo.SearchProjection INSTEAD OF DELETE
            AS BEGIN THROW 51023,'Synthetic retention projection failure',1; END;
            """, connection)) await create.ExecuteNonQueryAsync();
        var failure = await Assert.ThrowsAsync<DomainException>(() =>
            service.Delete(value.Id, new(value.Version, Reason), SqlFixture.Quality, CaseScenario.Key()));
        Assert.Equal("retention_incomplete", failure.Code);
        Assert.IsType<SqlException>(failure.InnerException);
        var failed = await service.Get(value.Id, SqlFixture.Quality);
        Assert.Equal("DeletionFailed", failed.Status);
        Assert.Null(failed.Deletion);
        Assert.Equal("retention_sql", failed.ErrorCode);
        await Assert.ThrowsAsync<FileNotFoundException>(() => Raw.ReadRaw(value.Object));
        Assert.Equal(1, await Projected(value));
        using (var drop = new SqlCommand("DROP TRIGGER dbo.CohorsRetentionFailure", connection)) await drop.ExecuteNonQueryAsync();
        var reconciled = await service.Delete(value.Id, new(failed.Version, Reason), SqlFixture.Quality, CaseScenario.Key());
        Assert.Equal("Deleted", reconciled.Status);
        Assert.Equal(2, reconciled.Attempts);
        Assert.Equal(value.Authorization!.Id, reconciled.Deletion!.OperationId);
        Assert.Equal(0, await Projected(value));
        var audit = await fixture.Store.Audit(value.Id);
        Assert.Single(audit, a => a.Action == "RawDeletionFailed");
        Assert.Single(audit, a => a.Action == "RawDeletionCompleted");
    }

    [Fact]
    public async Task T18_AnInFlightDeletionCannotAcquireANewReferenceFromAnotherSqlWriter()
    {
        var gated = new GatedRawStore(fixture.Objects, output);
        var service = Service(gated);
        var value = await Authorize(service, await RawItem(service));
        var deletion = service.Delete(value.Id, new(value.Version, Reason), SqlFixture.Quality, CaseScenario.Key());
        try
        {
            await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var newReference = new LateReference("late-reference", 1, "ortho", value.Object.Hash);
            var writer = fixture.Store.Execute<LateReference>(new(newReference.Id, "referenceFixture", "ortho", 0,
                CaseScenario.Key(), "LateReferenceFixture", Reason, SqlFixture.Quality, newReference, clock.Now), _ => newReference);
            await Task.Delay(100);
            Assert.False(writer.IsCompleted);
            gated.Release.TrySetResult();
            Assert.Equal("Deleted", (await deletion).Status);
            Assert.Equal("retention_rebinding", (await Assert.ThrowsAsync<DomainException>(() => writer)).Code);
            Assert.Null(await fixture.Store.Get<LateReference>("referenceFixture", newReference.Id));
        }
        finally
        {
            gated.Release.TrySetResult();
            await deletion;
        }
    }

    [Fact]
    public async Task T18_ConfirmedLifecycleChangesTheMinimumButNeverAuthorizesRegulatoryEvidenceDeletion()
    {
        var service = Service();
        var evidence = (await fixture.Store.List<Evidence>("evidence", ["ortho"])).First(e => e.Synthetic && e.Approved);
        var value = await service.Track(new(evidence.Id, evidence.Version, Reason), SqlFixture.Quality, CaseScenario.Key());
        Assert.Null((await service.Evaluate(value.Id, SqlFixture.Quality)).Evaluation.MinimumIntervalElapsed);
        var lifecycle = await service.RecordLifecycle(new(0, "ortho", new(2020, 6, 30), true,
            "DEMO-CONFIRMED-LAST-PLACING", Reason), SqlFixture.Quality, CaseScenario.Key());
        clock.Now = DateTimeOffset.Parse("2035-06-30T12:00:00+01:00");
        var onDate = (await service.Evaluate(value.Id, SqlFixture.Quality)).Evaluation;
        Assert.Equal(new DateOnly(2035, 6, 30), onDate.MinimumRetainThroughLocalDate);
        Assert.False(onDate.MinimumIntervalElapsed);
        Assert.Equal(Canonical.Hash(lifecycle), onDate.LifecycleSource!.Hash);
        clock.Now = clock.Now.AddDays(1);
        Assert.True((await service.Evaluate(value.Id, SqlFixture.Quality)).Evaluation.MinimumIntervalElapsed);
        await Assert.ThrowsAsync<DomainException>(() => Authorize(service, value));
        Assert.Equal(evidence.Content, await fixture.Objects.Read(evidence.ObjectKey));
    }

    private sealed record LateReference(string Id, int Version, string ProductId, string ObjectHash);
    private sealed class GatedRawStore(IObjectStore inner, Xunit.Abstractions.ITestOutputHelper output) : IObjectStore, IRawQuarantineStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> Put(string content, CancellationToken ct = default) => inner.Put(content, ct);
        public Task<string> Read(string hash, CancellationToken ct = default) => inner.Read(hash, ct);
        public Task<RetentionObjectBinding> CreateSyntheticRaw(string content, CancellationToken ct = default) =>
            ((IRawQuarantineStore)inner).CreateSyntheticRaw(content, ct);
        public Task<string> ReadRaw(RetentionObjectBinding value, CancellationToken ct = default) =>
            ((IRawQuarantineStore)inner).ReadRaw(value, ct);
        public async Task<ObjectDeletionReceipt> DeleteRaw(RetentionRecord value, DateTimeOffset at, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            try { return await ((IRawQuarantineStore)inner).DeleteRaw(value, at, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var frame = new System.Diagnostics.StackTrace(ex, true).GetFrames()
                    .FirstOrDefault(f => Path.GetFileName(f.GetFileName()) == "ObjectStores.cs");
                output.WriteLine($"retention_storage_failure: type={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; operation=DeleteRaw; step=ObjectStores.cs:{frame?.GetFileLineNumber()}");
                throw;
            }
        }
    }
}
