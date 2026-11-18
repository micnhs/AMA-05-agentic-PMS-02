using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Cohors.Api;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

[Collection("Local recovery")]
[Trait("Category", "Sql")]
[Trait("Category", "RecoveryEudamed")]
public sealed class EudamedRecoveryTests : IDisposable
{
    private readonly RecoveryFixture fixture;
    public EudamedRecoveryTests(RecoveryFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
    {
        this.fixture = fixture;
        fixture.DiagnosticOutput = output;
    }
    public void Dispose() => fixture.DiagnosticOutput = null;

    [Fact]
    public async Task OriginalBytesHistoricalApprovalTransportAndInboxSurviveIsolatedRestore()
    {
        await using var target = await fixture.NewTarget();
        await fixture.Restore(target);
        await fixture.RequireSourceUnavailable();
        var store = new SqlStateStore(target.Connection);
        var objects = new LocalObjectStore(target.ObjectRoot);
        var service = EudamedRecoveryData.Service(store, objects, fixture.Policies);
        var data = fixture.Eudamed;
        Assert.Equal(fixture.SourceTables, await RecoveryFixture.Tables(target.Connection));
        Assert.Equal(data.History, await EudamedRecoveryData.Histories(store));
        foreach (var hash in data.Originals.Values.Distinct(StringComparer.Ordinal))
        {
            var bytes = await objects.ReadBytes(hash);
            Assert.Equal(fixture.SourceBytes[hash], bytes);
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        Assert.True((await objects.ReadBytes(data.Originals["historical-pdf"])).AsSpan().StartsWith("%PDF-"u8));
        Assert.True((await objects.ReadBytes(data.Document.OriginalHash)).AsSpan().StartsWith("%PDF-"u8));
        Assert.Equal(data.Document.Length, (await objects.ReadBytes(data.Document.OriginalHash)).Length);
        var current = await service.DossierView(data.Current.Id, SqlFixture.Ra);
        Assert.Equal(Canonical.Serialize(data.Current), Canonical.Serialize(current.Dossier));
        Assert.Equal(Canonical.Serialize(data.Ledger), Canonical.Serialize(current.Ledger));
        var approved = Canonical.Read<EudamedDossier>(await objects.Read(data.Originals["approved-snapshot"]));
        EudamedRules.RequireFrozen(approved);
        Assert.Equal(Canonical.Serialize(data.Approved), Canonical.Serialize(approved));
        Assert.Equal(Canonical.Serialize(data.Approved.Package), await objects.Read(data.Approved.FrozenPackageHash!));
        Assert.Equal(Canonical.Serialize(data.Current.Package), await objects.Read(data.Current.FrozenPackageHash!));
        Assert.NotEqual(data.Approved.Revision, current.Dossier.Revision);
        Assert.NotEqual(data.Approved.ScopeHash, current.Dossier.ScopeHash);
        Assert.Equal("BusinessAccepted", Assert.Single(current.Ledger.Entries).BusinessStatus);
        Assert.Single(current.Dossier.Correspondence, c => c.Status == "Open" && c.Kind == "InformationRequest");
        foreach (var original in data.Inbox)
        {
            var actual = await store.Get<EudamedInbox>("eudamedInbox", original.Id);
            Assert.Equal(Canonical.Serialize(original), Canonical.Serialize(actual));
            Assert.Equal(Canonical.Serialize(original.Original), await objects.Read(original.OriginalHash));
        }
        var history = await service.DossierHistory(data.Current.Id, SqlFixture.Ra);
        Assert.Contains(history, item => Canonical.Hash(item) == Canonical.Hash(data.Approved));
        Assert.Equal(fixture.SourceTables, await RecoveryFixture.Tables(target.Connection));
        await fixture.RequireSentinel();
    }

    [Theory]
    [InlineData("before-backup")]
    [InlineData("before-restore")]
    public async Task MissingReferencedEudamedOriginalNeverProducesOrActivatesAnIncompleteCut(string boundary)
    {
        if (boundary == "before-backup")
        {
            await using var source = await fixture.NewTarget();
            await fixture.Restore(source);
            await fixture.RequireSourceUnavailable();
            await fixture.ExecuteMaster($"ALTER DATABASE [{source.Database}] SET READ_ONLY WITH NO_WAIT");
            try
            {
                foreach (var hash in fixture.Eudamed.Originals.Values.Distinct(StringComparer.Ordinal))
                {
                    var path = Path.Combine(source.ObjectRoot, hash);
                    var bytes = await File.ReadAllBytesAsync(path);
                    var backup = Path.Combine(fixture.Directory, "eudamed-rejected-" + Guid.NewGuid().ToString("N"));
                    File.Delete(path);
                    try
                    {
                        var result = await fixture.Script("Backup-Local.ps1", source.ConfigPath, backup, "-Quiesced");
                        Assert.NotEqual(0, result.ExitCode);
                        Assert.Contains("recovery_reference_coverage", result.Codes);
                        Assert.False(Directory.Exists(backup));
                    }
                    finally { await File.WriteAllBytesAsync(path, bytes); }
                    Assert.Equal(hash, RecoveryFixture.FileHash(path));
                    Assert.Equal(fixture.SourceTables, await RecoveryFixture.Tables(source.Connection));
                    await fixture.RequireSentinel();
                }
            }
            finally { await fixture.ExecuteMaster($"ALTER DATABASE [{source.Database}] SET READ_WRITE WITH NO_WAIT"); }
        }
        else
        {
            foreach (var hash in fixture.Eudamed.Originals.Values.Distinct(StringComparer.Ordinal))
            {
                await using var target = await fixture.NewTarget();
                var copy = fixture.CopyBackup("eudamed-missing");
                File.Delete(Path.Combine(copy, "objects", hash));
                var path = Path.Combine(copy, "manifest.json");
                var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
                // A plausible but incomplete inventory must still fail against restored authoritative SQL.
                manifest["objects"] = new JsonArray(manifest["objects"]!.AsArray()
                    .Where(item => item!["hash"]!.GetValue<string>() != hash).Select(item => item!.DeepClone()).ToArray());
                manifest["references"] = new JsonArray(manifest["references"]!.AsArray()
                    .Where(item => item!.GetValue<string>() != hash).Select(item => item!.DeepClone()).ToArray());
                await File.WriteAllTextAsync(path, manifest.ToJsonString());
                var result = await fixture.RestoreResult(target, copy, RecoveryFixture.FileHash(path));
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("recovery_reference_coverage", result.Codes);
                await fixture.RequireNoTarget(target);
                await fixture.RequireSentinel();
            }
        }
        Assert.Equal(fixture.ManifestHash, RecoveryFixture.FileHash(Path.Combine(fixture.BackupDirectory, "manifest.json")));
    }
}

public sealed partial class RecoveryFixture
{
    internal EudamedRecoveryData Eudamed { get; private set; } = null!;
}

internal sealed record EudamedRecoveryData(EudamedDocument Document, EudamedDossier Approved,
    EudamedDossier Current, EudamedDeliveryLedger Ledger, EudamedInbox[] Inbox,
    SortedDictionary<string, string> History, Dictionary<string, string> Originals)
{
    private const string Reason = "SYNTHETISCH: Der isolierte Wiederherstellungsschnitt erhaelt Originalbytes, historische Quellen und getrennte menschliche Freigaben. Keine reale Patientenhandlung, externe Uebermittlung oder implizite Sicherheitsfreigabe.";

    internal static EudamedService Service(SqlStateStore store, IObjectStore objects, PolicyCatalog policies,
        TimeProvider? clock = null) => new(store, objects, policies, new NoNetwork(), new EudamedComposer(),
            new PsurExportService(store, clock ?? new TestClock(DateTimeOffset.Parse("2026-11-16T15:00:00Z"))),
            clock ?? new TestClock(DateTimeOffset.Parse("2026-11-16T15:00:00Z")));

    internal static async Task<EudamedRecoveryData> Prepare(SqlFixture source)
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-16T15:00:00Z"));
        var service = Service(source.Store, source.Objects, source.Policies, clock);
        var actor = await service.SaveActor(null, new(0, "ortho", new("DEMO recovery manufacturer", "MF", "IE",
            "SYNTHETISCH: Local recovery address only", new("Demo", "Recovery", "recovery@example.invalid", "000000"), true),
            Reason), SqlFixture.Pms, CaseScenario.Key());
        actor = await service.SubmitActor(actor.Id, new(actor.Version, "Approve", Reason), SqlFixture.Ra, CaseScenario.Key());
        actor = await service.ProcessActor(actor.Id, actor.Version);
        var governance = (await source.Store.Get<ProductGovernance>("productGovernance", "ortho"))!;
        var references = await service.MapReferences(new(0, "ortho", actor.Id, actor.Version, governance.Version,
            "recovery-1", new(2025, 1, 1), new(2040, 12, 31), Reason, "DEMO-NB-RECOVERY", "GS1", Reason),
            SqlFixture.Ra, CaseScenario.Key());
        var profile = new EudamedProfile(EudamedMode.Simulator, EudamedEnvironment.Simulation, actor.Id, actor.Version,
            references.Id, references.Version, "DEMO-CA-IE", EudamedContracts.Business, EudamedContracts.Gateway, null);
        var configuration = await service.SaveConfiguration(new(0, "ortho", profile, Reason), SqlFixture.Ra, CaseScenario.Key());
        await service.Activate("ortho", new(0, configuration.Version, true, Reason), SqlFixture.Ra, CaseScenario.Key());
        var scenario = new CaseScenario(source);
        await scenario.Create();
        var capa = await new QualityService(source.Store, source.Objects, clock).OpenCapa(new(scenario.Case.Id, "ortho",
            "SYNTHETISCH: Isolierte Recovery-CAPA", Reason, Reason, SqlFixture.Quality.Id, Reason, clock.Now.AddDays(10)),
            SqlFixture.Quality, CaseScenario.Key());
        var content = new EudamedDossierContent("SYNTHETISCH: Historisches Recovery-PDF", Reason, Reason, "de", "EU-IE",
            ["IE"], "Submit", null, null, null, new(["DEMO-BATCH"], [], "Synthetische Feldkorrektur", Reason, Reason,
                "Synthetic quality role", Reason, clock.Now.AddDays(10), [capa.Id], new(2026, 11, 16), Reason, Reason,
                "NO_INV_ONGOING", Reason, ["CUSTOMER_INFORMATION_ONLY"], ["IDENTIFY_DEVICE"], Reason), null);
        var input = new DraftEudamedDossierCommand(0, "ortho", EudamedDossierKind.FSCA, content,
            [new("capa", capa.Id, capa.Version)], Reason);
        var first = await service.DraftDossier(null, input, SqlFixture.Pms, CaseScenario.Key());
        first = await Approve(service, first);
        var view = await service.DossierView(first.Id, SqlFixture.Ra);
        view = await service.QueueDossier(first.Id, new(first.Version, view.Ledger.Version, "InformationRequest", Reason),
            SqlFixture.Ra, CaseScenario.Key());
        await service.ProcessDelivery(view.Ledger.Id, Assert.Single(view.Ledger.Entries).Attempt.Id);
        view = await service.DossierView(first.Id, SqlFixture.Ra);
        clock.Now = clock.Now.AddSeconds(2);
        foreach (var planned in view.Dossier.PlannedResponses.OrderBy(item => item.Sequence).ToArray())
            view = await service.AdvanceSimulator(first.Id, new(view.Dossier.Version, planned.Id, false, Reason),
                SqlFixture.Ra, CaseScenario.Key());
        var revised = await service.DraftDossier(first.Id, input with
        {
            ExpectedVersion = view.Dossier.Version,
            Content = content with { Title = "SYNTHETISCH: Neue Recovery-PDF-Fassung", ClinicalRationale = Reason + " Neue Quellenkommentierung." }
        },
            SqlFixture.Pms, CaseScenario.Key());
        revised = await Approve(service, revised);
        var rendered = await new PsurExportService(source.Store, clock).Render(EudamedDossierKind.FSCA,
            "synthetic-standalone-recovery-document", content with { Title = "SYNTHETISCH: Zusaetzliches binaeres Original" },
            first.Package.Sources, SqlFixture.Pms);
        var documentBytes = rendered.Bytes;
        var document = await service.RecordDocument(new("ortho", "synthetic-recovery-original.pdf", "application/pdf",
            Convert.ToBase64String(documentBytes), Reason, Reason), SqlFixture.Pms, CaseScenario.Key());
        view = await service.DossierView(revised.Id, SqlFixture.Ra);
        var entry = Assert.Single(view.Ledger.Entries);
        var inbox = (await source.Store.List<EudamedInbox>("eudamedInbox", ["ortho"]))
            .Where(item => item.Original?.DossierId == revised.Id).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, inbox.Length);
        Assert.NotEqual(first.Package.Attachments[0].Hash, document.OriginalHash);
        Assert.NotEqual(first.Package.Attachments[0].Hash, Assert.Single(revised.Package.Attachments).Hash);
        Assert.NotEqual(document.OriginalHash, revised.Package.Attachments[0].Hash);
        var originals = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["certificate"] = references.Certificate.OriginalHash,
            ["document"] = document.OriginalHash,
            ["historical-pdf"] = first.Package.Attachments[0].Hash,
            ["current-pdf"] = Assert.Single(revised.Package.Attachments).Hash,
            ["historical-package"] = first.FrozenPackageHash!,
            ["current-package"] = revised.FrozenPackageHash!,
            ["approved-snapshot"] = entry.ApprovedDossier.Hash,
            ["transport-receipt"] = entry.Attempt.ReceiptHash!
        };
        foreach (var item in inbox) originals.Add("inbox-" + item.Original!.Kind, item.OriginalHash);
        Assert.All(originals.Values, hash => Assert.Matches("^[a-f0-9]{64}$", hash));
        return new(document, first, view.Dossier, view.Ledger, inbox, await Histories(source.Store), originals);
    }

    private static async Task<EudamedDossier> Approve(EudamedService service, EudamedDossier dossier)
    {
        dossier = await service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, Reason), SqlFixture.Medical, CaseScenario.Key());
        dossier = await service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
        EudamedRules.RequireFrozen(dossier);
        return dossier;
    }

    internal static async Task<SortedDictionary<string, string>> Histories(IStateStore store)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in new[] { "eudamedActor", "eudamedReferences", "eudamedConfiguration", "eudamedChannel",
            "eudamedDossier", "eudamedDocument", "eudamedDelivery", "eudamedInbox" })
            values.Add(kind, Canonical.Serialize(await store.History<System.Text.Json.JsonElement>(kind, ["ortho", "cardio"])));
        return values;
    }

    private sealed class NoNetwork : IDomibusGateway
    {
        public string[] AllowedEndpointReferences => [];
        public DomibusReferenceCatalog ReferenceCatalog => new([], [], [], []);
        public DomibusAvailability Availability(DomibusProfile profile) => new(false, ["No recovery network backend."], "None");
        private static Exception Denied() => new InvalidOperationException("Recovery never invokes a real gateway.");
        public Task Check(DomibusProfile profile, CancellationToken ct = default) => throw Denied();
        public Task<DomibusSubmitReceipt> Submit(DomibusProfile profile, DomibusMessage message, CancellationToken ct = default) => throw Denied();
        public Task<DomibusStatusResult> Status(DomibusProfile profile, string messageId, string accessPointRole, CancellationToken ct = default) => throw Denied();
        public Task<DomibusErrorsResult> Errors(DomibusProfile profile, string messageId, string accessPointRole, CancellationToken ct = default) => throw Denied();
        public Task<string[]> Pending(DomibusProfile profile, CancellationToken ct = default) => throw Denied();
        public Task<DomibusRetrievedMessage> Retrieve(DomibusProfile profile, string messageId, CancellationToken ct = default) => throw Denied();
        public Task<byte[]> ConfirmDownload(DomibusProfile profile, string messageId, CancellationToken ct = default) => throw Denied();
    }
}

[Trait("Category", "RecoveryEudamedContract")]
public sealed class EudamedRecoveryContractTests
{
    [Fact]
    public Task TypedGraphIncludesHistoricalAndNestedOriginalsButNotInlineHashes() =>
        Probe(GraphProbe, "EUDAMED_RECOVERY_GRAPH=Verified");

    [Fact]
    public Task PresencePreflightRejectsMissingHistoricalAndNestedOriginalsWithoutReplacingByteVerification() =>
        Probe(GraphProbe + "\n" + PresenceProbe, "EUDAMED_RECOVERY_PRESENCE=Verified");

    private static async Task Probe(string body, string expectedMarker)
    {
        var home = RecoveryFixture.RepositoryRoot();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("COHORS_RECOVERY_PWSH") ?? "pwsh")
        {
            WorkingDirectory = home,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["COHORS_EUDAMED_RECOVERY_SCRIPT"] = Path.Combine(home, "scripts", "Backup-Local.ps1");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "$ErrorActionPreference = 'Stop'\n. $env:COHORS_EUDAMED_RECOVERY_SCRIPT\n" + body)));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Typed recovery contract probe could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, "Typed EUDAMED recovery contract failed; unfiltered child output is not published.");
        Assert.Contains(expectedMarker, output);
    }

    private const string PresenceProbe = """
            $script:presenceRows = $revisions
            function Invoke-RecoverySql($Connection, [string]$Sql, [switch]$Text) {
                if (-not $Text) { throw 'Presence probe only supplies a complete synthetic JSON read.' }
                return Json $script:presenceRows
            }
            $home = Split-Path (Split-Path $env:COHORS_EUDAMED_RECOVERY_SCRIPT -Parent) -Parent
            $root = Assert-RecoveryPath (Join-Path $home ('.local\recovery-presence-contract-' + [Guid]::NewGuid().ToString('N'))) -New
            [IO.Directory]::CreateDirectory($root) | Out-Null
            try {
                foreach ($hash in $references) {
                    [IO.File]::WriteAllText([IO.Path]::Combine($root, $hash), 'SYNTHETIC presence fixture, not verified original bytes.')
                }
                Assert-RecoveryEudamedOriginals $null $root
                Reject { Get-RecoveryObjects $root } 'recovery_object_hash'
                foreach ($hash in @($stored['pdf-old'], $stored['approved-snapshot'],
                    $stored['distribution'], $stored['certificate-group'])) {
                    $path = [IO.Path]::Combine($root, $hash)
                    [IO.File]::Delete($path)
                    Reject { Assert-RecoveryEudamedOriginals $null $root } 'recovery_reference_coverage'
                    [IO.File]::WriteAllText($path, 'SYNTHETIC presence fixture, not verified original bytes.')
                }
                $script:presenceRows = @(@{ Kind = 'eudamedUnknown'; Json = '{}' })
                Reject { Assert-RecoveryEudamedOriginals $null $root } 'recovery_reference'
                Write-Output 'EUDAMED_RECOVERY_PRESENCE=Verified'
            } finally {
                foreach ($hash in $references) { [IO.File]::Delete([IO.Path]::Combine($root, $hash)) }
                [IO.Directory]::Delete($root, $false)
            }
            """;

    internal const string GraphProbe = """
            function Digest([string]$Text) {
                return [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text)))
            }
            function Json($Value) { return ConvertTo-Json -InputObject $Value -Depth 100 -Compress }
            function Reject([scriptblock]$Action, [string]$Code) {
                $failed = $false
                try { & $Action | Out-Null }
                catch {
                    if (-not $_.Exception.Message.StartsWith($Code + ':', [StringComparison]::Ordinal)) { throw }
                    $failed = $true
                }
                if (-not $failed) { throw 'A required typed recovery rejection was absent.' }
            }
            $stored = @{}
            foreach ($label in @('certificate-current', 'certificate-old', 'certificate-group', 'certificate-parent',
                'pdf-old', 'pdf-current', 'pdf-parent', 'pdf-document', 'package-old', 'package-parent',
                'approved-snapshot', 'transport', 'inbox', 'correspondence', 'protected-transport', 'distribution')) {
                $stored[$label] = Digest ('SYNTHETIC STORED ' + $label)
            }
            $pdf = [byte[]](37,80,68,70,45,49,46,55,10,37,255,254,0,128,10,37,37,69,79,70)
            $stored['pdf-document'] = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($pdf))
            function Source([string]$Kind, $Value) {
                $json = Json $Value
                return @{ kind = $Kind; source = @{ id = $Value.id; version = $Value.version; hash = (Digest $json) }; json = $json }
            }
            function Package([string]$Certificate, [string]$Pdf, $Sources) {
                return @{ references = @{ certificate = @{ originalHash = $Certificate } };
                    attachments = @(@{ hash = $Pdf; source = @{ id = 'capa'; version = 1; hash = (Digest 'SQL source only') } });
                    sources = $Sources; content = @{ fsn = $null }; businessXml = '<synthetic />';
                    businessXmlHash = (Digest '<synthetic />'); binding = @{ profileHash = (Digest 'Inline profile') };
                    validation = @{ schemaFingerprint = (Digest 'Inline schema'); enumerationFingerprint = (Digest 'Inline enum') } }
            }
            function Dossier([string]$Id, [int]$Version, $Package, $Frozen) {
                return @{ id = $Id; version = $Version; package = $Package; scopeHash = (Digest (Json $Package));
                    frozenPackageHash = $Frozen; reviewStatus = $(if ($null -eq $Frozen) { 'Draft' } else { 'Frozen' });
                    approvals = @(@{ scopeHash = (Digest 'Inline signature scope') }); attempts = @();
                    businessResponses = @(); correspondence = @(); distributions = @() }
            }
            $group = @{ id = 'eudamed-references-cardio'; version = 4;
                certificate = @{ originalHash = $stored['certificate-group'] } }
            $parent = Dossier 'predecessor' 3 (Package $stored['certificate-parent'] $stored['pdf-parent'] @()) $stored['package-parent']
            $old = Dossier 'dossier' 3 (Package $stored['certificate-old'] $stored['pdf-old'] @(
                (Source 'eudamedReferences' $group), (Source 'eudamedDossier' $parent))) $stored['package-old']
            $stored['approved-snapshot'] = Digest (Json $old)
            $document = @{ id = 'document'; version = 1; originalHash = $stored['pdf-document']; length = $pdf.Length;
                name = 'synthetic.pdf'; mediaType = 'application/pdf'; synthetic = $true }
            $current = Dossier 'dossier' 4 (Package $stored['certificate-current'] $stored['pdf-current'] @((Source 'eudamedDocument' $document))) $null
            $current.package.content.fsn = @{ documentHash = $stored['pdf-document'] }
            $current.distributions = @(@{ packageHash = $stored['package-old']; evidenceHash = $stored['distribution'] })
            $attempt = @{ packageHash = $stored['package-old']; receiptHash = $stored['transport'] }
            $ledger = @{ entries = @(@{ approvedDossier = @{ id = 'dossier'; version = 3; hash = $stored['approved-snapshot'] };
                attempt = $attempt; responses = @(@{ originalHash = $stored['inbox'] });
                correspondence = @(@{ originalHash = $stored['correspondence'] });
                plannedResponses = @(@{ content = (Json @{ packageHash = $stored['package-old']; status = 'Planned' }) }) }) }
            $inbox = @{ originalHash = $stored['inbox']; protectedTransportHash = $stored['protected-transport'];
                original = @{ packageHash = $stored['package-old'] } }
            $revisions = @(
                @{ Kind = 'eudamedDossier'; Json = (Json $current) },
                @{ Kind = 'eudamedDossier'; Json = (Json $old) },
                @{ Kind = 'eudamedDelivery'; Json = (Json $ledger) },
                @{ Kind = 'eudamedInbox'; Json = (Json $inbox) })
            $references = Get-RecoveryEudamedReferences $revisions
            if (@(Compare-Object @($stored.Values | Sort-Object -CaseSensitive) $references -CaseSensitive).Count) {
                throw 'Typed EUDAMED graph lost an original or invented an inline blob.'
            }
            $withoutHistory = Get-RecoveryEudamedReferences @($revisions[0], $revisions[3])
            if ($withoutHistory -ccontains $stored['pdf-old'] -or $references -cnotcontains $stored['pdf-old']) {
                throw 'The old-only PDF did not depend on historical SQL coverage.'
            }
            $inline = @($current.scopeHash, $current.package.businessXmlHash, $current.package.binding.profileHash,
                $current.package.validation.schemaFingerprint, $current.package.validation.enumerationFingerprint,
                $current.approvals[0].scopeHash, $old.package.sources[0].source.hash, $old.package.sources[1].source.hash,
                $current.package.attachments[0].source.hash, (Digest $ledger.entries[0].plannedResponses[0].content))
            foreach ($hash in $inline) {
                if ($references -ccontains $hash) { throw 'An embedded binding was falsely classified as a stored original.' }
            }
            $objects = @($stored.Values | ForEach-Object { @{ hash = $_; length = 20 } })
            $state = @{ references = $references; retention = @{ contract = 'cohors-retention-cut-1'; rows = @() } }
            Assert-RecoveryCoverage $state $objects
            foreach ($hash in $references) {
                Reject { Assert-RecoveryCoverage $state @($objects | Where-Object { $_.hash -cne $hash }) } 'recovery_reference_coverage'
            }
            Reject { Get-RecoveryEudamedReferences @($revisions[0], $revisions[2], $revisions[3]) } 'recovery_sql_integrity'
            $bad = @{ Kind = 'eudamedDocument'; Json = '{"originalHash":null}' }
            Reject { Get-RecoveryEudamedReferences @($bad) } 'recovery_reference'
            $bad.Json = '{}'
            Reject { Get-RecoveryEudamedReferences @($bad) } 'recovery_reference'
            $bad.Kind = 'eudamedUnknown'
            Reject { Get-RecoveryEudamedReferences @($bad) } 'recovery_reference'
            $old.package.sources[0].source.hash = Digest 'Wrong inline source binding'
            Reject { Get-RecoveryEudamedReferences @(@{ Kind = 'eudamedDossier'; Json = (Json $old) }) } 'recovery_sql_integrity'
            $current.package.sources[0].kind = 'eudamedUnknown'
            Reject { Get-RecoveryEudamedReferences @(@{ Kind = 'eudamedDossier'; Json = (Json $current) }) } 'recovery_reference'
            Write-Output ('EUDAMED_RECOVERY_GRAPH=Verified; required=' + $references.Count + '; missing=' + $references.Count +
                '; inlineExcluded=' + $inline.Count + '; malformed=5; missingApprovedSql=1')
            """;
}
