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
        var queued = await OutboxId("eudamedDelivery", view.Ledger.Id, Assert.Single(view.Dossier.Attempts).QueuedVersion);
        var worker = new OutboxProcessor(fixture.Store, scenario.Service,
            new KnowledgeSearch(fixture.Store, fixture.Objects, fixture.Store), service);
        Assert.Equal("Handled", (await worker.Process(queued)).State);
        Assert.Equal("Handled", (await worker.Process(queued)).State);
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
    public async Task ExpectedAbsentReservationDependencyRejectsAConcurrentCreationWithoutChangingTheTarget()
    {
        var sourceId = "eu-absence-" + CaseScenario.Key();
        var targetId = "eu-guarded-" + CaseScenario.Key();
        var context = new CommandContext(targetId, "eudamedDelivery", "ortho", 0, CaseScenario.Key(),
            "EudamedDependencyProbe", Reason, SqlFixture.Ra, new { targetId }, clock.Now)
            { Dependencies = [new("eudamedDelivery", new(sourceId, 0, ""))] };
        var target = await fixture.Store.Execute<EudamedDeliveryLedger>(context,
            _ => new(targetId, 1, "ortho", "synthetic-guard", []));
        await fixture.Store.Execute<EudamedDeliveryLedger>(new(sourceId, "eudamedDelivery", "ortho", 0,
            CaseScenario.Key(), "EudamedReservationCreated", Reason, SqlFixture.Ra, new { sourceId }, clock.Now),
            _ => new(sourceId, 1, "ortho", "synthetic-reservation", []));
        var next = context with { ExpectedVersion = target.Version, IdempotencyKey = CaseScenario.Key() };
        Assert.Equal("dependency_version", (await Assert.ThrowsAsync<DomainException>(() =>
            fixture.Store.Execute<EudamedDeliveryLedger>(next, current => current! with { Version = current.Version + 1 }))).Code);
        Assert.Equal(Canonical.Hash(target), Canonical.Hash(await fixture.Store.Get<EudamedDeliveryLedger>("eudamedDelivery", targetId)));
        Assert.Single(await fixture.Store.Audit(targetId));
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

    [Theory]
    [InlineData("Comment")]
    [InlineData("InformationRequest")]
    public async Task AcceptedMirCorrespondenceNeedsNewReplyApprovalsWithoutResubmittingNativeObligations(string kind)
    {
        var timing = TestPhaseTiming.FromEnvironment(kind);
        timing.Record(CorrespondencePhase.CaseStarted);
        var (service, scenario, original) = await FrozenMirDossier();
        timing.Record(CorrespondencePhase.DossierBuilt);
        var parent = await QueueAndProcess(service, original, kind);
        timing.Record(CorrespondencePhase.OriginalTransportCompleted);
        var responseIndex = 0;
        foreach (var planned in parent.Dossier.PlannedResponses.OrderBy(p => p.Sequence))
        {
            var firstAcceptance = responseIndex++ == 0;
            timing.Record(firstAcceptance ? CorrespondencePhase.FirstAcceptanceStarted : CorrespondencePhase.CorrespondenceProcessingStarted);
            clock.Now = planned.DueAt;
            parent = await service.AdvanceSimulator(original.Id, new(parent.Dossier.Version, planned.Id, false, Reason),
                SqlFixture.Ra, CaseScenario.Key());
            timing.Record(firstAcceptance ? CorrespondencePhase.FirstAcceptanceCompleted : CorrespondencePhase.CorrespondenceProcessingCompleted);
        }
        var correspondence = Assert.Single(parent.Dossier.Correspondence);
        var originalBytes = await fixture.Objects.Read(correspondence.OriginalHash);
        var native = (await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id))!;
        var nativeHash = Canonical.Hash(native);
        var nativeAudit = Canonical.Hash(await fixture.Store.Audit(native.Id));
        Assert.Single(native.Submissions);
        timing.Record(CorrespondencePhase.OriginalChecksCompleted);
        var content = parent.Dossier.Package.Content with { IntendedAction = "Reply", ParentId = original.Id,
            ParentVersion = parent.Dossier.Version, ReplyToCorrespondenceId = correspondence.Id,
            Title = "SYNTHETISCH: Quellengebundene Antwort auf " + kind,
            ClinicalRationale = Reason + " Die Antwort behandelt die bezeichnete Originalrueckfrage, nicht eine neue Erstmeldung." };
        var selections = new EudamedSourceSelection[] { new("case", native.Id, native.Version),
            new("eudamedDossier", parent.Dossier.Id, parent.Dossier.Version) };
        timing.Record(CorrespondencePhase.ReplyDraftStarted);
        var reply = await service.DraftDossier(null, new(0, "ortho", EudamedDossierKind.MIR,
            content, selections, Reason), SqlFixture.Pms, CaseScenario.Key());
        timing.Record(CorrespondencePhase.ReplyDraftCompleted);
        Assert.Empty(reply.Approvals);
        var draft = await service.DossierView(reply.Id, SqlFixture.Ra);
        Assert.Equal("open_findings", (await Assert.ThrowsAsync<DomainException>(() =>
            service.QueueDossier(reply.Id, new(reply.Version, draft.Ledger.Version, "Accept", Reason),
                SqlFixture.Ra, CaseScenario.Key()))).Code);
        timing.Record(CorrespondencePhase.MedicalApprovalStarted);
        reply = await service.ApproveDossier(reply.Id, new(reply.Version, Reason, Reason),
            SqlFixture.Medical, CaseScenario.Key());
        timing.Record(CorrespondencePhase.MedicalApprovalCompleted);
        Assert.Equal("eudamed_approval", (await Assert.ThrowsAsync<DomainException>(() =>
            service.QueueDossier(reply.Id, new(reply.Version, draft.Ledger.Version, "Accept", Reason),
                SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal("four_eyes", (await Assert.ThrowsAsync<DomainException>(() =>
            service.ApproveDossier(reply.Id, new(reply.Version, Reason, null),
                SqlFixture.Medical with { Roles = [Roles.Medical, Roles.Regulatory] }, CaseScenario.Key()))).Code);
        timing.Record(CorrespondencePhase.RegulatoryApprovalStarted);
        reply = await service.ApproveDossier(reply.Id, new(reply.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
        timing.Record(CorrespondencePhase.RegulatoryApprovalCompleted);
        Assert.All(reply.Approvals, approval => Assert.DoesNotContain(original.Approvals, old => old.Id == approval.Id));
        timing.Record(CorrespondencePhase.ReplyQueueTransportStarted);
        var view = await QueueAndProcess(service, reply, "Accept");
        timing.Record(CorrespondencePhase.ReplyQueueTransportCompleted);
        Assert.Null(Assert.Single(view.Ledger.Entries).CaseObligation);
        Assert.Equal("Open", Assert.Single((await service.DossierView(original.Id, SqlFixture.Ra)).Dossier.Correspondence).Status);
        Assert.Equal(nativeHash, Canonical.Hash(await fixture.Store.Get<SafetyCase>("case", native.Id)));
        var response = Assert.Single(view.Dossier.PlannedResponses);
        timing.Record(CorrespondencePhase.ReplyAcceptanceStarted);
        view = await service.AdvanceSimulator(reply.Id, new(view.Dossier.Version, response.Id, false, Reason),
            SqlFixture.Ra, CaseScenario.Key());
        timing.Record(CorrespondencePhase.ReplyAcceptanceCompleted);
        timing.Record(CorrespondencePhase.FinalAssertionsStarted);
        var completed = await service.DossierView(original.Id, SqlFixture.Ra);
        var answered = Assert.Single(completed.Dossier.Correspondence);
        Assert.Equal("Replied", answered.Status);
        Assert.Equal(reply.Id, answered.ReplyDossierId);
        Assert.Equal(Canonical.Hash(correspondence), Canonical.Hash(answered with { Status = "Open", ReplyDossierId = null }));
        Assert.Equal(Canonical.Hash(original.Package), Canonical.Hash(completed.Dossier.Package));
        Assert.Equal(Canonical.Hash(original.Approvals), Canonical.Hash(completed.Dossier.Approvals));
        Assert.Equal(originalBytes, await fixture.Objects.Read(correspondence.OriginalHash));
        Assert.Equal(nativeHash, Canonical.Hash(await fixture.Store.Get<SafetyCase>("case", native.Id)));
        Assert.Equal(nativeAudit, Canonical.Hash(await fixture.Store.Audit(native.Id)));
        Assert.Equal("BusinessAccepted", Assert.Single(view.Ledger.Entries).BusinessStatus);
        timing.Record(CorrespondencePhase.DuplicateReceiptStarted);
        await service.AdvanceSimulator(reply.Id, new(view.Dossier.Version, response.Id, true, Reason),
            SqlFixture.Ra, CaseScenario.Key());
        timing.Record(CorrespondencePhase.DuplicateReceiptCompleted);
        Assert.Equal(Canonical.Hash(completed.Dossier), Canonical.Hash(await service.GetDossier(original.Id, SqlFixture.Ra)));
        Assert.Equal(nativeHash, Canonical.Hash(await fixture.Store.Get<SafetyCase>("case", native.Id)));
        timing.Record(CorrespondencePhase.ResubmissionGuardStarted);
        Assert.Equal("already_submitted", (await Assert.ThrowsAsync<DomainException>(() =>
            service.QueueDossier(original.Id, new(completed.Dossier.Version, completed.Ledger.Version, "Accept", Reason),
                SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal(0, gateway.NetworkCalls);
        timing.Record(CorrespondencePhase.CaseCompleted);
    }

    [Fact]
    [Trait("Category", "OperationsSqlBoundary")]
    public async Task CorrespondenceWatchdogDeduplicatesAndCalendarClosesOnlyTheBusinessAcceptedReplyTarget()
    {
        var (service, scenario, original) = await FrozenMirDossier();
        var parent = await QueueAndProcess(service, original, "InformationRequest");
        foreach (var planned in parent.Dossier.PlannedResponses.OrderBy(p => p.Sequence))
        {
            clock.Now = planned.DueAt;
            parent = await service.AdvanceSimulator(original.Id, new(parent.Dossier.Version, planned.Id, false, Reason),
                SqlFixture.Ra, CaseScenario.Key());
        }
        var first = Assert.Single(parent.Dossier.Correspondence);
        var originalBytes = await fixture.Objects.ReadBytes(first.OriginalHash);
        var firstInbox = (await fixture.Store.Get<EudamedInbox>("eudamedInbox", first.InboxId))!;
        var secondOriginal = firstInbox.Original! with { MessageId = "second-question-" + CaseScenario.Key(),
            Sequence = 3, Content = Reason + " Zweite unabhaengige Originalrueckfrage.", DueAt = first.DueAt!.Value.AddDays(1) };
        var secondInboxId = "eu-inbox-" + Canonical.Hash(new { firstInbox.Binding.Profile.Environment,
            firstInbox.Binding.ProfileHash, messageId = secondOriginal.MessageId });
        var secondHash = await fixture.Objects.Put(Canonical.Serialize(secondOriginal));
        await fixture.Store.Execute<EudamedInbox>(new(secondInboxId, "eudamedInbox", "ortho", 0, CaseScenario.Key(),
            "SyntheticCorrespondenceOriginalReceived", Reason, SqlFixture.Ra, secondOriginal, clock.Now),
            _ => new(secondInboxId, 1, "ortho", firstInbox.Binding, secondOriginal, secondHash, null,
                secondOriginal.MessageId, "Stored", "NotApplicable", null, clock.Now, null));
        await service.ApplyInbox(secondInboxId);
        parent = await service.DossierView(original.Id, SqlFixture.Ra);
        var second = Assert.Single(parent.Dossier.Correspondence, c => c.InboxId == secondInboxId);
        var packageHash = Canonical.Hash(parent.Dossier.Package);
        var signatures = Canonical.Hash(parent.Dossier.Approvals);
        var nativeBefore = (await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id))!;
        clock.Now = first.DueAt.Value.AddMinutes(1);
        var restricted = await fixture.RestrictedOperations();
        var operations = new OperationsService(restricted, restricted, clock, restricted);
        await operations.Tick();
        var ledger = (await fixture.Store.Get<EudamedDeliveryLedger>("eudamedDelivery", parent.Ledger.Id))!;
        var alert = Assert.Single(ledger.CorrespondenceAlerts!);
        Assert.Equal(first.Id, alert.CorrespondenceId);
        Assert.Equal(first.OriginalHash, alert.OriginalHash);
        Assert.Equal(Roles.Regulatory, alert.OwnerRole);
        Assert.Equal("Overdue", alert.Code);
        var ledgerHash = Canonical.Hash(ledger);
        await operations.Tick();
        Assert.Equal(ledgerHash, Canonical.Hash(await fixture.Store.Get<EudamedDeliveryLedger>("eudamedDelivery", ledger.Id)));
        Assert.Single(await fixture.Store.Audit(ledger.Id), e => e.Action == "EudamedCorrespondenceEscalated");
        Assert.Equal(Canonical.Hash(first), Canonical.Hash((await service.GetDossier(original.Id, SqlFixture.Ra))
            .Correspondence.Single(c => c.Id == first.Id)));
        var reader = new WorkspaceReadService(fixture.Store, clock);
        var month = new DateOnly(clock.Now.Year, clock.Now.Month, 1);
        async Task<CalendarEvent[]> Events() => (await reader.Calendar(SqlFixture.Ra, "ortho", month, "UTC",
            "EudamedReply", "all", null, 0)).Events.Where(e => e.EntityId == original.Id).ToArray();
        var before = await Events();
        Assert.Equal(2, before.Length);
        Assert.Equal("Overdue", before.Single(e => e.Correspondence!.CorrespondenceId == first.Id).Timing);
        Assert.All(before, e => Assert.True(e.IsOpen));
        Assert.DoesNotContain((await reader.Calendar(SqlFixture.Ra with { Products = ["cardio"] }, null, month, "UTC",
            "EudamedReply", "all", null, 0)).Events, e => e.EntityId == original.Id);
        var native = (await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id))!;
        Assert.Equal(Canonical.Hash(nativeBefore.Deadlines), Canonical.Hash(native.Deadlines));
        Assert.Equal(Canonical.Hash(nativeBefore.Approvals), Canonical.Hash(native.Approvals));
        var nativeHash = Canonical.Hash(native);
        parent = await service.DossierView(original.Id, SqlFixture.Ra);
        var reply = await service.DraftDossier(null, new(0, "ortho", EudamedDossierKind.MIR,
            parent.Dossier.Package.Content with { IntendedAction = "Reply", ParentId = original.Id,
                ParentVersion = parent.Dossier.Version, ReplyToCorrespondenceId = first.Id },
            [new("case", native.Id, native.Version), new("eudamedDossier", original.Id, parent.Dossier.Version)], Reason),
            SqlFixture.Pms, CaseScenario.Key());
        reply = await service.ApproveDossier(reply.Id, new(reply.Version, Reason, Reason), SqlFixture.Medical, CaseScenario.Key());
        reply = await service.ApproveDossier(reply.Id, new(reply.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
        var view = await service.DossierView(reply.Id, SqlFixture.Ra);
        view = await service.QueueDossier(reply.Id, new(reply.Version, view.Ledger.Version, "Accept", Reason),
            SqlFixture.Ra, CaseScenario.Key());
        Assert.All(await Events(), e => Assert.True(e.IsOpen));
        await service.ProcessDelivery(view.Ledger.Id, Assert.Single(view.Dossier.Attempts).Id);
        Assert.All(await Events(), e => Assert.True(e.IsOpen));
        view = await service.DossierView(reply.Id, SqlFixture.Ra);
        Assert.Equal("TransportDelivered", Assert.Single(view.Dossier.Attempts).Status);
        view = await service.AdvanceSimulator(reply.Id, new(view.Dossier.Version,
            Assert.Single(view.Dossier.PlannedResponses).Id, false, Reason), SqlFixture.Ra, CaseScenario.Key());
        var after = await Events();
        var completed = after.Single(e => e.Correspondence!.CorrespondenceId == first.Id);
        Assert.False(completed.IsOpen);
        Assert.Equal("RecordedClosed", completed.Timing);
        Assert.Equal(first.DueAt, completed.DueAt);
        Assert.Equal(first.OriginalHash, completed.Correspondence!.OriginalHash);
        Assert.True(after.Single(e => e.Correspondence!.CorrespondenceId == second.Id).IsOpen);
        await operations.Tick();
        Assert.Single((await fixture.Store.Get<EudamedDeliveryLedger>("eudamedDelivery", ledger.Id))!.CorrespondenceAlerts!);
        Assert.Single(await fixture.Store.Audit(ledger.Id), e => e.Action == "EudamedCorrespondenceEscalated");
        var final = await service.GetDossier(original.Id, SqlFixture.Ra);
        Assert.Equal(packageHash, Canonical.Hash(final.Package));
        Assert.Equal(signatures, Canonical.Hash(final.Approvals));
        Assert.Equal(originalBytes, await fixture.Objects.ReadBytes(first.OriginalHash));
        Assert.Equal(nativeHash, Canonical.Hash(await fixture.Store.Get<SafetyCase>("case", native.Id)));
        Assert.Equal(0, gateway.NetworkCalls);
        await fixture.Store.Manifest(ledger.Id, kind: "eudamedDelivery");
    }

    [Fact]
    public async Task PsrpCorrespondenceReplyKeepsTheAcceptedPsrOriginalAndExactPeriodWithoutNativeChanges()
    {
        var service = Service;
        await ActivateSimulator(service);
        var scenario = new CaseScenario(fixture);
        scenario.Case = await scenario.Service.Register(scenario.Input with
        {
            OccurredAt = DateTimeOffset.Parse("2026-10-11T10:00:00Z"),
            AwarenessAt = DateTimeOffset.Parse("2026-10-12T10:00:00Z")
        }, SqlFixture.Pms, CaseScenario.Key());
        await scenario.Respond();
        await scenario.Assess();
        await scenario.Prepare();
        scenario.Case = await scenario.Regulatory.AttachMir(scenario.Case.Id, new(scenario.Case.Version,
            scenario.MirFacts() with { EventDateFrom = new(2026, 10, 11), EventDateTo = new(2026, 10, 11) }, Reason),
            SqlFixture.Medical, CaseScenario.Key());
        await scenario.Approve();
        var evidence = (await fixture.Store.List<Evidence>("evidence", ["ortho"])).Single(e => e.Kind == "RiskFile");
        var agreement = await scenario.Regulatory.RecordSummaryAgreement(new(0, "ortho", "DEMO-PSRP-" + CaseScenario.Key(),
            "HPRA-SIMULATOR", "EU-IE", "de", SummaryReportingRules.ReportContract, Reason, Reason, 61,
            new(2026, 9, 1), new(2026, 12, 31), DateTimeOffset.Parse("2026-08-31T10:00:00Z"),
            "CommonAndWellDocumented", Reason, evidence.Id, evidence.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        var periodic = await scenario.Regulatory.DraftSummary(new(0, agreement.Id, agreement.Version,
            new(2026, 9, 1), new(2026, 10, 31), [new(scenario.Case.Id, scenario.Case.Version, Reason)],
            new(Reason, Reason, Reason, Reason), Reason), SqlFixture.Pms, CaseScenario.Key());
        periodic = await scenario.Regulatory.ApproveSummary(periodic.Id, new(periodic.Version, Reason), SqlFixture.Medical, CaseScenario.Key());
        periodic = await scenario.Regulatory.ApproveSummary(periodic.Id, new(periodic.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        var nativeHash = Canonical.Hash(scenario.Case);
        var nativeAudit = Canonical.Hash(await fixture.Store.Audit(scenario.Case.Id));
        var periodicHash = Canonical.Hash(periodic);
        async Task<EudamedDossier> Freeze(EudamedDossierKind kind, EudamedDossierContent content, EudamedSourceSelection[] sources)
        {
            var value = await service.DraftDossier(null, new(0, "ortho", kind, content, sources, Reason), SqlFixture.Pms, CaseScenario.Key());
            value = await service.ApproveDossier(value.Id, new(value.Version, Reason, Reason), SqlFixture.Medical, CaseScenario.Key());
            return await service.ApproveDossier(value.Id, new(value.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
        }
        async Task<EudamedDossierView> Receive(EudamedDossier value, string outcome)
        {
            var received = await QueueAndProcess(service, value, outcome);
            foreach (var planned in received.Dossier.PlannedResponses.OrderBy(p => p.Sequence))
            {
                clock.Now = planned.DueAt;
                received = await service.AdvanceSimulator(value.Id, new(received.Dossier.Version, planned.Id, false, Reason),
                    SqlFixture.Ra, CaseScenario.Key());
            }
            return received;
        }
        var psr = await Freeze(EudamedDossierKind.PSR, DossierContent() with
            { PsrTerms = new(2, periodic.Start, Reason + " Zwei ausdrueckliche Kalendermonate, keine Umrechnung von Tagen.") },
            [new("summaryAgreement", agreement.Id, agreement.Version)]);
        psr = (await Receive(psr, "Accept")).Dossier;
        var psrHash = Canonical.Hash(psr);
        var psrBytes = await fixture.Objects.ReadBytes(psr.FrozenPackageHash!);
        var psrp = await Freeze(EudamedDossierKind.PSRP, DossierContent() with
            { Period = new(periodic.Start, periodic.End, Reason), ParentId = psr.Id, ParentVersion = psr.Version },
            [new("summaryReport", periodic.Id, periodic.Version), new("eudamedDossier", psr.Id, psr.Version)]);
        var originalPackage = Canonical.Hash(psrp.Package);
        var originalApprovals = Canonical.Hash(psrp.Approvals);
        var psrpBytes = await fixture.Objects.ReadBytes(psrp.FrozenPackageHash!);
        psrp = (await Receive(psrp, "InformationRequest")).Dossier;
        var target = Assert.Single(psrp.Correspondence);
        var originalCorrespondence = await fixture.Objects.ReadBytes(target.OriginalHash);
        var contentReply = psrp.Package.Content with { IntendedAction = "Reply", ParentId = psrp.Id,
            ParentVersion = psrp.Version, ReplyToCorrespondenceId = target.Id, ClinicalRationale = Reason + " Originalgebundene Antwort." };
        var selections = new EudamedSourceSelection[] { new("summaryReport", periodic.Id, periodic.Version),
            new("eudamedDossier", psrp.Id, psrp.Version) };
        Assert.Equal("eudamed_period_source", (await Assert.ThrowsAsync<DomainException>(() =>
            service.DraftDossier(null, new(0, "ortho", EudamedDossierKind.PSRP,
                contentReply with { Period = contentReply.Period! with { Start = periodic.Start.AddDays(1) } },
                selections, Reason), SqlFixture.Pms, CaseScenario.Key()))).Code);
        var reply = await service.DraftDossier(null, new(0, "ortho", EudamedDossierKind.PSRP,
            contentReply, selections, Reason), SqlFixture.Pms, CaseScenario.Key());
        Assert.Empty(reply.Approvals);
        Assert.Empty(reply.Package.Validation.Errors);
        var xml = System.Xml.Linq.XDocument.Parse(reply.Package.BusinessXml);
        Assert.Equal(Assert.Single(psr.BusinessResponses).DossierCode,
            xml.Descendants().Single(e => e.Name.LocalName == "psr_ref").Value);
        Assert.Equal(Assert.Single(psrp.BusinessResponses).DossierCode,
            xml.Root!.Elements().Single(e => e.Name.LocalName == "DossierCode").Value);
        Assert.Equal(psrHash, Canonical.Hash(EudamedRules.PsrAgreement(reply.Package.Content, reply.Package.Sources)));
        Assert.Equal(contentReply.Period, reply.Package.Content.Period);
        reply = await service.ApproveDossier(reply.Id, new(reply.Version, Reason, Reason), SqlFixture.Medical, CaseScenario.Key());
        reply = await service.ApproveDossier(reply.Id, new(reply.Version, Reason, null), SqlFixture.Ra, CaseScenario.Key());
        Assert.All(reply.Approvals, approval => Assert.DoesNotContain(psrp.Approvals, old => old.Id == approval.Id));
        var accepted = await Receive(reply, "Accept");
        Assert.Equal("BusinessAccepted", Assert.Single(accepted.Ledger.Entries).BusinessStatus);
        Assert.Null(Assert.Single(accepted.Ledger.Entries).CaseObligation);
        Assert.Equal(periodic.Start, Assert.Single(accepted.Dossier.BusinessResponses).ReturnedPeriod!.Start);
        Assert.Equal(periodic.End, Assert.Single(accepted.Dossier.BusinessResponses).ReturnedPeriod!.End);
        var completed = await service.GetDossier(psrp.Id, SqlFixture.Ra);
        var answered = Assert.Single(completed.Correspondence);
        Assert.Equal("Replied", answered.Status);
        Assert.Equal(reply.Id, answered.ReplyDossierId);
        Assert.Equal(Canonical.Hash(target), Canonical.Hash(answered with { Status = "Open", ReplyDossierId = null }));
        Assert.Equal(originalPackage, Canonical.Hash(completed.Package));
        Assert.Equal(originalApprovals, Canonical.Hash(completed.Approvals));
        Assert.Equal(psrpBytes, await fixture.Objects.ReadBytes(completed.FrozenPackageHash!));
        Assert.Equal(psrHash, Canonical.Hash(await service.GetDossier(psr.Id, SqlFixture.Ra)));
        Assert.Equal(psrBytes, await fixture.Objects.ReadBytes(psr.FrozenPackageHash!));
        Assert.Equal(originalCorrespondence, await fixture.Objects.ReadBytes(answered.OriginalHash));
        Assert.Equal(periodicHash, Canonical.Hash(await fixture.Store.Get<PeriodicSummaryReport>("summaryReport", periodic.Id)));
        Assert.Equal(nativeHash, Canonical.Hash(await fixture.Store.Get<SafetyCase>("case", scenario.Case.Id)));
        Assert.Equal(nativeAudit, Canonical.Hash(await fixture.Store.Audit(scenario.Case.Id)));
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
