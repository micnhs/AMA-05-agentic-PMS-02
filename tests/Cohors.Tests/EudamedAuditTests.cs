using System.IO.Compression;
using Cohors.Api;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed partial class ProductAuditTests
{
    [Fact]
    public async Task EudamedAuditPreservesBinaryPdfFrozenPackagesAndReceiptsAndFailsOnMissingOriginals()
    {
        var (service, gateway) = await AuditSimulator();
        var scenario = new CaseScenario(fixture);
        await scenario.Create();
        var capa = await new QualityService(fixture.Store, fixture.Objects, clock).OpenCapa(
            new(scenario.Case.Id, "ortho", "SYNTHETISCH: Gebundene Feldmassnahme", Reason, Reason,
                SqlFixture.Quality.Id, Reason, clock.Now.AddDays(10)), SqlFixture.Quality, CaseScenario.Key());
        var content = AuditDossierContent() with { Fsca = new(["DEMO-BATCH"], [], "Synthetische Feldkorrektur",
            Reason, Reason, "Synthetic Quality role", Reason, clock.Now.AddDays(10), [capa.Id],
            DateOnly.FromDateTime(clock.Now.UtcDateTime), Reason, Reason, "NO_INV_ONGOING", Reason,
            ["CUSTOMER_INFORMATION_ONLY"], ["IDENTIFY_DEVICE"], Reason) };
        var dossier = await service.DraftDossier(null, new(0, "ortho", EudamedDossierKind.FSCA, content,
            [new("capa", capa.Id, capa.Version)], Reason), SqlFixture.Pms, CaseScenario.Key());
        var view = await AcceptAuditDossier(service, dossier);
        var attachment = Assert.Single(view.Dossier.Package.Attachments);
        var pdf = await fixture.Objects.ReadBytes(attachment.Hash);
        await service.RecordDocument(new("ortho", "synthetic-fsca-original.pdf", "application/pdf",
            Convert.ToBase64String(pdf), "SYNTHETISCH: " + Reason, Reason), SqlFixture.Pms, CaseScenario.Key());
        var before = Canonical.Hash(await fixture.Store.CaptureProductAudit("ortho", Auditor, clock.Now));
        var file = await Exporter().Export("ortho", Auditor);
        using (var archive = new ZipArchive(new MemoryStream(file.Bytes), ZipArchiveMode.Read))
        {
            VerifyChecksums(archive);
            using var manifest = ReadJson(archive, "manifest.json");
            var objects = manifest.RootElement.GetProperty("objects").EnumerateArray().ToArray();
            var original = Assert.Single(objects, o => o.GetProperty("hash").GetString() == attachment.Hash);
            Assert.EndsWith(".pdf", original.GetProperty("path").GetString());
            Assert.Equal(pdf, Read(archive, original.GetProperty("path").GetString()!));
            var entry = Assert.Single(view.Ledger.Entries);
            foreach (var required in new[] { view.Dossier.FrozenPackageHash, entry.ApprovedDossier.Hash,
                entry.Attempt.ReceiptHash, Assert.Single(entry.Responses).OriginalHash,
                view.Dossier.Package.References.Certificate.OriginalHash })
                Assert.Contains(objects, o => o.GetProperty("hash").GetString() == required);
            Assert.DoesNotContain(objects, o => o.GetProperty("hash").GetString() == view.Dossier.Package.BusinessXmlHash);
            var kinds = manifest.RootElement.GetProperty("aggregates").EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToArray();
            foreach (var kind in new[] { "eudamedActor", "eudamedChannel", "eudamedConfiguration", "eudamedReferences",
                "eudamedDossier", "eudamedDelivery", "eudamedInbox", "eudamedDocument" })
                Assert.Contains(kind, kinds);
        }
        Assert.Equal(before, Canonical.Hash(await fixture.Store.CaptureProductAudit("ortho", Auditor, clock.Now)));
        var other = await Exporter().Export("cardio", Auditor with { Products = ["cardio"] });
        using (var archive = new ZipArchive(new MemoryStream(other.Bytes), ZipArchiveMode.Read))
        using (var manifest = ReadJson(archive, "manifest.json"))
            Assert.DoesNotContain(manifest.RootElement.GetProperty("aggregates").EnumerateArray(),
                e => e.GetProperty("productId").GetString() == "ortho");
        foreach (var missingHash in new[] { attachment.Hash, view.Dossier.FrozenPackageHash!, view.Ledger.Entries[0].ApprovedDossier.Hash })
        {
            var path = Path.Combine(fixture.ObjectDirectory, missingHash);
            var bytes = await File.ReadAllBytesAsync(path);
            try
            {
                File.Delete(path);
                Assert.Equal("audit_object_unavailable", (await Assert.ThrowsAsync<DomainException>(() =>
                    Exporter().Export("ortho", Auditor))).Code);
            }
            finally { await File.WriteAllBytesAsync(path, bytes); }
        }
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Fact]
    public async Task EudamedAuditIncludesHistoricalNonLeadingDeliveryAndInboxWithAllMemberAuthorization()
    {
        var (service, gateway) = await AuditSimulator();
        var actor = Assert.Single((await service.Workspace("ortho", SqlFixture.Ra)).Actors);
        await AuditReferences(service, "cardio", actor);
        var input = new RecordPsurGroupCommand(0, "DEMO-PSUR-GROUP-EU-AUDIT-" + CaseScenario.Key().ToUpperInvariant(),
            "ortho", ["ortho", "cardio"], new(2025, 11, 15), Reason, Reason);
        var group = await Analytics().RecordGroup(input, SqlFixture.Pms, CaseScenario.Key());
        var report = await Analytics().Freeze(new("ortho", "ortho", group.FirstPeriodStart,
            group.FirstPeriodStart.AddYears(1).AddDays(-1), "Worldwide", GroupId: group.Id, GroupVersion: group.Version),
            SqlFixture.Pms, CaseScenario.Key());
        var content = AuditDossierContent() with { Period = new(report.Snapshot.Start, report.Snapshot.End, Reason) };
        var dossier = await service.DraftDossier(null, new(0, "ortho", EudamedDossierKind.PSUR, content,
            [new("psur", report.Id, report.Version)], Reason), SqlFixture.Pms, CaseScenario.Key());
        var view = await AcceptAuditDossier(service, dossier);
        await Analytics().RecordGroup(input with { ExpectedVersion = group.Version, MemberProductIds = ["ortho"] },
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            Exporter().Export("cardio", Auditor with { Products = ["cardio"] }))).Status);
        var file = await Exporter().Export("cardio", Auditor);
        using var archive = new ZipArchive(new MemoryStream(file.Bytes), ZipArchiveMode.Read);
        VerifyChecksums(archive);
        using var manifest = ReadJson(archive, "manifest.json");
        var aggregates = manifest.RootElement.GetProperty("aggregates").EnumerateArray().ToArray();
        Assert.Contains(aggregates, e => e.GetProperty("id").GetString() == dossier.Id);
        var ledger = Assert.Single(aggregates, e => e.GetProperty("id").GetString() == view.Ledger.Id);
        var inbox = Assert.Single(aggregates, e => e.GetProperty("id").GetString() == view.Dossier.BusinessResponses[0].InboxId);
        foreach (var bound in new[] { ledger, inbox })
            Assert.Equal(new[] { "cardio", "ortho" },
                bound.GetProperty("products").EnumerateArray().Select(p => p.GetString()).ToArray());
        Assert.Equal(Canonical.Hash(view.Dossier), Canonical.Hash(await service.GetDossier(dossier.Id, SqlFixture.Ra)));
        Assert.Equal(0, gateway.NetworkCalls);
    }

    private async Task<(EudamedService, EudamedConfigurationTests.NoNetworkGateway)> AuditSimulator()
    {
        var gateway = new EudamedConfigurationTests.NoNetworkGateway();
        var service = new EudamedService(fixture.Store, fixture.Objects, fixture.Policies, gateway,
            new EudamedComposer(), new PsurExportService(fixture.Store, clock), clock);
        var actor = await service.SaveActor(null, new(0, "ortho", new("DEMO audit manufacturer", "MF", "IE",
            "SYNTHETISCH: Audit test address", new("Demo", "Safety", "contact@example.invalid", "000000"), true),
            Reason), SqlFixture.Pms, CaseScenario.Key());
        actor = await service.SubmitActor(actor.Id, new(actor.Version, "Approve", Reason), SqlFixture.Ra, CaseScenario.Key());
        actor = await service.ProcessActor(actor.Id, actor.Version);
        var references = await AuditReferences(service, "ortho", actor);
        var draft = await service.SaveConfiguration(new(0, "ortho",
            new(EudamedMode.Simulator, EudamedEnvironment.Simulation, actor.Id, actor.Version, references.Id,
                references.Version, "DEMO-CA-IE", EudamedContracts.Business, EudamedContracts.Gateway, null), Reason),
            SqlFixture.Ra, CaseScenario.Key());
        await service.Activate("ortho", new(0, draft.Version, true, Reason), SqlFixture.Ra, CaseScenario.Key());
        return (service, gateway);
    }

    private async Task<EudamedReferences> AuditReferences(EudamedService service, string product, EudamedActorApplication actor)
    {
        var governance = (await fixture.Store.Get<ProductGovernance>("productGovernance", product))!;
        return await service.MapReferences(new(0, product, actor.Id, actor.Version, governance.Version, "synthetic-1",
            new(2025, 1, 1), new(2030, 12, 31), "SYNTHETISCH: " + Reason, "DEMO-NB-TEST", "GS1", Reason),
            SqlFixture.Ra, CaseScenario.Key());
    }

    private static EudamedDossierContent AuditDossierContent() =>
        new("SYNTHETISCH: Gebundene Audittestfassung", Reason, Reason, "de", "EU-IE", ["IE"],
            "Submit", null, null, null, null, null);

    private static async Task<EudamedDossierView> AcceptAuditDossier(EudamedService service, EudamedDossier dossier)
    {
        dossier = await service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, Reason), SqlFixture.Medical, CaseScenario.Key());
        dossier = await service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
        var view = await service.DossierView(dossier.Id, SqlFixture.Ra);
        view = await service.QueueDossier(dossier.Id, new(dossier.Version, view.Ledger.Version, "Accept", Reason),
            SqlFixture.Ra, CaseScenario.Key());
        await service.ProcessDelivery(view.Ledger.Id, Assert.Single(view.Dossier.Attempts).Id);
        view = await service.DossierView(dossier.Id, SqlFixture.Ra);
        return await service.AdvanceSimulator(dossier.Id, new(view.Dossier.Version,
            Assert.Single(view.Dossier.PlannedResponses).Id, false, Reason), SqlFixture.Ra, CaseScenario.Key());
    }
}
