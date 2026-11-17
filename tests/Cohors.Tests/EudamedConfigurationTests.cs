using Cohors.Api;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

[Collection("SQL pilot")]
public sealed class EudamedConfigurationTests(SqlFixture fixture)
{
    private const string Reason = "SYNTHETISCH: Der konkrete Teststand wird mit Originalbezug und getrennter menschlicher Verantwortung geprueft. Keine amtliche Registrierung, reale Uebermittlung oder implizite Risikoakzeptanz.";
    private readonly TestClock clock = new(DateTimeOffset.Parse("2026-11-16T15:00:00Z"));
    private readonly NoNetworkGateway gateway = new();
    private EudamedService Service => new(fixture.Store, fixture.Objects, fixture.Policies, gateway,
        new EudamedComposer(), new PsurExportService(fixture.Store, clock), clock);

    [Fact]
    public async Task ActorRoundsArePersistentCorrelatedAndWorkerReplayDoesNotDuplicate()
    {
        var service = Service;
        var application = await service.SaveActor(null, new(0, "ortho", Details(), Reason), SqlFixture.Pms, CaseScenario.Key());
        foreach (var scenario in new[] { "Correction", "Reject", "Approve" })
        {
            if (application.Status != "Draft")
                application = await service.SaveActor(application.Id, new(application.Version, "ortho",
                    application.Details with { Address = application.Details.Address + " / reviewed" }, Reason), SqlFixture.Pms, CaseScenario.Key());
            var submitted = await service.SubmitActor(application.Id, new(application.Version, scenario, Reason), SqlFixture.Ra, CaseScenario.Key());
            var dispatch = (await fixture.Store.PendingEvents(1000)).Single(e =>
                e.Type == "EudamedActorSubmitted" && e.AggregateId == submitted.Id && e.Version == submitted.Version);
            var processor = new OutboxProcessor(fixture.Store, fixture.Service(clock),
                new KnowledgeSearch(fixture.Store, fixture.Objects, fixture.Store), service);
            Assert.Equal("Handled", (await processor.Process(dispatch.Id)).State);
            application = (await fixture.Store.Get<EudamedActorApplication>("eudamedActor", submitted.Id))!;
            var audit = await fixture.Store.Audit(application.Id);
            Assert.Equal("Handled", (await processor.Process(dispatch.Id)).State);
            Assert.Equal(audit.Length, (await fixture.Store.Audit(application.Id)).Length);
            Assert.Equal(submitted.Version, application.Rounds.Last().Revision);
            Assert.Equal(Canonical.Hash(application.Details), application.Rounds.Last().ApplicationHash);
            if (scenario != "Approve") Assert.Null(application.SimulatedIdentity);
        }
        Assert.Equal("SimulatedApproved", application.Status);
        Assert.Equal(6, application.Rounds.Length);
        Assert.StartsWith("DEMO-EUDAMED-MF-", application.SimulatedIdentity);
        Assert.Equal(9, (await fixture.Store.History<EudamedActorApplication>("eudamedActor", ["ortho"]))
            .Count(a => a.Id == application.Id));
        Assert.Contains(await service.IntegrationAudit("eudamedActor", application.Id, SqlFixture.Ra),
            e => e.Action == "EudamedActorSimulationResponded");
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Fact]
    public async Task ActiveProfileIsSeparateFromDraftAndOnlyCurrentHumanRaMaySwitch()
    {
        var service = Service;
        var (application, references) = await RegisteredReferences(service);
        var before = await service.Workspace("ortho", SqlFixture.Ra);
        var profile = Simulator(application, references);
        var draft = await service.SaveConfiguration(new(before.Draft?.Version ?? 0, "ortho", profile, Reason), SqlFixture.Ra, CaseScenario.Key());
        var activation = new ActivateEudamedConfigurationCommand(before.ActiveChannel?.Version ?? 0, draft.Version, true, Reason);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.Activate("ortho", activation, SqlFixture.Pms, CaseScenario.Key()))).Status);
        Assert.Equal("eudamed_impact_confirmation", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Activate("ortho", activation with { ConfirmImpact = false }, SqlFixture.Ra, CaseScenario.Key()))).Code);
        var active = await service.Activate("ortho", activation, SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal((before.ActiveChannel?.DispatchGeneration ?? 0) + 1, active.DispatchGeneration);
        Assert.True((await service.Workspace("ortho", SqlFixture.Ra)).ActiveReadiness.CanDispatch);
        var emptyBackend = new DomibusProfile("", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "");
        var externalDraft = profile with { Mode = EudamedMode.Domibus, Environment = EudamedEnvironment.Playground,
            ActorId = "", ActorVersion = 0, ReferencesId = "", ReferencesVersion = 0, Domibus = emptyBackend };
        var domibus = await service.SaveConfiguration(new(draft.Version, "ortho", externalDraft, Reason), SqlFixture.Ra, CaseScenario.Key());
        var split = await service.Workspace("ortho", SqlFixture.Ra);
        Assert.Equal(EudamedMode.Simulator, split.EffectiveMode);
        Assert.Equal(EudamedMode.Domibus, split.Draft!.Profile.Mode);
        Assert.Equal(active, split.ActiveChannel);
        Assert.False(split.DraftReadiness!.CanActivate);
        Assert.Equal("eudamed_not_ready", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Activate("ortho", new(active.Version, domibus.Version, true, Reason), SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal("domibus_unknown_reference", (await Assert.ThrowsAsync<DomainException>(() =>
            service.SaveConfiguration(new(domibus.Version, "ortho", externalDraft with
                { Domibus = emptyBackend with { SecretReference = "unregistered-credential-value", SecretVersion = "1" } }, Reason),
                SqlFixture.Ra, CaseScenario.Key()))).Code);
        var disabled = await service.Deactivate("ortho", new(active.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        Assert.False(disabled.Enabled);
        Assert.Equal(active.DispatchGeneration + 1, disabled.DispatchGeneration);
        Assert.Equal("version_conflict", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Deactivate("ortho", new(active.Version, Reason), SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Fact]
    public async Task ActorCorrectionInvalidatesReadinessWithoutRewritingBoundReferencesOrBypassingScope()
    {
        var service = Service;
        var (application, references) = await RegisteredReferences(service);
        var originalReferences = Canonical.Hash(references);
        var workspace = await service.Workspace("ortho", SqlFixture.Ra);
        var draft = await service.SaveConfiguration(new(workspace.Draft?.Version ?? 0, "ortho", Simulator(application, references), Reason),
            SqlFixture.Ra, CaseScenario.Key());
        var active = await service.Activate("ortho", new(workspace.ActiveChannel?.Version ?? 0, draft.Version, true, Reason),
            SqlFixture.Ra, CaseScenario.Key());
        await service.SaveActor(application.Id, new(application.Version, "ortho", application.Details with
            { Contact = application.Details.Contact with { FamilyName = "Corrected" } }, Reason), SqlFixture.Pms, CaseScenario.Key());
        var blocked = await service.Workspace("ortho", SqlFixture.Ra);
        Assert.False(blocked.ActiveReadiness.CanDispatch);
        Assert.Equal(active, blocked.ActiveChannel);
        Assert.Equal(originalReferences, Canonical.Hash(blocked.References.Single(r => r.Id == references.Id)));
        var foreign = SqlFixture.Ra with { Products = ["cardio"] };
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.Workspace("ortho", foreign))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.IntegrationAudit("eudamedActor", application.Id, foreign))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.SubmitActor(application.Id, new(application.Version + 1, "Approve", Reason),
                SqlFixture.Ra with { IsHuman = false }, CaseScenario.Key()))).Status);
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Fact]
    public async Task ActorEditCannotCreateAnArbitraryCrossAggregateIdentifier()
    {
        var id = "eudamed-channel-forged-" + Guid.NewGuid().ToString("N");
        Assert.Equal("eudamed_actor_missing", (await Assert.ThrowsAsync<DomainException>(() =>
            Service.SaveActor(id, new(0, "ortho", Details(), Reason), SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Null(await fixture.Store.Get<EudamedActorApplication>("eudamedActor", id));
        Assert.Empty(await fixture.Store.Audit(id));
    }

    private async Task<(EudamedActorApplication, EudamedReferences)> RegisteredReferences(EudamedService service)
    {
        var application = await service.SaveActor(null, new(0, "ortho", Details(), Reason), SqlFixture.Pms, CaseScenario.Key());
        var submitted = await service.SubmitActor(application.Id, new(application.Version, "Approve", Reason), SqlFixture.Ra, CaseScenario.Key());
        application = await service.ProcessActor(submitted.Id, submitted.Version);
        var governance = (await fixture.Store.Get<ProductGovernance>("productGovernance", "ortho"))!;
        var previous = await fixture.Store.Get<EudamedReferences>("eudamedReferences", "eudamed-references-ortho");
        var references = await service.MapReferences(new(previous?.Version ?? 0, "ortho", application.Id, application.Version,
            governance.Version, "synthetic-1", new(2025, 1, 1), new(2030, 12, 31), Reason, "DEMO-NB-TEST", "GS1", Reason),
            SqlFixture.Ra, CaseScenario.Key());
        return (application, references);
    }

    private static EudamedActorDetails Details() => new("DEMO test manufacturer", "MF", "IE",
        "SYNTHETISCH: Test address, no real organisation",
        new("Demo", "Safety", "contact@example.invalid", "000000"), true);

    private static EudamedProfile Simulator(EudamedActorApplication application, EudamedReferences references) =>
        new(EudamedMode.Simulator, EudamedEnvironment.Simulation, application.Id, application.Version,
            references.Id, references.Version, "DEMO-CA-IE", EudamedContracts.Business, EudamedContracts.Gateway, null);

    private sealed class NoNetworkGateway : IDomibusGateway
    {
        public int NetworkCalls { get; private set; }
        public string[] AllowedEndpointReferences => [];
        public DomibusReferenceCatalog ReferenceCatalog => new([], [], [], []);
        public DomibusAvailability Availability(DomibusProfile profile) => new(false, ["No configured backend."], "None");
        private Exception Forbidden() { NetworkCalls++; return new InvalidOperationException("No network operation is permitted in this actor/configuration test."); }
        public Task Check(DomibusProfile profile, CancellationToken ct = default) => throw Forbidden();
        public Task<DomibusSubmitReceipt> Submit(DomibusProfile profile, DomibusMessage message, CancellationToken ct = default) => throw Forbidden();
        public Task<DomibusStatusResult> Status(DomibusProfile profile, string messageId, string accessPointRole, CancellationToken ct = default) => throw Forbidden();
        public Task<DomibusErrorsResult> Errors(DomibusProfile profile, string messageId, string accessPointRole, CancellationToken ct = default) => throw Forbidden();
        public Task<string[]> Pending(DomibusProfile profile, CancellationToken ct = default) => throw Forbidden();
        public Task<DomibusRetrievedMessage> Retrieve(DomibusProfile profile, string messageId, CancellationToken ct = default) => throw Forbidden();
        public Task<byte[]> ConfirmDownload(DomibusProfile profile, string messageId, CancellationToken ct = default) => throw Forbidden();
    }
}
