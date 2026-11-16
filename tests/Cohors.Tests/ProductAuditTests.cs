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
public sealed class ProductAuditTests : IAsyncLifetime
{
    private readonly SqlFixture fixture = new() { SeedCases = false };
    private readonly TestClock clock = new(DateTimeOffset.Parse("2026-11-16T15:00:00Z"));
    private const string Reason = "Synthetischer produktbezogener Auditnachweis mit Originalversionen, Quellen und getrennten Berechtigungen; keine reale Annahme.";
    private static readonly Actor Auditor = new("audit-reader", "Synthetic auditor", [Roles.Auditor], ["ortho", "cardio"], true);
    private ProductAuditService Exporter() => new(fixture.Store, fixture.Objects, fixture.Policies, clock);
    private AnalyticsService Analytics() => new(fixture.Store, new AgentRunner(new(null, null, null, null)), fixture.Policies, clock);
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    [Fact]
    public async Task L03_AuditPreservesOriginalVersionBytesObjectsApprovalsDeadlinesAndTransportWithoutWriting()
    {
        var s = new CaseScenario(fixture);
        await s.Create(); await s.Assess(); await s.Prepare(); await s.Approve();
        s.Case = await s.Service.Submit(s.Case.Id, new(s.Case.Version, "Unknown", Reason), SqlFixture.Ra, CaseScenario.Key());
        var pending = await fixture.Store.PendingEvents(500);
        foreach (var entry in pending)
        {
            var owner = CaseScenario.Key();
            Assert.NotNull(await fixture.Store.ClaimEvent(entry.Id, owner));
            await fixture.Store.CompleteEvent(entry.Id, "audit-fixture", owner, null);
        }
        var before = Canonical.Serialize(await fixture.Store.Get<SafetyCase>("case", s.Case.Id));
        var auditBefore = Canonical.Serialize(await fixture.Store.Audit(s.Case.Id));
        var file = await Exporter().Export("ortho", Auditor with { Products = ["ortho"] });
        Assert.Equal("application/zip", file.ContentType);
        using var archive = new ZipArchive(new MemoryStream(file.Bytes), ZipArchiveMode.Read);
        VerifyChecksums(archive);
        using var manifest = ReadJson(archive, "manifest.json");
        Assert.Equal(ProductAuditRules.Contract, manifest.RootElement.GetProperty("contract").GetString());
        Assert.Equal("ortho", Assert.Single(manifest.RootElement.GetProperty("products").EnumerateArray()).GetString());
        var entries = manifest.RootElement.GetProperty("aggregates").EnumerateArray().ToArray();
        Assert.Contains(entries, e => e.GetProperty("kind").GetString() == "agentInvocation");
        Assert.DoesNotContain(entries, e => e.GetProperty("productId").GetString() == "cardio");
        var caseEntry = Assert.Single(entries, e => e.GetProperty("id").GetString() == s.Case.Id);
        await using var connection = await fixture.Store.Open();
        foreach (var version in caseEntry.GetProperty("versions").EnumerateArray())
        {
            using var query = SqlStateStore.Command(connection, null,
                "SELECT Json FROM dbo.AggregateVersions WHERE Kind='case' AND Id=@id AND Version=@version",
                ("id", s.Case.Id), ("version", version.GetProperty("version").GetInt32()));
            var original = (string)(await query.ExecuteScalarAsync())!;
            var bytes = Read(archive, version.GetProperty("path").GetString()!);
            Assert.Equal(Encoding.UTF8.GetBytes(original), bytes);
            Assert.Equal(version.GetProperty("hash").GetString(), Hash(bytes));
        }
        var current = Canonical.Read<SafetyCase>(Encoding.UTF8.GetString(Read(archive,
            caseEntry.GetProperty("versions").EnumerateArray().Last().GetProperty("path").GetString()!)));
        Assert.Equal(2, current.Approvals.Length);
        Assert.Equal(Canonical.Serialize(s.Case.CurrentReport), Canonical.Serialize(current.CurrentReport));
        Assert.Equal("TransmissionUnknown", current.CurrentReport!.State.ToString());
        Assert.Equal(Encoding.UTF8.GetBytes(s.Case.CurrentReport!.Mir!.Xml),
            Read(archive, "objects/" + s.Case.CurrentReport.Mir.XmlHash + ".txt"));
        using var deadlines = ReadJson(archive, "transport/case-deadlines.json");
        using var inbox = ReadJson(archive, "transport/inbox.json");
        Assert.Equal(s.Case.Deadlines.Length, deadlines.RootElement.GetArrayLength());
        Assert.Equal(pending.Count(e => Canonical.Read<JsonElement>(e.Payload).GetProperty("productId").GetString() == "ortho"),
            inbox.RootElement.GetArrayLength());
        using var verification = ReadJson(archive, "verification.json");
        Assert.StartsWith("Not performed", verification.RootElement.GetProperty("externalLedgerDigestVerification").GetString());
        Assert.StartsWith("Not performed", verification.RootElement.GetProperty("recovery").GetString());
        Assert.Equal(before, Canonical.Serialize(await fixture.Store.Get<SafetyCase>("case", s.Case.Id)));
        Assert.Equal(auditBefore, Canonical.Serialize(await fixture.Store.Audit(s.Case.Id)));
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            Exporter().Export("ortho", Auditor with { IsHuman = false }))).Status);
    }

    [Fact]
    public async Task L03_NonLeadingAndHistoricalMembershipIncludeBoundSourcesButNotUnrelatedFamilyRecords()
    {
        var analytics = Analytics();
        var input = new RecordPsurGroupCommand(0, "DEMO-PSUR-GROUP-AUDIT-" + CaseScenario.Key().ToUpperInvariant(),
            "ortho", ["ortho", "cardio"], new(2025, 11, 15), Reason, Reason);
        var group = await analytics.RecordGroup(input, SqlFixture.Pms, CaseScenario.Key());
        var report = await analytics.Freeze(new("ortho", "ortho", group.FirstPeriodStart,
            group.FirstPeriodStart.AddYears(1).AddDays(-1), "Worldwide", GroupId: group.Id, GroupVersion: group.Version),
            SqlFixture.Pms, CaseScenario.Key());
        group = await analytics.RecordGroup(input with { ExpectedVersion = group.Version, MemberProductIds = ["ortho"] },
            SqlFixture.Pms, CaseScenario.Key());
        var unrelated = await fixture.Service(clock).Register(new CaseScenario(fixture).Input,
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            Exporter().Export("cardio", Auditor with { Products = ["cardio"] }))).Status);
        var file = await Exporter().Export("cardio", Auditor);
        using var archive = new ZipArchive(new MemoryStream(file.Bytes), ZipArchiveMode.Read);
        VerifyChecksums(archive);
        using var manifest = ReadJson(archive, "manifest.json");
        var entries = manifest.RootElement.GetProperty("aggregates").EnumerateArray().ToArray();
        Assert.Contains(entries, e => e.GetProperty("id").GetString() == report.Id);
        Assert.Contains(entries, e => e.GetProperty("kind").GetString() == "agentInvocation" && e.GetProperty("productId").GetString() == "ortho");
        Assert.DoesNotContain(entries, e => e.GetProperty("id").GetString() == unrelated.Id);
        var definition = Assert.Single(entries, e => e.GetProperty("id").GetString() == group.Id);
        Assert.Equal(2, definition.GetProperty("versions").GetArrayLength());
        foreach (var aggregate in entries)
            foreach (var version in aggregate.GetProperty("versions").EnumerateArray())
                foreach (var reference in ProductAuditRules.Sources(Encoding.UTF8.GetString(Read(archive, version.GetProperty("path").GetString()!))))
                    Assert.Contains(entries, e => e.GetProperty("id").GetString() == reference.Id &&
                        e.GetProperty("versions").EnumerateArray().Any(v => v.GetProperty("version").GetInt32() == reference.Version &&
                            v.GetProperty("hash").GetString() == reference.Hash));
        Assert.Equal(Canonical.Hash(report), Canonical.Hash(await fixture.Store.Get<Psur>("psur", report.Id)));
    }

    [Fact]
    public async Task L03_ActualMissingAndManipulatedObjectBytesFailInsteadOfProducingAPartialArchive()
    {
        var document = (await fixture.Store.List<Evidence>("evidence", ["ortho"])).First();
        var path = Path.Combine(fixture.ObjectDirectory, document.Hash);
        var original = await File.ReadAllBytesAsync(path);
        try
        {
            File.Delete(path);
            var missing = await Assert.ThrowsAsync<DomainException>(() => Exporter().Export("ortho", Auditor));
            Assert.Equal("audit_object_unavailable", missing.Code);
            Assert.IsType<FileNotFoundException>(missing.InnerException);
            await File.WriteAllTextAsync(path, "SYNTHETIC TAMPERED OBJECT");
            Assert.Equal("object_integrity", (await Assert.ThrowsAsync<DomainException>(() => Exporter().Export("ortho", Auditor))).Code);
        }
        finally { await File.WriteAllBytesAsync(path, original); }
        var valid = await Exporter().Export("ortho", Auditor);
        using var archive = new ZipArchive(new MemoryStream(valid.Bytes), ZipArchiveMode.Read);
        Assert.Equal(original, Read(archive, "objects/" + document.Hash + ".txt"));
        using var inbox = ReadJson(archive, "transport/inbox.json");
        Assert.Equal(0, inbox.RootElement.GetArrayLength());
        using var outbox = ReadJson(archive, "transport/outbox.json");
        Assert.NotEmpty(outbox.RootElement.EnumerateArray());
        Assert.All(outbox.RootElement.EnumerateArray(),
            entry => Assert.Equal(JsonValueKind.Null, entry.GetProperty("CompletedAt").ValueKind));
    }

    [Fact]
    public async Task L03_DeletedRawMetadataIsReadableButAnIndependentSharedHashReferenceCannotBeExcluded()
    {
        var retention = new RetentionService(fixture.Store, fixture.Store, fixture.Objects, fixture.Policies, new(true), clock);
        async Task<RetentionRecord> Raw() => await retention.CreateRaw(new("ortho", "DEMO-RAW-AUDIT-" + CaseScenario.Key(),
            clock.Now.AddDays(-8), Reason), SqlFixture.Quality, CaseScenario.Key());
        var deleted = await Raw();
        deleted = await retention.Authorize(deleted.Id, new(deleted.Version, true,
            "Nur dieser isolierte synthetische Testbereich: keine ungeloesten externen Export-, Backup-, Cache- oder Agentenkopien.", Reason),
            SqlFixture.Quality, CaseScenario.Key());
        deleted = await retention.Delete(deleted.Id, new(deleted.Version, Reason), SqlFixture.Quality, CaseScenario.Key());
        await Assert.ThrowsAsync<FileNotFoundException>(() => ((IRawQuarantineStore)fixture.Objects).ReadRaw(deleted.Object));
        var retained = await Raw();
        var text = await ((IRawQuarantineStore)fixture.Objects).ReadRaw(retained.Object);
        var file = await Exporter().Export("ortho", Auditor);
        using (var archive = new ZipArchive(new MemoryStream(file.Bytes), ZipArchiveMode.Read))
        {
            Assert.Null(archive.GetEntry("objects/" + deleted.Object.Hash + ".txt"));
            Assert.Null(archive.GetEntry("objects/" + retained.Object.Hash + ".txt"));
            using var verification = ReadJson(archive, "verification.json");
            Assert.Contains(verification.RootElement.GetProperty("exclusions").EnumerateArray(),
                e => e.GetProperty("source").GetString() == $"retention:{deleted.Id}@{deleted.Version}");
        }
        var template = (await fixture.Store.List<Evidence>("evidence", ["ortho"])).First();
        var evidence = template with { Id = "audit-independent-evidence", Version = 1, Content = text,
            Hash = retained.Object.Hash, ObjectKey = retained.Object.Hash };
        await fixture.Store.Execute<Evidence>(new(evidence.Id, "evidence", "ortho", 0, CaseScenario.Key(),
            "AuditMissingIndependentObjectFixture", Reason, SqlFixture.Quality, evidence, clock.Now), _ => evidence);
        Assert.Equal("audit_object_unavailable", (await Assert.ThrowsAsync<DomainException>(() =>
            Exporter().Export("ortho", Auditor))).Code);
        await fixture.Objects.Put(text);
        file = await Exporter().Export("ortho", Auditor);
        using var corrected = new ZipArchive(new MemoryStream(file.Bytes), ZipArchiveMode.Read);
        Assert.Equal(Encoding.UTF8.GetBytes(text), Read(corrected, "objects/" + evidence.Hash + ".txt"));
        Assert.Null(corrected.GetEntry("objects/" + deleted.Object.Hash + ".txt"));
    }

    [Theory]
    [InlineData("missing-version", "audit_history")]
    [InlineData("version-hash", "history_tampered")]
    [InlineData("version-time", "history_tampered")]
    [InlineData("outbox", "audit_outbox")]
    [InlineData("receipt", "audit_receipt")]
    [InlineData("deadline", "audit_deadline")]
    [InlineData("source", "audit_source")]
    public async Task L03_PayloadChainAloneCannotHideMissingVersionsSourcesOrChangedOperationalBindings(string fault, string code)
    {
        var s = new CaseScenario(fixture);
        await s.Create();
        if (fault == "source")
        {
            var exposure = (await fixture.Store.List<Exposure>("exposure", ["ortho"])).First();
            var input = new TrendInput("ortho", exposure.Region, exposure.Start, exposure.End, [s.Case.Id], exposure,
                0.001m, "synthetic-audit-method", [new(s.Case.Id, 999, new string('0', 64))]);
            var trend = new TrendRun("audit-bad-source", 1, "ortho", input, new(1, exposure.Count, exposure.Unit,
                1, 1, 0.1, false, []), clock.Now, "Draft", Reason, null, null, Canonical.Hash(input));
            await fixture.Store.Execute<TrendRun>(new(trend.Id, "trend", "ortho", 0, CaseScenario.Key(),
                "AuditUnboundSourceFixture", Reason, SqlFixture.Pms, trend, clock.Now), _ => trend);
        }
        else
        {
            var sql = fault switch
            {
                "missing-version" => "DELETE dbo.AggregateVersions WHERE Kind='case' AND Id=@id",
                "version-hash" => "UPDATE dbo.AggregateVersions SET Hash=REPLICATE('0',64) WHERE Kind='case' AND Id=@id",
                "version-time" => "UPDATE dbo.AggregateVersions SET At=DATEADD(minute,1,At) WHERE Kind='case' AND Id=@id",
                "outbox" => "UPDATE dbo.Outbox SET Type='ChangedAuditEvent' WHERE AggregateKind='case' AND AggregateId=@id",
                "receipt" => "UPDATE dbo.Outbox SET CompletedAt=SYSUTCDATETIME() WHERE AggregateKind='case' AND AggregateId=@id",
                "deadline" => "UPDATE dbo.Deadlines SET SafetyDue=DATEADD(day,1,SafetyDue) WHERE CaseId=@id",
                _ => throw new InvalidOperationException("Unknown audit fault fixture.")
            };
            await using var connection = await fixture.Store.Open();
            using var change = SqlStateStore.Command(connection, null, sql, ("id", s.Case.Id));
            await change.ExecuteNonQueryAsync();
        }
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() => Exporter().Export("ortho", Auditor))).Code);
    }

    private static void VerifyChecksums(ZipArchive archive)
    {
        using var checksums = ReadJson(archive, "checksums.json");
        Assert.Equal(archive.Entries.Count - 1, checksums.RootElement.EnumerateObject().Count());
        foreach (var checksum in checksums.RootElement.EnumerateObject())
            Assert.Equal(checksum.Value.GetString(), Hash(Read(archive, checksum.Name)));
    }

    private static byte[] Read(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        using var stream = entry!.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static JsonDocument ReadJson(ZipArchive archive, string name) => JsonDocument.Parse(Read(archive, name));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
