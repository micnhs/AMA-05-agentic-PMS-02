using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
        var (service, dossier) = await DraftFscaDossier();
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

    [Fact]
    [Trait("Category", "FsnPublication")]
    public async Task FsnPublicationNeedsSeparateFourEyesAndExplicitConfirmationAndPreservesOriginalEvidence()
    {
        var (service, dossier) = await FrozenFsnDossier(false);
        var view = await ReceiveDossier(service, dossier, "Accept");
        var original = view.Dossier;
        var frozenBytes = await fixture.Objects.ReadBytes(original.FrozenPackageHash!);
        var legacy = JsonNode.Parse(Canonical.Serialize(original))!.AsObject();
        legacy.Remove("fsnPublication");
        var legacyJson = Canonical.Serialize(legacy);
        Assert.Equal(Canonical.HashText(legacyJson), Canonical.Hash(Canonical.Read<EudamedDossier>(legacyJson)));
        var draft = FsnPublicationInput(view);
        Assert.Equal("fsn_urgent_source", (await Assert.ThrowsAsync<DomainException>(() =>
            service.DraftFsnPublication(dossier.Id, draft with { Route = "UrgentAlternative" }, SqlFixture.Ra, CaseScenario.Key()))).Code);
        dossier = await service.DraftFsnPublication(dossier.Id, draft, SqlFixture.Pms, CaseScenario.Key());
        Assert.Empty(dossier.FsnPublication!.Plan.Feedback);
        Assert.Empty(dossier.Distributions);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.ApproveFsnPublication(dossier.Id, new(dossier.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key()))).Status);
        dossier = await service.ApproveFsnPublication(dossier.Id, new(dossier.Version, Reason, null), SqlFixture.Medical, CaseScenario.Key());
        Assert.Equal("four_eyes", (await Assert.ThrowsAsync<DomainException>(() =>
            service.ApproveFsnPublication(dossier.Id, new(dossier.Version, Reason, null),
                SqlFixture.Medical with { Roles = [Roles.Regulatory] }, CaseScenario.Key()))).Code);
        dossier = await service.ApproveFsnPublication(dossier.Id, new(dossier.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
        var before = dossier;
        var ledgerHash = Canonical.Hash((await service.DossierView(dossier.Id, SqlFixture.Ra)).Ledger);
        var auditBefore = await fixture.Store.Audit(dossier.Id);
        var command = new EudamedDistributionCommand(dossier.Version, view.Ledger.Version,
            dossier.FsnPublication!.ScopeHash, false, Reason);
        Assert.Equal("fsn_publication_confirmation", (await Assert.ThrowsAsync<DomainException>(() =>
            service.PublishFsnDistribution(dossier.Id, command, SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal(Canonical.Hash(before), Canonical.Hash(await service.GetDossier(dossier.Id, SqlFixture.Ra)));
        Assert.Equal(auditBefore.Length, (await fixture.Store.Audit(dossier.Id)).Length);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.PublishFsnDistribution(dossier.Id, command with { ConfirmSimulation = true }, SqlFixture.Medical, CaseScenario.Key()))).Status);
        var key = CaseScenario.Key();
        command = command with { ConfirmSimulation = true };
        dossier = await service.PublishFsnDistribution(dossier.Id, command, SqlFixture.Ra, key);
        var distribution = Assert.Single(dossier.Distributions);
        Assert.Equal(EudamedContracts.Bind(before), distribution.PublicationSource);
        Assert.Equal(Canonical.Hash(dossier), Canonical.Hash(await service.PublishFsnDistribution(dossier.Id, command, SqlFixture.Ra, key)));
        Assert.Equal("Published", dossier.FsnPublication!.ReviewStatus);
        Assert.Equal("Draft", dossier.Package.Content.Fsn!.PublicationState);
        Assert.Equal(frozenBytes, await fixture.Objects.ReadBytes(dossier.FrozenPackageHash!));
        Assert.Equal(ledgerHash, Canonical.Hash((await service.DossierView(dossier.Id, SqlFixture.Ra)).Ledger));
        var file = await service.ReadDossierOriginal(dossier.Id, dossier.Version, distribution.EvidenceHash, SqlFixture.Ra);
        Assert.Equal(distribution.EvidenceHash, Convert.ToHexStringLower(SHA256.HashData(file.Bytes)));
        var evidence = Canonical.Read<FsnDistributionEvidence>(Encoding.UTF8.GetString(file.Bytes));
        Assert.Equal(Canonical.Hash(dossier.FsnPublication), Canonical.Hash(evidence.Publication));
        Assert.Equal(distribution.PublicationScopeHash, Canonical.Hash(evidence.Publication.Plan));
        Assert.True(evidence.Simulated);
        Assert.Equal("simulator://fsn/DEMO-IE-001", Assert.Single(evidence.Recipients).TechnicalRecipient);
        Assert.Equal(original.Version, evidence.Publication.Plan.DossierSource.Version);
        Assert.Equal(2, evidence.Publication.Approvals.Select(a => a.ActorId).Distinct(StringComparer.Ordinal).Count());
        await Assert.ThrowsAsync<DomainException>(() =>
            service.ReadDossierOriginal(dossier.Id, before.Version, distribution.EvidenceHash, SqlFixture.Ra));
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.ReadDossierOriginal(dossier.Id, dossier.Version, distribution.EvidenceHash,
                SqlFixture.Ra with { Products = ["cardio"] }))).Status);
        var capture = await fixture.Store.CaptureProductAudit("ortho", SqlFixture.Ra, clock.Now);
        Assert.Contains(ProductAuditRules.Objects(capture.Aggregates).Objects, item => item.Hash == distribution.EvidenceHash);
        Assert.Contains(await fixture.Store.Audit(dossier.Id), item => item.Action == "FsnPublicationSimulated");
        Assert.False(string.IsNullOrWhiteSpace(await OutboxId("eudamedDossier", dossier.Id, dossier.Version)));
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Fact]
    [Trait("Category", "FsnPublication")]
    public async Task NewFsnCorrespondenceInvalidatesPublicationAndRequiresExplicitOriginalBoundAssessment()
    {
        var (service, dossier) = await FrozenFsnDossier(false);
        var view = await QueueAndProcess(service, dossier, "Comment");
        clock.Now = clock.Now.AddSeconds(2);
        var planned = view.Dossier.PlannedResponses.OrderBy(p => p.Sequence).ToArray();
        view = await service.AdvanceSimulator(dossier.Id, new(view.Dossier.Version, planned[0].Id, false, Reason), SqlFixture.Ra, CaseScenario.Key());
        dossier = await FreezeFsnPublication(service, view);
        var old = new EudamedDistributionCommand(dossier.Version, view.Ledger.Version, dossier.FsnPublication!.ScopeHash, true, Reason);
        view = await service.AdvanceSimulator(dossier.Id, new(dossier.Version, planned[1].Id, false, Reason), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal("version_conflict", (await Assert.ThrowsAsync<DomainException>(() =>
            service.PublishFsnDistribution(dossier.Id, old, SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal("fsn_publication_stale", (await Assert.ThrowsAsync<DomainException>(() =>
            service.PublishFsnDistribution(dossier.Id, old with { ExpectedVersion = view.Dossier.Version,
                ExpectedLedgerVersion = view.Ledger.Version }, SqlFixture.Ra, CaseScenario.Key()))).Code);
        var input = FsnPublicationInput(view);
        Assert.Equal("fsn_feedback_incomplete", (await Assert.ThrowsAsync<DomainException>(() =>
            service.DraftFsnPublication(dossier.Id, input with { Feedback = [] }, SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Equal("fsn_feedback_stale", (await Assert.ThrowsAsync<DomainException>(() =>
            service.DraftFsnPublication(dossier.Id, input with { Feedback = [input.Feedback[0] with { OriginalHash = Canonical.HashText("not-the-original") }] },
                SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Equal("fsn_recipient_scope", (await Assert.ThrowsAsync<DomainException>(() =>
            service.DraftFsnPublication(dossier.Id, input with { Recipients = [new("DEMO-DE-001", "DE", "SYNTHETISCH: Nicht freigegebenes Land")] },
                SqlFixture.Pms, CaseScenario.Key()))).Code);
        dossier = await service.DraftFsnPublication(dossier.Id, input, SqlFixture.Pms, CaseScenario.Key());
        Assert.Empty(dossier.FsnPublication!.Approvals);
        Assert.NotEqual(old.PublicationScopeHash, dossier.FsnPublication.ScopeHash);
        Assert.Equal(Canonical.Hash(Assert.Single(FsnPublicationRules.Feedback(view.Ledger))),
            Canonical.Hash(Assert.Single(dossier.FsnPublication.Plan.Feedback).Original));
        Assert.Empty(dossier.Distributions);
        Assert.Equal("fsn_publication_confirmation", (await Assert.ThrowsAsync<DomainException>(() =>
            service.PublishFsnDistribution(dossier.Id, old with { ExpectedVersion = dossier.Version,
                ExpectedLedgerVersion = view.Ledger.Version }, SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Theory]
    [InlineData("InformationRequest")]
    [InlineData("Unknown")]
    [Trait("Category", "FsnPublication")]
    public async Task UrgentFsnAlternativeIsSeparatelyApprovedAndNeverRetriesOrClosesAuthorityWork(string scenario)
    {
        var (service, dossier) = await FrozenFsnDossier(true);
        var view = await ReceiveDossier(service, dossier, scenario);
        var input = FsnPublicationInput(view);
        await Assert.ThrowsAsync<DomainException>(() =>
            service.DraftFsnPublication(dossier.Id, input, SqlFixture.Ra, CaseScenario.Key()));
        var before = Canonical.Hash(view.Ledger);
        dossier = await FreezeFsnPublication(service, view, "UrgentAlternative");
        dossier = await service.PublishFsnDistribution(dossier.Id, new(dossier.Version, view.Ledger.Version,
            dossier.FsnPublication!.ScopeHash, true, Reason), SqlFixture.Ra, CaseScenario.Key());
        var current = await service.DossierView(dossier.Id, SqlFixture.Ra);
        Assert.Equal(before, Canonical.Hash(current.Ledger));
        Assert.Single(current.Dossier.Distributions);
        Assert.Single(current.Dossier.Attempts);
        if (scenario == "InformationRequest")
            Assert.Equal("Open", Assert.Single(FsnPublicationRules.Feedback(current.Ledger)).Status);
        else
            Assert.Equal("TransmissionUnknown", Assert.Single(current.Dossier.Attempts).Status);
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Theory]
    [InlineData("channel")]
    [InlineData("source")]
    [Trait("Category", "FsnPublication")]
    public async Task ChangedFsnRecipientProfileOrSourceCannotReusePublicationApproval(string boundary)
    {
        var (service, original) = await FrozenFsnDossier(true);
        var view = await service.DossierView(original.Id, SqlFixture.Ra);
        var dossier = await FreezeFsnPublication(service, view, "UrgentAlternative");
        var oldScope = dossier.FsnPublication!.ScopeHash;
        view = await service.DossierView(dossier.Id, SqlFixture.Ra);
        dossier = await service.DraftFsnPublication(dossier.Id, FsnPublicationInput(view, "UrgentAlternative") with
        { Recipients = [new("DEMO-IE-002", "IE", "SYNTHETISCH: Zweiter Testempfaenger")] }, SqlFixture.Pms, CaseScenario.Key());
        Assert.Empty(dossier.FsnPublication!.Approvals);
        Assert.Equal("fsn_publication_confirmation", (await Assert.ThrowsAsync<DomainException>(() =>
            service.PublishFsnDistribution(dossier.Id, new(dossier.Version, view.Ledger.Version, oldScope, true, Reason),
                SqlFixture.Ra, CaseScenario.Key()))).Code);
        dossier = await service.ApproveFsnPublication(dossier.Id, new(dossier.Version, Reason, null), SqlFixture.Medical, CaseScenario.Key());
        dossier = await service.ApproveFsnPublication(dossier.Id, new(dossier.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
        if (boundary == "channel")
        {
            var channel = (await service.Workspace("ortho", SqlFixture.Ra)).ActiveChannel!;
            await service.Deactivate("ortho", new(channel.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        }
        else
        {
            var fsca = await service.GetDossier(dossier.Package.Content.Fsn!.FscaId, SqlFixture.Ra);
            await service.DraftDossier(fsca.Id, new(fsca.Version, "ortho", EudamedDossierKind.FSCA,
                fsca.Package.Content with { ClinicalRationale = Reason + " Neue ausdrueckliche Dokumentfassung." },
                fsca.Package.Sources.Select(s => new EudamedSourceSelection(s.Kind, s.Source.Id, s.Source.Version)).ToArray(), Reason),
                SqlFixture.Pms, CaseScenario.Key());
        }
        var error = await Assert.ThrowsAsync<DomainException>(() => service.PublishFsnDistribution(dossier.Id,
            new(dossier.Version, view.Ledger.Version, dossier.FsnPublication!.ScopeHash, true, Reason),
            SqlFixture.Ra, CaseScenario.Key()));
        Assert.Equal(409, error.Status);
        Assert.Equal(Canonical.Hash(dossier), Canonical.Hash(await service.GetDossier(dossier.Id, SqlFixture.Ra)));
        Assert.Empty((await service.GetDossier(dossier.Id, SqlFixture.Ra)).Distributions);
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("historical")]
    [Trait("Category", "FsnPublication")]
    public async Task FsnPublicationUsesCompleteSharedFeedbackAndRequiresCurrentAndHistoricalScopes(string boundary)
    {
        var (service, first) = await FrozenFsnDossier(false);
        if (boundary == "historical") first = await SetFsnScope(first.Id, ["ortho", "cardio"]);
        var firstView = await ReceiveDossier(service, first, "Comment");
        first = await SetFsnScope(first.Id, boundary == "current" ? ["ortho", "cardio"] : ["ortho"]);
        var second = await FrozenSiblingFsn(service, first);
        var view = await ReceiveDossier(service, second, "Accept");
        Assert.Empty(FsnPublicationRules.Feedback(view.Ledger));
        var feedback = await service.FsnFeedback(second.Id, SqlFixture.Ra);
        Assert.Equal(EudamedContracts.Bind(view.Dossier), feedback.Dossier);
        Assert.Equal(view.Ledger.Id, feedback.Ledger.Id);
        Assert.Equal(view.Ledger.Version, feedback.Ledger.Version);
        Assert.Equal(Canonical.Hash(Assert.Single(FsnPublicationRules.Feedback(firstView.Ledger))),
            Canonical.Hash(Assert.Single(feedback.Feedback)));
        var command = FsnPublicationInput(view) with
        {
            ExpectedLedgerVersion = feedback.Ledger.Version,
            Feedback = feedback.Feedback.Select(f => new FsnFeedbackDecision(f.Id, f.OriginalHash, "NoChangeJustified", Reason)).ToArray()
        };
        var before = Canonical.Hash(view.Dossier);
        var auditBefore = await fixture.Store.Audit(second.Id);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.FsnFeedback(second.Id, SqlFixture.Ra with { Products = ["ortho"] }))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.DraftFsnPublication(second.Id, command, SqlFixture.Pms with { Products = ["ortho"] }, CaseScenario.Key()))).Status);
        Assert.Equal(before, Canonical.Hash(await service.GetDossier(second.Id, SqlFixture.Ra)));
        Assert.Equal(auditBefore.Length, (await fixture.Store.Audit(second.Id)).Length);
        second = await service.DraftFsnPublication(second.Id, command, SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(Canonical.Hash(feedback.Feedback), Canonical.Hash(second.FsnPublication!.Plan.Feedback.Select(f => f.Original).ToArray()));
        Assert.Contains("cardio", second.AccessProducts);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.GetDossier(second.Id, SqlFixture.Ra with { Products = ["ortho"] }))).Status);
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Fact]
    [Trait("Category", "FsnPublication")]
    public async Task FsnHistoricalPublicationReplayAndCopiedDistributionKeepTheRelatedCurrentScope()
    {
        var (service, first) = await FrozenFsnDossier(false);
        first = (await ReceiveDossier(service, first, "Comment")).Dossier;
        var second = await FrozenSiblingFsn(service, first);
        var view = await ReceiveDossier(service, second, "Accept");
        var feedback = await service.FsnFeedback(second.Id, SqlFixture.Pms);
        second = await service.DraftFsnPublication(second.Id, FsnPublicationInput(view) with
        {
            ExpectedLedgerVersion = feedback.Ledger.Version,
            Feedback = feedback.Feedback.Select(f => new FsnFeedbackDecision(f.Id, f.OriginalHash, "NoChangeJustified", Reason)).ToArray()
        }, SqlFixture.Pms, CaseScenario.Key());
        second = await service.ApproveFsnPublication(second.Id, new(second.Version, Reason, null), SqlFixture.Medical, CaseScenario.Key());
        second = await service.ApproveFsnPublication(second.Id, new(second.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
        var command = new EudamedDistributionCommand(second.Version, feedback.Ledger.Version, second.FsnPublication!.ScopeHash, true, Reason);
        var key = CaseScenario.Key();
        var published = await service.PublishFsnDistribution(second.Id, command, SqlFixture.Ra, key);
        var distribution = Assert.Single(published.Distributions);
        var restricted = SqlFixture.Ra with { Products = ["ortho"] };
        var original = await service.ReadDossierOriginal(second.Id, published.Version, distribution.EvidenceHash, restricted);
        await SetFsnScope(first.Id, ["ortho", "cardio"]);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.GetDossier(second.Id, restricted))).Status);
        var next = new DraftEudamedDossierCommand(published.Version, "ortho", EudamedDossierKind.FSN,
            published.Package.Content with { ClinicalRationale = Reason + " Neue synthetische Dokumentfassung nach gesonderter Verteilung." },
            published.Package.Sources.Select(s => new EudamedSourceSelection(s.Kind, s.Source.Id, s.Source.Version)).ToArray(), Reason);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.DraftDossier(second.Id, next, SqlFixture.Pms with { Products = ["ortho"] }, CaseScenario.Key()))).Status);
        second = await service.DraftDossier(second.Id, next, SqlFixture.Pms, CaseScenario.Key());
        Assert.Null((await service.GetDossier(second.Id, restricted)).FsnPublication);
        Assert.DoesNotContain(await service.DossierHistory(second.Id, restricted), d => d.FsnPublication is not null);
        Assert.Contains(await service.DossierHistory(second.Id, SqlFixture.Ra), d => Canonical.Hash(d) == Canonical.Hash(published));
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.PublishFsnDistribution(second.Id, command, restricted, key))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.ReadDossierOriginal(second.Id, published.Version, distribution.EvidenceHash, restricted))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.ReadDossierOriginal(second.Id, second.Version, distribution.EvidenceHash, restricted))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.IntegrationAudit("eudamedDossier", second.Id, restricted))).Status);
        Assert.Contains(await service.IntegrationAudit("eudamedDossier", second.Id, SqlFixture.Ra),
            entry => entry.Action == "FsnPublicationSimulated");
        Assert.Equal(original.Bytes, (await service.ReadDossierOriginal(second.Id, second.Version, distribution.EvidenceHash, SqlFixture.Ra)).Bytes);
        Assert.Equal(0, gateway.NetworkCalls);
    }

    private async Task<(EudamedService Service, EudamedDossier Dossier)> DraftFscaDossier()
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
        return (service, dossier);
    }

    private async Task<(EudamedService Service, EudamedDossier Dossier)> FrozenFsnDossier(bool urgent)
    {
        var (service, fsca) = await DraftFscaDossier();
        fsca = await service.ApproveDossier(fsca.Id, new(fsca.Version, Reason, Reason), SqlFixture.Medical, CaseScenario.Key());
        fsca = await service.ApproveDossier(fsca.Id, new(fsca.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
        fsca = (await ReceiveDossier(service, fsca, "Accept")).Dossier;
        Assert.Contains(EudamedRules.CurrentResponses(fsca),
            response => response.Applied && response.Status == "BusinessAccepted" && !string.IsNullOrWhiteSpace(response.DossierCode));
        var bytes = await fixture.Objects.ReadBytes(Assert.Single(fsca.Package.Attachments).Hash);
        var document = await service.RecordDocument(new("ortho", "synthetic-fsn-byte-binding-fixture.pdf", "application/pdf",
            Convert.ToBase64String(bytes), "SYNTHETISCH: Vorhandene Test-PDF-Bytes pruefen nur den Originalbezug, nicht die klinische FSN-Dokumentgestaltung. " + Reason, Reason),
            SqlFixture.Pms, CaseScenario.Key());
        var content = DossierContent() with { Fsn = new(fsca.Id, fsca.Version,
            "SYNTHETISCH: Testeinrichtungen innerhalb des freigegebenen Laenderscopes", Reason,
            document.OriginalHash, document.Name, "1", "Draft", urgent, "FINAL", DateOnly.FromDateTime(clock.Now.UtcDateTime)) };
        var dossier = await service.DraftDossier(null, new(0, "ortho", EudamedDossierKind.FSN, content,
            [new("eudamedDossier", fsca.Id, fsca.Version), new("eudamedDocument", document.Id, document.Version)], Reason),
            SqlFixture.Pms, CaseScenario.Key());
        dossier = await service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, Reason), SqlFixture.Medical, CaseScenario.Key());
        dossier = await service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
        return (service, dossier);
    }

    private async Task<EudamedDossierView> ReceiveDossier(EudamedService service, EudamedDossier dossier, string scenario)
    {
        var view = await QueueAndProcess(service, dossier, scenario);
        clock.Now = clock.Now.AddSeconds(2);
        if (scenario != "Unknown")
            foreach (var planned in view.Dossier.PlannedResponses.OrderBy(p => p.Sequence).ToArray())
                view = await service.AdvanceSimulator(dossier.Id, new(view.Dossier.Version, planned.Id, false, Reason), SqlFixture.Ra, CaseScenario.Key());
        return view;
    }

    private async Task<EudamedDossier> FrozenSiblingFsn(EudamedService service, EudamedDossier first)
    {
        var dossier = await service.DraftDossier(null, new(0, "ortho", EudamedDossierKind.FSN,
            first.Package.Content with { Title = "SYNTHETISCH: Weiteres FSN-Dossier derselben Meldepflicht",
                Fsn = first.Package.Content.Fsn! with { DocumentVersion = "2" } },
            first.Package.Sources.Select(s => new EudamedSourceSelection(s.Kind, s.Source.Id, s.Source.Version)).ToArray(), Reason),
            SqlFixture.Pms, CaseScenario.Key());
        dossier = await service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, Reason), SqlFixture.Medical, CaseScenario.Key());
        return await service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
    }

    private async Task<EudamedDossier> SetFsnScope(string id, string[] products)
    {
        var dossier = (await fixture.Store.Get<EudamedDossier>("eudamedDossier", id))!;
        return await fixture.Store.Execute<EudamedDossier>(new(id, "eudamedDossier", dossier.ProductId, dossier.Version,
            CaseScenario.Key(), "SyntheticFsnScopeChanged", Reason, SqlFixture.Ra, new { products }, clock.Now),
            current => current! with { Version = current.Version + 1, AccessProducts = products });
    }

    private static DraftFsnPublicationCommand FsnPublicationInput(EudamedDossierView view, string route = "AfterAuthorityFeedback") =>
        new(view.Dossier.Version, view.Ledger.Version, [new("DEMO-IE-001", "IE", "SYNTHETISCH: Testempfaenger")],
            "SimulatedEmail", route, FsnPublicationRules.Feedback(view.Ledger)
                .Select(f => new FsnFeedbackDecision(f.Id, f.OriginalHash, "NoChangeJustified", Reason)).ToArray(),
            Reason, Reason, Reason);

    private static async Task<EudamedDossier> FreezeFsnPublication(EudamedService service, EudamedDossierView view, string route = "AfterAuthorityFeedback")
    {
        var dossier = await service.DraftFsnPublication(view.Dossier.Id, FsnPublicationInput(view, route), SqlFixture.Pms, CaseScenario.Key());
        dossier = await service.ApproveFsnPublication(dossier.Id, new(dossier.Version, Reason, null), SqlFixture.Medical, CaseScenario.Key());
        return await service.ApproveFsnPublication(dossier.Id, new(dossier.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
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
