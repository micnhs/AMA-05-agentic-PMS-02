using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed partial class EudamedConfigurationTests
{
    [Fact]
    public async Task OutboxTransportAndBusinessReceiptAreSeparateAndOnlyExactNativeObligationsComplete()
    {
        var (service, scenario, dossier) = await FrozenMirDossier();
        var frozen = Canonical.Serialize(dossier.Package);
        var native = Canonical.Serialize(scenario.Case.CurrentReport!.FrozenJson);
        var originalDeadlines = scenario.Case.Deadlines;
        var view = await service.DossierView(dossier.Id, SqlFixture.Ra);
        var input = new SubmitEudamedDossierCommand(dossier.Version, view.Ledger.Version, "Accept", Reason);
        var key = CaseScenario.Key();
        view = await service.QueueDossier(dossier.Id, input, SqlFixture.Ra, key);
        Assert.Equal("Queued", Assert.Single(view.Dossier.Attempts).Status);
        Assert.Equal("eudamed_delivery_pending", (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service.Submit(scenario.Case.Id, new(scenario.Case.Version, "Accept", Reason),
                SqlFixture.Ra, CaseScenario.Key()))).Code);
        var queued = (await fixture.Store.PendingEvents(1000)).Single(e =>
            e.Type == "EudamedDeliveryQueued" && e.AggregateId == view.Ledger.Id);
        var worker = new OutboxProcessor(fixture.Store, scenario.Service,
            new KnowledgeSearch(fixture.Store, fixture.Objects, fixture.Store), service);
        Assert.Equal("Handled", (await worker.Process(queued.Id)).State);
        Assert.Equal("Handled", (await worker.Process(queued.Id)).State);
        view = await service.DossierView(dossier.Id, SqlFixture.Ra);
        Assert.Equal("TransportDelivered", Assert.Single(view.Dossier.Attempts).Status);
        var beforeBusiness = (await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id))!;
        Assert.Empty(beforeBusiness.Submissions);
        Assert.Equal(Canonical.Hash(originalDeadlines), Canonical.Hash(beforeBusiness.Deadlines));
        var plan = Assert.Single(view.Dossier.PlannedResponses);
        view = await service.AdvanceSimulator(dossier.Id, new(view.Dossier.Version, plan.Id, false, Reason),
            SqlFixture.Ra, CaseScenario.Key());
        var completed = (await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id))!;
        Assert.Equal("EudamedBusinessAccepted", Assert.Single(completed.Submissions).ReceiverStatus);
        Assert.Equal(native, Canonical.Serialize(completed.CurrentReport!.FrozenJson));
        Assert.Equal(frozen, Canonical.Serialize(view.Dossier.Package));
        Assert.Equal(Canonical.Hash(scenario.Case.Approvals), Canonical.Hash(completed.Approvals));
        Assert.Equal(scenario.Case.FirstAwarenessAt, completed.FirstAwarenessAt);
        Assert.All(originalDeadlines, deadline =>
        {
            var actual = completed.Deadlines.Single(d => d.Id == deadline.Id);
            Assert.Equal(deadline.LegalDue, actual.LegalDue);
            Assert.Equal(deadline.SafetyDue, actual.SafetyDue);
            Assert.Equal(deadline.TriggerAt, actual.TriggerAt);
            Assert.Equal("SimulatedReported", actual.Status);
        });
        var duplicateKey = CaseScenario.Key();
        var duplicateInput = new AdvanceEudamedSimulatorCommand(view.Dossier.Version, plan.Id, true, Reason);
        await service.AdvanceSimulator(dossier.Id, duplicateInput, SqlFixture.Ra, duplicateKey);
        await service.AdvanceSimulator(dossier.Id, duplicateInput, SqlFixture.Ra, duplicateKey);
        var original = Assert.Single((await service.Workspace("ortho", SqlFixture.Ra)).Inbox, i => i.Original?.DossierId == dossier.Id);
        Assert.Equal(1, original.DuplicateReceipts);
        Assert.Equal("Applied", original.Status);
        Assert.Single((await service.DossierView(dossier.Id, SqlFixture.Ra)).Dossier.BusinessResponses);
        Assert.Single((await service.QueueDossier(dossier.Id, input, SqlFixture.Ra, key)).Dossier.Attempts);
        Assert.Single((await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id))!.Submissions);
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Fact]
    public async Task UnknownAttemptCannotBeResentAndLateReplyKeepsItsProfileAfterDeactivation()
    {
        var (service, scenario, dossier) = await FrozenMirDossier();
        var view = await QueueAndProcess(service, dossier, "Unknown");
        var attempt = Assert.Single(view.Dossier.Attempts);
        Assert.Equal("TransmissionUnknown", attempt.Status);
        Assert.Equal("eudamed_delivery_pending", (await Assert.ThrowsAsync<DomainException>(() =>
            service.QueueDossier(dossier.Id, new(view.Dossier.Version, view.Ledger.Version, "Accept", Reason),
                SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal("eudamed_delivery_pending", (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service.Submit(scenario.Case.Id, new(scenario.Case.Version, "Accept", Reason),
                SqlFixture.Ra, CaseScenario.Key()))).Code);
        var active = (await service.Workspace("ortho", SqlFixture.Ra)).ActiveChannel!;
        var disabled = await service.Deactivate("ortho", new(active.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        var reconstructed = Service;
        view = await reconstructed.AdvanceSimulator(dossier.Id, new(view.Dossier.Version,
            Assert.Single(view.Dossier.PlannedResponses).Id, false, Reason), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal(Canonical.Hash(attempt.Binding), Canonical.Hash(Assert.Single(view.Dossier.Attempts).Binding));
        Assert.Equal("TransportDelivered", view.Dossier.Attempts[0].Status);
        Assert.Equal("BusinessAccepted", Assert.Single(view.Dossier.BusinessResponses).Status);
        Assert.Equal(disabled, (await service.Workspace("ortho", SqlFixture.Ra)).ActiveChannel);
        Assert.Single((await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id))!.Submissions);
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Fact]
    public async Task QueuedAttemptIsFencedBeforeClaimAndNeverRetargetsToANewerGeneration()
    {
        var (service, scenario, dossier) = await FrozenMirDossier();
        var view = await service.DossierView(dossier.Id, SqlFixture.Ra);
        view = await service.QueueDossier(dossier.Id, new(dossier.Version, view.Ledger.Version, "Accept", Reason),
            SqlFixture.Ra, CaseScenario.Key());
        var attempt = Assert.Single(view.Dossier.Attempts);
        var active = (await service.Workspace("ortho", SqlFixture.Ra)).ActiveChannel!;
        await service.Deactivate("ortho", new(active.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        await service.ProcessDelivery(view.Ledger.Id, attempt.Id);
        view = await service.DossierView(dossier.Id, SqlFixture.Ra);
        Assert.Equal("Fenced", Assert.Single(view.Dossier.Attempts).Status);
        Assert.Null(view.Dossier.Attempts[0].StartedAt);
        Assert.Equal(attempt.Binding, view.Dossier.Attempts[0].Binding);
        Assert.Empty(view.Dossier.PlannedResponses);
        Assert.Empty((await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id))!.Submissions);
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Fact]
    public async Task OutOfOrderOriginalsRemainVisibleWithoutRegressingTheAcceptedState()
    {
        var (service, scenario, dossier) = await FrozenMirDossier();
        var view = await QueueAndProcess(service, dossier, "OutOfOrder");
        var newest = view.Dossier.PlannedResponses.Single(p => p.Sequence == 2);
        view = await service.AdvanceSimulator(dossier.Id, new(view.Dossier.Version, newest.Id, false, Reason),
            SqlFixture.Ra, CaseScenario.Key());
        var acceptedCase = Canonical.Hash(await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id));
        clock.Now = clock.Now.AddSeconds(2);
        var older = view.Dossier.PlannedResponses.Single(p => p.Sequence == 1);
        view = await service.AdvanceSimulator(dossier.Id, new(view.Dossier.Version, older.Id, false, Reason),
            SqlFixture.Ra, CaseScenario.Key());
        Assert.False(view.Dossier.BusinessResponses.Single(r => r.Sequence == 1).Applied);
        Assert.True(view.Dossier.BusinessResponses.Single(r => r.Sequence == 2).Applied);
        Assert.Equal("BusinessAccepted", Assert.Single(view.Ledger.Entries).BusinessStatus);
        Assert.Equal(acceptedCase, Canonical.Hash(await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id)));
        Assert.Equal(2, (await service.Workspace("ortho", SqlFixture.Ra)).Inbox.Count(i => i.Original?.DossierId == dossier.Id));
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Theory]
    [InlineData("Reject", "BusinessRejected")]
    [InlineData("DraftOnly", "DraftCreated")]
    [InlineData("TechnicalFailure", "NotSent")]
    [InlineData("Delayed", "BusinessAccepted")]
    public async Task NegativeOrDelayedScenariosNeverPretendAnImmediateBusinessAcceptance(string scenarioName, string state)
    {
        var (service, scenario, dossier) = await FrozenMirDossier();
        var view = await QueueAndProcess(service, dossier, scenarioName);
        Assert.Empty((await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id))!.Submissions);
        if (scenarioName == "TechnicalFailure")
        {
            Assert.Equal("TransportFailed", Assert.Single(view.Dossier.Attempts).Status);
            Assert.Empty(view.Dossier.PlannedResponses);
        }
        else
        {
            var planned = Assert.Single(view.Dossier.PlannedResponses);
            if (scenarioName == "Delayed")
            {
                Assert.Equal("eudamed_event_not_due", (await Assert.ThrowsAsync<DomainException>(() =>
                    service.AdvanceSimulator(dossier.Id, new(view.Dossier.Version, planned.Id, false, Reason),
                        SqlFixture.Ra, CaseScenario.Key()))).Code);
                clock.Now = clock.Now.AddSeconds(21);
            }
            view = await service.AdvanceSimulator(dossier.Id, new(view.Dossier.Version, planned.Id, false, Reason),
                SqlFixture.Ra, CaseScenario.Key());
        }
        Assert.Equal(state, Assert.Single(view.Ledger.Entries).BusinessStatus);
        var result = (await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id))!;
        if (state == "BusinessAccepted") Assert.Single(result.Submissions);
        else
        {
            Assert.Empty(result.Submissions);
            Assert.Equal(Canonical.Hash(scenario.Case.Deadlines), Canonical.Hash(result.Deadlines));
        }
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Fact]
    public async Task NativeAndEudamedCommandsCannotBothClaimTheSameCurrentSource()
    {
        var (service, scenario, dossier) = await FrozenMirDossier();
        var view = await service.DossierView(dossier.Id, SqlFixture.Ra);
        async Task<string> Queue()
        {
            try
            {
                await service.QueueDossier(dossier.Id, new(dossier.Version, view.Ledger.Version, "Accept", Reason),
                    SqlFixture.Ra, CaseScenario.Key());
                return "eudamed";
            }
            catch (DomainException error) when (error.Code is "dependency_version" or "eudamed_delivery_pending" or "version_conflict")
            { return error.Code; }
        }
        async Task<string> Native()
        {
            try
            {
                await scenario.Service.Submit(scenario.Case.Id, new(scenario.Case.Version, "Accept", Reason), SqlFixture.Ra, CaseScenario.Key());
                return "legacy";
            }
            catch (DomainException error) when (error.Code is "dependency_version" or "eudamed_delivery_pending" or "version_conflict")
            { return error.Code; }
        }
        var outcomes = await Task.WhenAll(Queue(), Native());
        Assert.Single(outcomes, o => o is "eudamed" or "legacy");
        var final = await service.DossierView(dossier.Id, SqlFixture.Ra);
        var native = (await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id))!;
        Assert.Equal(1, final.Ledger.Entries.Length + native.Submissions.Length);
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Fact]
    public async Task TwoApprovedDossiersShareOneReservationAndChangedSourcesCannotQueue()
    {
        var (service, scenario, first) = await FrozenMirDossier();
        var second = await FreezeMir(service, scenario);
        var view = await service.DossierView(first.Id, SqlFixture.Ra);
        await service.QueueDossier(first.Id, new(first.Version, view.Ledger.Version, "Accept", Reason), SqlFixture.Ra, CaseScenario.Key());
        var reserved = await service.DossierView(second.Id, SqlFixture.Ra);
        Assert.Equal("eudamed_delivery_pending", (await Assert.ThrowsAsync<DomainException>(() =>
            service.QueueDossier(second.Id, new(second.Version, reserved.Ledger.Version, "Accept", Reason),
                SqlFixture.Ra, CaseScenario.Key()))).Code);
        await scenario.Assess();
        var attempt = (await service.DossierView(first.Id, SqlFixture.Ra)).Dossier.Attempts.Single();
        await service.ProcessDelivery(view.Ledger.Id, attempt.Id);
        var fenced = (await service.DossierView(first.Id, SqlFixture.Ra)).Dossier.Attempts.Single();
        Assert.Equal("Fenced", fenced.Status);
        Assert.Equal("eudamed_source_changed", fenced.ErrorCode);
        Assert.Empty((await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id))!.Submissions);
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DraftOnlyOutOfOrderNeverReceiptsNativeObligationsEvenWithAContradictoryLateAcceptance(bool newestFirst)
    {
        var (service, scenario, dossier) = await FrozenMirDossier("DraftOnly");
        var native = Canonical.Hash(scenario.Case);
        var signatures = Canonical.Hash(dossier.Approvals);
        var frozen = await fixture.Objects.Read(dossier.FrozenPackageHash!);
        var view = await QueueAndProcess(service, dossier, "OutOfOrder");
        clock.Now = clock.Now.AddSeconds(2);
        var ordered = newestFirst ? view.Dossier.PlannedResponses.OrderByDescending(p => p.Sequence)
            : view.Dossier.PlannedResponses.OrderBy(p => p.Sequence);
        foreach (var planned in ordered)
        {
            Assert.Equal("DraftCreated", Canonical.Read<EudamedInboxOriginal>(planned.Content).Status);
            view = await service.AdvanceSimulator(dossier.Id, new(view.Dossier.Version, planned.Id, false, Reason),
                SqlFixture.Ra, CaseScenario.Key());
            Assert.Equal(native, Canonical.Hash(await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id)));
        }
        Assert.Equal("DraftCreated", Assert.Single(view.Ledger.Entries).BusinessStatus);
        var received = (await service.Workspace("ortho", SqlFixture.Ra)).Inbox.Where(i => i.Original?.DossierId == dossier.Id).ToArray();
        Assert.Equal(2, received.Length);
        foreach (var inbox in received)
            Assert.Equal(inbox.OriginalHash, Canonical.HashText(await fixture.Objects.Read(inbox.OriginalHash)));
        var contrary = received[0].Original! with { MessageId = "contradictory-" + CaseScenario.Key(),
            Sequence = 3, Status = "BusinessAccepted" };
        var original = Canonical.Serialize(contrary);
        var hash = await fixture.Objects.Put(original);
        var id = "eu-inbox-test-" + CaseScenario.Key();
        var attempt = Assert.Single(view.Dossier.Attempts);
        await fixture.Store.Execute<EudamedInbox>(new(id, "eudamedInbox", "ortho", 0, CaseScenario.Key(),
            "EudamedInboxStored", Reason, SqlFixture.Ra, new { hash }, clock.Now), _ =>
                new(id, 1, "ortho", attempt.Binding, contrary, hash, null, contrary.MessageId, "Stored",
                    "NotApplicable", null, clock.Now, null));
        await service.ApplyInbox(id);
        await service.ProjectDelivery(view.Ledger.Id);
        view = await service.DossierView(dossier.Id, SqlFixture.Ra);
        Assert.Equal("ActionMismatch", Assert.Single(view.Ledger.Entries).BusinessStatus);
        Assert.True(EudamedRules.Pending(view.Ledger.Entries[0]));
        var conflicting = view.Dossier.BusinessResponses.Single(r => r.InboxId == id);
        Assert.Equal("ActionMismatch", conflicting.Status);
        Assert.False(conflicting.Applied);
        Assert.Equal("BusinessAccepted", (await fixture.Store.Get<EudamedInbox>("eudamedInbox", id))!.Original!.Status);
        Assert.Equal(original, await fixture.Objects.Read(hash));
        Assert.Equal(native, Canonical.Hash(await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id)));
        Assert.Equal(signatures, Canonical.Hash(view.Dossier.Approvals));
        Assert.Equal(frozen, await fixture.Objects.Read(view.Dossier.FrozenPackageHash!));
        Assert.Equal(0, gateway.NetworkCalls);
    }

    [Fact]
    public async Task DraftCreationNeedsANewExplicitApprovedSubmitPackageBeforeNativeReceipt()
    {
        var (service, scenario, dossier) = await FrozenMirDossier("DraftOnly");
        var view = await QueueAndProcess(service, dossier, "Accept");
        view = await service.AdvanceSimulator(dossier.Id, new(view.Dossier.Version,
            Assert.Single(view.Dossier.PlannedResponses).Id, false, Reason), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal("DraftCreated", Assert.Single(view.Ledger.Entries).BusinessStatus);
        Assert.Equal(Canonical.Hash(scenario.Case), Canonical.Hash(await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id)));
        var submitted = await service.DraftDossier(dossier.Id, new(view.Dossier.Version, "ortho", EudamedDossierKind.MIR,
            dossier.Package.Content with { IntendedAction = "Submit" }, [new("case", scenario.Case.Id, scenario.Case.Version)],
            Reason), SqlFixture.Pms, CaseScenario.Key());
        Assert.Empty(submitted.Approvals);
        Assert.NotEqual(dossier.ScopeHash, submitted.ScopeHash);
        submitted = await service.ApproveDossier(dossier.Id, new(submitted.Version, Reason, Reason), SqlFixture.Medical, CaseScenario.Key());
        submitted = await service.ApproveDossier(dossier.Id, new(submitted.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
        view = await service.DossierView(dossier.Id, SqlFixture.Ra);
        view = await service.QueueDossier(dossier.Id, new(submitted.Version, view.Ledger.Version, "Accept", Reason),
            SqlFixture.Ra, CaseScenario.Key());
        var attempt = view.Dossier.Attempts.Single(a => a.DossierRevision == submitted.Revision);
        await service.ProcessDelivery(view.Ledger.Id, attempt.Id);
        view = await service.DossierView(dossier.Id, SqlFixture.Ra);
        var plan = view.Dossier.PlannedResponses.Single(p => p.AttemptId == attempt.Id);
        view = await service.AdvanceSimulator(dossier.Id, new(view.Dossier.Version, plan.Id, false, Reason),
            SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal("DraftCreated", view.Ledger.Entries.Single(e => e.IntendedAction == "DraftOnly").BusinessStatus);
        Assert.Equal("BusinessAccepted", view.Ledger.Entries.Single(e => e.IntendedAction == "Submit").BusinessStatus);
        Assert.Equal(attempt.Id, Assert.Single((await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id))!.Submissions).Id);
        Assert.Equal(dossier.ScopeHash, Canonical.HashText(await fixture.Objects.Read(dossier.FrozenPackageHash!)));
        Assert.Equal(0, gateway.NetworkCalls);
    }

    private async Task<(EudamedService Service, CaseScenario Scenario, EudamedDossier Dossier)> FrozenMirDossier(string action = "Submit")
    {
        var service = Service;
        await ActivateSimulator(service);
        var scenario = await ApprovedMir();
        return (service, scenario, await FreezeMir(service, scenario, action));
    }

    private static async Task<EudamedDossier> FreezeMir(EudamedService service, CaseScenario scenario, string action = "Submit")
    {
        var dossier = await service.DraftDossier(null, new(0, "ortho", EudamedDossierKind.MIR, DossierContent() with { IntendedAction = action },
            [new("case", scenario.Case.Id, scenario.Case.Version)], Reason), SqlFixture.Pms, CaseScenario.Key());
        dossier = await service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, Reason), SqlFixture.Medical, CaseScenario.Key());
        return await service.ApproveDossier(dossier.Id, new(dossier.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
    }

    private static async Task<EudamedDossierView> QueueAndProcess(EudamedService service, EudamedDossier dossier, string scenario)
    {
        var view = await service.DossierView(dossier.Id, SqlFixture.Ra);
        view = await service.QueueDossier(dossier.Id, new(dossier.Version, view.Ledger.Version, scenario, Reason),
            SqlFixture.Ra, CaseScenario.Key());
        await service.ProcessDelivery(view.Ledger.Id, Assert.Single(view.Dossier.Attempts).Id);
        return await service.DossierView(dossier.Id, SqlFixture.Ra);
    }
}
