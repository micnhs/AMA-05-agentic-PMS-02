using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Cohors.Agents;
using Cohors.Api;
using Cohors.Application;
using Cohors.Domain;
using PdfSharp.Pdf.IO;

namespace Cohors.Tests;

[Collection("SQL pilot")]
public sealed class PsurExportTests : IAsyncLifetime
{
    private readonly SqlFixture fixture = new() { SeedCases = false };
    private readonly TestClock clock = new(DateTimeOffset.Parse("2026-11-16T15:00:00Z"));
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();
    private AnalyticsService Service() => new(fixture.Store, new AgentRunner(new(null, null, null, null)), fixture.Policies, clock);
    private Task<Psur> Freeze() => Service().Freeze(new("ortho", "ortho", new(2025, 11, 15), new(2026, 11, 14), "Worldwide"),
        SqlFixture.Pms, CaseScenario.Key());

    [Fact]
    public async Task T16_ExportBindsReadablePdfCompleteMetadataAndChecksumsWithoutChangingTheReport()
    {
        var scenario = new CaseScenario(fixture);
        await scenario.Create();
        var p = await Freeze();
        const string reason = "Synthetischer medizinisch-technischer Entwurf mit unveraenderten Quellangaben kontrolliert geprueft.";
        p = await Service().Revise(p.Id, new(p.Version, p.Draft + "\nPr\u00fcftext: \u00c4tiologie, Gef\u00e4\u00df, \u03b2-Reaktion. Keine kausale Feststellung.", reason),
            SqlFixture.Medical, CaseScenario.Key());
        p = await Service().Approve(p.Id, new(p.Version, reason), SqlFixture.Medical, CaseScenario.Key());
        p = await Service().Approve(p.Id, new(p.Version, reason), SqlFixture.Ra, CaseScenario.Key());
        var exporter = new PsurExportService(fixture.Store, clock);
        var file = await exporter.Export(p.Id, p.Version, "zip", SqlFixture.Pms);
        Assert.Equal("application/zip", file.ContentType);
        using var archive = new ZipArchive(new MemoryStream(file.Bytes), ZipArchiveMode.Read);
        Assert.Equal(["audit.json", "checksums.json", "metadata.json", "report.json", "report.pdf", "snapshot.json"],
            archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        byte[] Read(string name)
        {
            using var input = archive.GetEntry(name)!.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(Read("checksums.json"))!;
        Assert.Equal(archive.Entries.Where(e => e.FullName != "checksums.json").Select(e => e.FullName).Order(StringComparer.Ordinal),
            manifest.Keys.Order(StringComparer.Ordinal));
        foreach (var item in manifest) Assert.Equal(item.Value, Convert.ToHexStringLower(SHA256.HashData(Read(item.Key))));
        Assert.Equal(Canonical.Serialize(p), System.Text.Encoding.UTF8.GetString(Read("report.json")));
        Assert.Equal(Canonical.Serialize(p.Snapshot), System.Text.Encoding.UTF8.GetString(Read("snapshot.json")));
        using var metadata = JsonDocument.Parse(Read("metadata.json"));
        Assert.Equal(p.ScopeHash, metadata.RootElement.GetProperty("scopeHash").GetString());
        Assert.Equal(p.Snapshot.Series!.Reference, metadata.RootElement.GetProperty("series").GetProperty("reference").GetString());
        Assert.Equal(2, metadata.RootElement.GetProperty("approvals").GetArrayLength());
        Assert.Contains("Lesekopien", metadata.RootElement.GetProperty("boundary").GetString());
        var pdf = Read("report.pdf");
        using var document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        Assert.True(document.PageCount >= 2, "The full report must remain paginated rather than truncated to a cover page.");
        Assert.Contains(p.Snapshot.Series.Reference, document.Info.Title);
        Assert.Equal(Canonical.Hash(p), Canonical.Hash(await fixture.Store.Get<Psur>("psur", p.Id)));
        var artifacts = Path.Combine(AppContext.BaseDirectory, "ExportEvidence");
        Directory.CreateDirectory(artifacts);
        await File.WriteAllBytesAsync(Path.Combine(artifacts, "cohors-psur.pdf"), pdf);
        await File.WriteAllBytesAsync(Path.Combine(artifacts, "cohors-psur.zip"), file.Bytes);
    }

    [Fact]
    public async Task T16_ExportRejectsWrongProductStaleVersionAndTamperedSourceBinding()
    {
        var p = await Freeze();
        var exporter = new PsurExportService(fixture.Store, clock);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            exporter.Export(p.Id, p.Version, "json", SqlFixture.Pms with { Products = ["cardio"] }))).Status);
        Assert.Equal("version_conflict", (await Assert.ThrowsAsync<DomainException>(() =>
            exporter.Export(p.Id, p.Version - 1, "pdf", SqlFixture.Pms))).Code);
        Assert.Equal("psur_export_format", (await Assert.ThrowsAsync<DomainException>(() =>
            exporter.Export(p.Id, p.Version, "unsupported", SqlFixture.Pms))).Code);
        var forged = p with { Version = p.Version + 1, SnapshotHash = new string('0', 64) };
        await fixture.Store.Execute<Psur>(new(p.Id, "psur", p.ProductId, p.Version, CaseScenario.Key(), "InvalidExportBinding",
            "Gezielte synthetische Negativprobe mit inkonsistentem Snapshot-Hash.", SqlFixture.Quality, forged, clock.Now), _ => forged);
        Assert.Equal("psur_hash", (await Assert.ThrowsAsync<DomainException>(() =>
            exporter.Export(p.Id, forged.Version, "zip", SqlFixture.Pms))).Code);
    }

    [Fact]
    public async Task T16_LegacyExportShowsCurrentBlockingFindingsWithoutRepairingFrozenSources()
    {
        var p = await Freeze();
        var snapshot = p.Snapshot with { SourceManifest = null, Series = null, Actions = null, Directory = null };
        var legacy = p with { Version = p.Version + 1, Snapshot = snapshot, SnapshotHash = Canonical.Hash(snapshot), Findings = [] };
        legacy = legacy with { ScopeHash = PortfolioRules.PsurScope(legacy) };
        await fixture.Store.Execute<Psur>(new(p.Id, "psur", p.ProductId, p.Version, CaseScenario.Key(), "LegacyExportFixture",
            "Synthetischer Altbericht ohne nachtraegliche Erfindung historischer Quellstaende.", SqlFixture.Quality, legacy, clock.Now), _ => legacy);
        var data = await new PsurExportService(fixture.Store, clock).Read(p.Id, legacy.Version, SqlFixture.Pms);
        Assert.Equal("Blocked", data.SnapshotCheckState);
        Assert.Contains(data.CurrentFindings, f => f.Severity is "Critical" or "Major");
        Assert.Null(data.Report.Snapshot.SourceManifest);
        Assert.Null(data.Report.Snapshot.Series);
        Assert.Equal(Canonical.Hash(legacy), Canonical.Hash(await fixture.Store.Get<Psur>("psur", p.Id)));
    }
}
