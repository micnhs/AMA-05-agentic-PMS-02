using System.Security.Cryptography;
using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Tests;

public sealed partial class EudamedConfigurationTests
{
    [Fact]
    public async Task DossierEditCannotCreateACallerSuppliedAggregateIdentifier()
    {
        var id = "eudamed-dossier-forged-" + CaseScenario.Key();
        var input = new DraftEudamedDossierCommand(0, "ortho", EudamedDossierKind.MIR, DossierContent(),
            [new("case", "missing-case", 1)], Reason);
        Assert.Equal("eudamed_dossier_missing", (await Assert.ThrowsAsync<DomainException>(() =>
            Service.DraftDossier(id, input, SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Null(await fixture.Store.Get<EudamedDossier>("eudamedDossier", id));
        Assert.Empty(await fixture.Store.Audit(id));
    }

    [Fact]
    public async Task MirDossierFreezesExactSourcesAndRevisesWithoutReusingOldApprovals()
    {
        var service = Service;
        await ActivateSimulator(service);
        var scenario = await ApprovedMir();
        var originalCase = Canonical.Hash(scenario.Case);
        var input = new DraftEudamedDossierCommand(0, "ortho", EudamedDossierKind.MIR, DossierContent(),
            [new("case", scenario.Case.Id, scenario.Case.Version)], Reason);
        var key = CaseScenario.Key();
        var dossier = await service.DraftDossier(null, input, SqlFixture.Pms, key);
        Assert.True(dossier.Package.Validation.Errors.Length == 0, string.Join("\n", dossier.Package.Validation.Errors));
        Assert.Equal(Canonical.Hash(dossier), Canonical.Hash(await service.DraftDossier(null, input, SqlFixture.Pms, key)));
        Assert.Equal(originalCase, dossier.Package.Sources.Single().Source.Hash);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, Reason), SqlFixture.Pms, CaseScenario.Key()))).Status);
        dossier = await service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, Reason), SqlFixture.Medical, CaseScenario.Key());
        Assert.Equal("four_eyes", (await Assert.ThrowsAsync<DomainException>(() =>
            service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, null),
                SqlFixture.Medical with { Roles = [Roles.Regulatory] }, CaseScenario.Key()))).Code);
        dossier = await service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
        EudamedRules.RequireFrozen(dossier);
        Assert.Equal(dossier.ScopeHash, Canonical.HashText(await fixture.Objects.Read(dossier.FrozenPackageHash!)));
        Assert.Equal(originalCase, Canonical.Hash(await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id)));
        Assert.Equal("eudamed_review_state", (await Assert.ThrowsAsync<DomainException>(() =>
            service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key()))).Code);
        var old = Canonical.Hash(dossier);
        var revised = await service.DraftDossier(dossier.Id, input with { ExpectedVersion = dossier.Version,
            Content = input.Content with { ClinicalRationale = Reason + " Neue ausdruecklich menschliche medizinische Einordnung." } },
            SqlFixture.Medical, CaseScenario.Key());
        Assert.Equal("Draft", revised.ReviewStatus);
        Assert.Empty(revised.Approvals);
        Assert.Null(revised.FrozenPackageHash);
        Assert.NotEqual(dossier.ScopeHash, revised.ScopeHash);
        Assert.Contains(await service.DossierHistory(dossier.Id, SqlFixture.Ra), history => Canonical.Hash(history) == old);
        Assert.Equal(dossier.ScopeHash, Canonical.HashText(await fixture.Objects.Read(dossier.FrozenPackageHash!)));
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Fact]
    public async Task ChangedLiveSourceAndDisabledChannelBlockApprovalWithoutMutatingTheCapturedDossier()
    {
        var service = Service;
        var active = await ActivateSimulator(service);
        var scenario = await ApprovedMir();
        var dossier = await service.DraftDossier(null, new(0, "ortho", EudamedDossierKind.MIR, DossierContent(),
            [new("case", scenario.Case.Id, scenario.Case.Version)], Reason), SqlFixture.Pms, CaseScenario.Key());
        var original = Canonical.Hash(dossier);
        await scenario.Assess();
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() =>
            service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, Reason), SqlFixture.Medical, CaseScenario.Key()))).Status);
        await service.Deactivate("ortho", new(active.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal("eudamed_channel_fenced", (await Assert.ThrowsAsync<DomainException>(() =>
            service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, Reason), SqlFixture.Medical, CaseScenario.Key()))).Code);
        Assert.Equal(original, Canonical.Hash(await service.GetDossier(dossier.Id, SqlFixture.Ra)));
        Assert.DoesNotContain(await fixture.Store.Audit(dossier.Id), item => item.Action == "EudamedDossierApproved");
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Fact]
    public async Task FscaPdfAndRecordedBinaryOriginalSurviveApprovalWithExactHashesAndScope()
    {
        var service = Service;
        await ActivateSimulator(service);
        var scenario = new CaseScenario(fixture);
        await scenario.Create();
        var quality = new QualityService(fixture.Store, fixture.Objects, clock);
        var capa = await quality.OpenCapa(new(scenario.Case.Id, "ortho", "SYNTHETISCH: Quellgebundene Feldmassnahme",
            Reason, Reason, SqlFixture.Quality.Id, Reason, clock.Now.AddDays(10)), SqlFixture.Quality, CaseScenario.Key());
        var content = DossierContent() with { Fsca = new(["DEMO-BATCH"], [], "Synthetische Feldkorrektur",
            Reason, Reason, "Synthetic Quality role", Reason, clock.Now.AddDays(10), [capa.Id],
            DateOnly.FromDateTime(clock.Now.UtcDateTime), Reason, Reason, "NO_INV_ONGOING", Reason,
            ["CUSTOMER_INFORMATION_ONLY"], ["IDENTIFY_DEVICE"], Reason) };
        var dossier = await service.DraftDossier(null, new(0, "ortho", EudamedDossierKind.FSCA, content,
            [new("capa", capa.Id, capa.Version)], Reason), SqlFixture.Pms, CaseScenario.Key());
        Assert.Empty(dossier.Package.Validation.Errors);
        var attachment = Assert.Single(dossier.Package.Attachments);
        var bytes = await fixture.Objects.ReadBytes(attachment.Hash);
        Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8));
        Assert.Equal(attachment.Hash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        dossier = await service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, Reason), SqlFixture.Medical, CaseScenario.Key());
        dossier = await service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
        EudamedRules.RequireFrozen(dossier);
        Assert.Equal(bytes, await fixture.Objects.ReadBytes(attachment.Hash));
        var documentInput = new EudamedDocumentCommand("ortho", "synthetic-fsca-original.pdf", "application/pdf",
            Convert.ToBase64String(bytes), Reason, Reason);
        var key = CaseScenario.Key();
        var recorded = await service.RecordDocument(documentInput, SqlFixture.Pms, key);
        Assert.Equal(attachment.Hash, recorded.OriginalHash);
        Assert.Equal(recorded, await service.RecordDocument(documentInput, SqlFixture.Pms, key));
        Assert.Equal("eudamed_document_type", (await Assert.ThrowsAsync<DomainException>(() =>
            service.RecordDocument(documentInput with { Name = "../outside.pdf" }, SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.GetDossier(dossier.Id, SqlFixture.Ra with { Products = ["cardio"] }))).Status);
        Assert.Equal(0, gateway.NetworkCalls);
    }

    private async Task<EudamedChannel> ActivateSimulator(EudamedService service)
    {
        var (application, references) = await RegisteredReferences(service);
        var workspace = await service.Workspace("ortho", SqlFixture.Ra);
        var draft = await service.SaveConfiguration(new(workspace.Draft?.Version ?? 0, "ortho",
            Simulator(application, references), Reason), SqlFixture.Ra, CaseScenario.Key());
        return await service.Activate("ortho", new(workspace.ActiveChannel?.Version ?? 0, draft.Version, true, Reason),
            SqlFixture.Ra, CaseScenario.Key());
    }

    private async Task<CaseScenario> ApprovedMir()
    {
        var scenario = new CaseScenario(fixture);
        await scenario.Create();
        await scenario.Assess();
        await scenario.Prepare();
        scenario.Case = await scenario.Regulatory.AttachMir(scenario.Case.Id, new(scenario.Case.Version,
            scenario.MirFacts() with { DeviceProblemCode = "A0401", InvestigationTypeCode = "B01",
                InvestigationFindingCode = "C01", InvestigationConclusionCode = "D01", ComponentCode = "G01",
                Rationale = "SYNTHETISCH: Ausdrueckliche Testauswahl aus den gepinnten aktiven EUDAMED-Katalogen; keine reale klinische Kodierentscheidung. " + Reason },
            Reason), SqlFixture.Medical, CaseScenario.Key());
        await scenario.Approve();
        return scenario;
    }

    private static EudamedDossierContent DossierContent() => new("SYNTHETISCH: Quellengebundenes Pruefdossier",
        Reason, Reason, "de", "EU-IE", ["IE"], "Submit", null, null, null, null, null);
}
