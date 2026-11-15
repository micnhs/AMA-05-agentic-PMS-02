using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
public sealed class RecoveryTests(RecoveryFixture fixture)
{
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
            new AgentRunner(new(null, null, null, null)), search, clock);
        var regulatory = new RegulatoryService(sql, objects, service, new MirComposer(), fixture.Policies, clock);

        Assert.Equal(fixture.SourceTables, await RecoveryFixture.Tables(target.Connection));
        foreach (var item in fixture.SourceBytes)
            Assert.Equal(item.Value, await File.ReadAllBytesAsync(Path.Combine(target.ObjectRoot, item.Key)));
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
        fixture.Passed("positive: source unavailable; native SQL, all object bytes, historical/current retrieval, frozen MIR, deadlines, unknown and idempotent outbox");
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

public sealed class RecoveryFixture : IAsyncLifetime
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
    private bool sourceRemoved;
    public string Directory { get; private set; } = "";
    public string BackupDirectory => Path.Combine(Directory, "backup");
    public string ManifestHash { get; private set; } = "";
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
            sourceContainer = await StartSql("source", sourcePort);
            var sourceConnection = Connection(sourcePort, sourceContainer.Password, "master");
            Environment.SetEnvironmentVariable("COHORS_TEST_CONNECTION", sourceConnection);
            var source = new SqlFixture { SeedCases = false };
            try
            {
                await source.InitializeAsync();
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
                foreach (var file in System.IO.Directory.EnumerateFiles(sourceObjects))
                    SourceBytes.Add(Path.GetFileName(file), await File.ReadAllBytesAsync(file));
                SourceTables = await Tables(source.ConnectionString);
                var config = await WriteConfig(source.ConnectionString, sourceObjects, sourceContainer, "source");
                SqlConnection.ClearAllPools();
                await Execute(sourceConnection, $"ALTER DATABASE [{sourceDatabase}] SET READ_ONLY WITH NO_WAIT");
                var backup = await Script("Backup-Local.ps1", config, BackupDirectory, "-Quiesced");
                RequireSuccess(backup, "native backup");
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

    public async Task Restore(RecoveryTarget target)
    {
        var result = await RestoreResult(target, BackupDirectory, ManifestHash);
        RequireSuccess(result, "isolated restore");
        target.OwnsDatabase = true;
        Assert.True(File.Exists(target.ReadyPath));
    }

    public Task<RecoveryProcess> RestoreResult(RecoveryTarget target, string backup, string digest) =>
        Script("Restore-Local.ps1", target.ConfigPath, backup, "-ManifestSha256", digest);

    public Task<RecoveryProcess> Script(string name, string configuration, string backup, params string[] extra) =>
        Run("recovery-script", Environment.GetEnvironmentVariable("COHORS_RECOVERY_PWSH") ?? "pwsh",
            ["-NoProfile", "-File", Path.Combine(home, "scripts", name), "-ConfigurationPath", configuration,
                "-BackupDirectory", backup, "-ApplicationDirectory", applicationDirectory, .. extra]);

    public string CopyBackup(string label)
    {
        var destination = Path.Combine(Directory, label + "-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path.Combine(destination, "objects"));
        foreach (var file in System.IO.Directory.EnumerateFiles(BackupDirectory))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (var file in System.IO.Directory.EnumerateFiles(Path.Combine(BackupDirectory, "objects")))
            File.Copy(file, Path.Combine(destination, "objects", Path.GetFileName(file)), overwrite: false);
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
            if (checks.Count == 8)
            {
                var evidence = new
                {
                    scope = "Local native SQL/object recovery; direct existing application services, not HTTP/browser or Azure",
                    sourceDatabaseRemoved = sourceRemoved, sourceObjectsRemoved = !System.IO.Directory.Exists(sourceObjects),
                    sourceContainerStoppedBeforeTarget = true, separateSqlServers = sourcePort != targetPort,
                    manifestSha256 = ManifestHash, objectCount = SourceBytes.Count,
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

    private async Task<RecoveryProcess> Run(string step, string executable, string[] arguments, Dictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = home, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var item in environment) start.Environment[item.Key] = item.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Recovery process could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(8)); }
        catch (TimeoutException)
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            throw new InvalidOperationException("Recovery step timed out; its exact process tree was stopped.");
        }
        var output = await stdout + await stderr;
        await File.WriteAllTextAsync(Path.Combine(Directory, "private", $"{++logNumber:D3}-{step}.log"), output);
        var codes = Regex.Matches(output, @"recovery_[a-z_]+").Select(m => m.Value).Distinct().ToArray();
        return new(process.ExitCode, output, codes);
    }

    private static void RequireSuccess(RecoveryProcess result, string step)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Recovery {step} failed (exit {result.ExitCode}; {string.Join(",", result.Codes)}). Raw logs remain private.");
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
        foreach (var (table, order) in new[] { ("Aggregates", "Kind,Id"), ("AggregateVersions", "Kind,Id,Version"),
            ("AuditEvents", "Sequence"), ("Commands", "IdempotencyKey"), ("Deadlines", "Id"), ("Inbox", "EventId,Consumer"),
            ("Outbox", "Id"), ("SchemaVersions", "Version") })
            tables.Add(table, (string)(await Scalar(connection, $"SELECT (SELECT * FROM dbo.[{table}] ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES)"))!);
        return tables;
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

public sealed record RecoveryProcess(int ExitCode, string Output, string[] Codes);

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
