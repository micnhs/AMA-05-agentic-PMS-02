using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed class RetentionObjectTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("cohors-raw-retention-");
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-11-16T15:00:00Z");
    public void Dispose() => directory.Delete(true);

    private RetentionRecord Authorized(RetentionObjectBinding binding)
    {
        var record = new RetentionRecord("raw-retention-test", 3, "ortho", binding, RetentionRules.PolicyVersion,
            "DEMO-RAW-OBJECT", now.AddDays(-8), now, true, false, "", "Deleting", null, 1, now, null, null);
        return record with { Authorization = new(new string('b', 64), "synthetic-quality", RetentionRules.Scope(record, null),
            now, "Explizite Loeschfreigabe fuer isolierte synthetische Testbytes.", true,
            "Isoliertes Testverzeichnis geprueft; keine Backups, Exporte, Caches oder Agentenkopien vorhanden.") };
    }

    [Fact]
    public async Task T18_ActualRemovalHasABoundIdempotentJournalAndRejectsResurrection()
    {
        const string text = "SYNTHETIC RAW RETENTION SAMPLE";
        var store = new LocalObjectStore(directory.FullName);
        var value = Authorized(await store.CreateSyntheticRaw(text));
        var receipt = await store.DeleteRaw(value, now);
        RetentionRules.RequireDeletionReceipt(value, receipt);
        var rawPath = Path.Combine(directory.FullName, "raw-quarantine", value.Object.Hash);
        Assert.False(File.Exists(rawPath));
        Assert.Equal(receipt, await store.DeleteRaw(value, now.AddMinutes(1)));
        Assert.Equal("object_retired", (await Assert.ThrowsAsync<DomainException>(() => store.Put(text))).Code);
        Assert.Equal("object_retired", (await Assert.ThrowsAsync<DomainException>(() => store.CreateSyntheticRaw(text))).Code);
        await File.WriteAllTextAsync(rawPath, text);
        Assert.Equal("retention_reappeared", (await Assert.ThrowsAsync<DomainException>(() => store.DeleteRaw(value, now))).Code);
        Assert.Equal(text, await File.ReadAllTextAsync(rawPath));
    }

    [Fact]
    public async Task T18_UnexpectedMissingBytesCannotBecomeSuccessfulDeletionWithoutAnIntentReceipt()
    {
        var store = new LocalObjectStore(directory.FullName);
        var value = Authorized(await store.CreateSyntheticRaw("SYNTHETIC MISSING RAW SAMPLE"));
        File.Delete(Path.Combine(directory.FullName, "raw-quarantine", value.Object.Hash));
        var failure = await Assert.ThrowsAsync<ObjectStoreIoException>(() => store.DeleteRaw(value, now));
        var cause = Assert.IsType<FileNotFoundException>(failure.InnerException);
        var detail = Assert.Single(failure.Failures);
        Assert.Equal(RawDeletionIoStep.VerifyRawBeforePreparation, detail.Step);
        Assert.Equal(nameof(FileNotFoundException), detail.ExceptionType);
        Assert.Equal($"0x{cause.HResult:X8}", detail.HResult);
        Assert.False(Directory.Exists(Path.Combine(directory.FullName, ".deletion-receipts")));
    }

    [Fact]
    public async Task T18_SharedBytesInTheEvidenceAreaBlockRawDeletion()
    {
        const string text = "SYNTHETIC SHARED RAW AND EVIDENCE SAMPLE";
        var store = new LocalObjectStore(directory.FullName);
        var value = Authorized(await store.CreateSyntheticRaw(text));
        var evidenceHash = await store.Put(text);
        Assert.Equal(value.Object.Hash, evidenceHash);
        Assert.Equal("retention_shared_store_bytes", (await Assert.ThrowsAsync<DomainException>(() => store.DeleteRaw(value, now))).Code);
        Assert.Equal(text, await store.Read(evidenceHash));
        Assert.Equal(text, await store.ReadRaw(value.Object));
    }

    [Fact]
    public async Task T18_AnotherAuthorizationCannotReuseACompletedObjectReceipt()
    {
        var store = new LocalObjectStore(directory.FullName);
        var value = Authorized(await store.CreateSyntheticRaw("SYNTHETIC OPERATION-BOUND RAW SAMPLE"));
        await store.DeleteRaw(value, now);
        var other = value with { Authorization = value.Authorization! with { Id = new string('c', 64) } };
        Assert.Equal("retention_deletion_receipt", (await Assert.ThrowsAsync<DomainException>(() => store.DeleteRaw(other, now))).Code);
    }
}
