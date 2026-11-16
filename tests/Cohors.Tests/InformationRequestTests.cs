using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Cohors.Tests;

public sealed class InformationRequestCompatibilityTests
{
    [Fact]
    public void HistoricalRequestRoundTripsWithoutInventingRecipientOrDispatchProof()
    {
        const string original = """{"approvals":[],"disclosureBasis":"Historical disclosure basis","dueAt":"2026-11-20T15:00:00\u002B00:00","id":"old-request","manufacturerBasis":"Historical manufacturer basis","minimalData":"Historical minimum","question":"Historical question","recipientRole":"Historical role only","relevance":"Historical relevance","response":null,"scopeHash":"unchanged-original-scope","simulated":true,"status":"SimulatedSent"}""";
        var request = Canonical.Read<InformationRequest>(original);
        Assert.Equal(original, Canonical.Serialize(request));
        Assert.Null(request.Lifecycle);
        Assert.False(InformationRules.HasConfirmedDelivery(request));
        Assert.Equal("request_legacy", Assert.Throws<DomainException>(() => InformationRules.Scope(request)).Code);
        var oldCommand = new RequestInformationCommand(1, "Question", "Minimum", "Relevance", "Role",
            "Manufacturer", "Disclosure", DateTimeOffset.Parse("2026-11-20T15:00:00Z"));
        Assert.DoesNotContain("recipient\"", Canonical.Serialize(oldCommand));
        Assert.DoesNotContain("envelope", Canonical.Serialize(new RespondInformationCommand(1, "old-request", "Original", "Reason")));
    }
}

[Collection("SQL pilot")]
[Trait("Category", "Sql")]
public sealed class InformationRequestTests(SqlFixture fixture)
{
    private const string Reason = "SYNTHETISCH: Konkrete Anfrage, Ursprungsbefund, Produkt und Kontakt getrennt geprueft; keine reale Kommunikation oder Kausalitaetsfreigabe.";
    private const string Text = "  SYNTHETISCHER ORIGINALBEFUND:\r\nMechanische Lockerung beschrieben; Ursache und Befundqualitaet bleiben separat zu pruefen.  ";
    private static readonly InformationRecipient Recipient = new("synthetic-contact", "Synthetic clinic", "Synthetic findings desk",
        "Clinical safety contact", "Email", "findings@example.invalid", true);
    private static string Key() => CaseScenario.Key();

    private static RequestInformationCommand Draft(CaseScenario s) => new(s.Case.Version,
        "Welche direkten Befunde stuetzen oder widerlegen die konkurrierenden Ursachen?",
        "Nur relevante synthetische Befundausschnitte; keine Vollakte oder Identifikatoren.", Reason,
        Recipient.Role, "Nur synthetische Pilotverarbeitung; reale Rechtsgrundlagen separat pruefen.",
        "Nur synthetische Offenlegung; keine reale Klinikkommunikation.", s.Clock.Now.AddDays(7), Recipient);

    private static InformationRequest Current(CaseScenario s, string id) => s.Case.InformationRequests.Single(r => r.Id == id);

    private async Task<InformationRequest> Approved(CaseScenario s)
    {
        s.Case = await s.Service.AddInformationRequest(s.Case.Id, Draft(s), SqlFixture.Pms, Key());
        var id = s.Case.InformationRequests.Last().Id;
        s.Case = await s.Service.ApproveInformation(s.Case.Id, id, new(s.Case.Version, Reason, null), SqlFixture.Medical, Key());
        s.Case = await s.Service.ApproveInformation(s.Case.Id, id, new(s.Case.Version, Reason, null), SqlFixture.Ra, Key());
        return Current(s, id);
    }

    private async Task<SafetyCase> ProcessQueued(CaseScenario s)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        using var select = new SqlCommand("SELECT Id FROM dbo.Outbox WHERE AggregateId=@id AND Version=@version AND Type='InformationDispatchQueued'", connection);
        select.Parameters.AddWithValue("@id", s.Case.Id);
        select.Parameters.AddWithValue("@version", s.Case.Version);
        var eventId = Assert.IsType<string>(await select.ExecuteScalarAsync());
        var processor = new OutboxProcessor(fixture.Store, s.Service, new KnowledgeSearch(fixture.Store, fixture.Objects, fixture.Store));
        Assert.Equal("Handled", (await processor.Process(eventId)).State);
        var completed = await s.Service.Get(s.Case.Id, SqlFixture.Pms);
        Assert.True(await fixture.Store.HasReceipt(eventId, "cohors-domain-events-v1"));
        Assert.Equal("Handled", (await processor.Process(eventId)).State);
        Assert.Equal(completed.Version, (await s.Service.Get(s.Case.Id, SqlFixture.Pms)).Version);
        return completed;
    }

    private async Task<InformationRequest> Delivered(CaseScenario s, string? requestId = null, string scenario = "Accept")
    {
        var request = requestId is null ? await Approved(s) : Current(s, requestId);
        s.Case = await s.Service.DispatchInformation(s.Case.Id, request.Id, new(s.Case.Version, scenario, Reason), SqlFixture.Pms, Key());
        s.Case = await ProcessQueued(s);
        return Current(s, request.Id);
    }

    private static RespondInformationCommand Reply(CaseScenario s, InformationRequest request) =>
        new(s.Case.Version, request.Id, Text, Reason, new("reply-" + Key(), s.Case.Id, s.Case.ProductId,
            request.Lifecycle!.CorrelationId, Recipient, "Synthetischer Originalbefund aus expliziter Testquelle.", true));

    private static MapInformationResponseCommand Map(CaseScenario s, InformationRequest request, bool confirmMissing = false) =>
        new(s.Case.Version, request.Id, request.Lifecycle!.CorrelationId, Canonical.Hash(request.Lifecycle.Recipient), confirmMissing, Reason);

    [Fact]
    public async Task ApprovalIsNotDeliveryAndTheOutboxProducesOneBoundReceiptWithoutClockChanges()
    {
        var s = new CaseScenario(fixture); await s.Create();
        var original = s.Case;
        var request = await Approved(s);
        Assert.Equal("Approved", request.Status);
        Assert.Empty(request.Lifecycle!.Dispatches);
        Assert.False(InformationRules.HasConfirmedDelivery(request));
        Assert.NotEqual(request.Approvals[0].ActorId, request.Approvals[1].ActorId);
        Assert.All(request.Approvals, a => Assert.Equal(request.ScopeHash, a.ScopeHash));
        var premature = Reply(s, request);
        s.Case = await s.Service.RespondInformation(s.Case.Id, premature, SqlFixture.Pms, Key());
        var held = Assert.Single(s.Case.InformationResponses!);
        Assert.Equal("Held", held.Status);
        Assert.Empty(held.CandidateRequestIds);
        Assert.Equal(original.Evidence.Length, s.Case.Evidence.Length);

        var command = new DispatchInformationCommand(s.Case.Version, "Accept", Reason);
        var key = Key();
        s.Case = await s.Service.DispatchInformation(s.Case.Id, request.Id, command, SqlFixture.Pms, key);
        Assert.Equal("SendPending", Current(s, request.Id).Status);
        var pending = Assert.Single(Current(s, request.Id).Lifecycle!.Dispatches);
        Assert.Null(pending.AttemptedAt);
        Assert.Null(pending.ReceiptReference);
        Assert.Equal(s.Case.Version, (await s.Service.DispatchInformation(s.Case.Id, request.Id, command, SqlFixture.Pms, key)).Version);
        s.Case = await ProcessQueued(s);
        request = Current(s, request.Id);
        var sent = Assert.Single(request.Lifecycle!.Dispatches);
        Assert.Equal("SimulatedSent", request.Status);
        Assert.True(InformationRules.ReceiptMatches(request, sent));
        Assert.Equal(sent.SimulatorReceipt!.Reference, sent.ReceiptReference);
        Assert.Equal(Canonical.Hash(Recipient), sent.RecipientHash);
        Assert.Equal(Canonical.Serialize(original.Deadlines), Canonical.Serialize(s.Case.Deadlines));
        Assert.Equal(Canonical.Serialize(original.Awareness), Canonical.Serialize(s.Case.Awareness));
        Assert.Empty(s.Case.AgentRuns);
        Assert.Empty(s.Case.Submissions);
        var audit = await fixture.Store.Audit(s.Case.Id);
        Assert.Single(audit, a => a.Action == "InformationDispatchCompleted");
        Assert.Equal(s.Case.Version, audit.Length);
        Assert.Equal(s.Case.Version, (await fixture.Store.History<SafetyCase>("case", ["ortho"])).Count(c => c.Id == s.Case.Id));
    }

    [Theory]
    [InlineData("Fail")]
    [InlineData("Unknown")]
    public async Task FailureAndUnknownRemainDistinctAndUnknownCannotBeBlindlyRetried(string scenario)
    {
        var s = new CaseScenario(fixture); await s.Create();
        var request = await Delivered(s, scenario: scenario);
        var attempt = Assert.Single(request.Lifecycle!.Dispatches);
        Assert.Null(attempt.ReceiptReference);
        Assert.False(InformationRules.HasConfirmedDelivery(request));
        if (scenario == "Fail")
        {
            Assert.Equal("SendFailed", request.Status);
            Assert.Null(attempt.SimulatorReceipt);
            request = await Delivered(s, request.Id);
            Assert.Equal(2, request.Lifecycle!.Dispatches.Length);
            Assert.Equal("SendFailed", request.Lifecycle.Dispatches[0].Status);
            Assert.Equal("SimulatedSent", request.Lifecycle.Dispatches[1].Status);
            Assert.All(request.Lifecycle.Dispatches, d => Assert.Equal(attempt.CorrelationId, d.CorrelationId));
        }
        else
        {
            Assert.Equal("TransmissionUnknown", request.Status);
            Assert.Equal("request_delivery_gate", (await Assert.ThrowsAsync<DomainException>(() =>
                s.Service.DispatchInformation(s.Case.Id, request.Id, new(s.Case.Version, "Accept", Reason), SqlFixture.Ra, Key()))).Code);
            Assert.Equal("request_dispatch_missing", (await Assert.ThrowsAsync<DomainException>(() =>
                s.Service.ReconcileInformation(s.Case.Id, request.Id, "wrong-attempt", new(s.Case.Version, Reason), SqlFixture.Ra, Key()))).Code);
            s.Case = await s.Service.ReconcileInformation(s.Case.Id, request.Id, attempt.Id, new(s.Case.Version, Reason), SqlFixture.Ra, Key());
            request = Current(s, request.Id);
            Assert.Single(request.Lifecycle!.Dispatches);
            Assert.True(InformationRules.HasConfirmedDelivery(request));
        }
    }

    [Fact]
    public async Task ExactReplyIsPreservedThenMappedAsUnconfirmedEvidenceAndReviewedSeparately()
    {
        var s = new CaseScenario(fixture); await s.Create(); await s.Assess(); await s.Prepare(); await s.Approve();
        var original = s.Case;
        var request = await Delivered(s);
        var input = Reply(s, request);
        var receiveKey = Key();
        s.Case = await s.Service.RespondInformation(s.Case.Id, input, SqlFixture.Pms, receiveKey);
        var receivedVersion = s.Case.Version;
        var response = Assert.Single(s.Case.InformationResponses!);
        Assert.Equal("Received", response.Status);
        Assert.Null(response.Mapping);
        Assert.Equal(original.Evidence.Length, s.Case.Evidence.Length);
        Assert.Equal(Text, await fixture.Objects.Read(response.ContentHash));
        Assert.Equal(Canonical.Serialize(new { input.RequestId, input.Response, input.Envelope }),
            await fixture.Objects.Read(response.OriginalHash));
        Assert.Equal(ReportState.Frozen, s.Case.CurrentReport!.State);
        Assert.Equal(receivedVersion, (await s.Service.RespondInformation(s.Case.Id, input, SqlFixture.Pms, receiveKey)).Version);
        Assert.Equal(receivedVersion, (await s.Service.RespondInformation(s.Case.Id, input with { ExpectedVersion = receivedVersion },
            SqlFixture.Pms, Key())).Version);
        Assert.Equal("response_revision", (await Assert.ThrowsAsync<DomainException>(() => s.Service.RespondInformation(s.Case.Id,
            input with { ExpectedVersion = receivedVersion, Response = Text + "Anderer Inhalt." }, SqlFixture.Pms, Key()))).Code);

        var map = Map(s, request);
        var mapKey = Key();
        s.Case = await s.Service.MapInformationResponse(s.Case.Id, response.Id, map, SqlFixture.Pms, mapKey);
        var mappedVersion = s.Case.Version;
        response = Assert.Single(s.Case.InformationResponses!);
        Assert.Equal("Mapped", response.Status);
        Assert.Equal(request.Id, response.Mapping!.RequestId);
        Assert.False(response.Mapping.MissingReferencesConfirmed);
        var evidence = Assert.Single(s.Case.Evidence, e => e.Id == response.Mapping.EvidenceId);
        Assert.False(evidence.Approved);
        Assert.Equal(Text, evidence.Content);
        Assert.Equal(CaseStage.AwaitingMedicalReview, s.Case.Stage);
        Assert.Equal(ReportState.Superseded, s.Case.CurrentReport!.State);
        Assert.Equal(original.CurrentReport!.FrozenJson, s.Case.CurrentReport.FrozenJson);
        Assert.Equal(Canonical.Serialize(original.Approvals), Canonical.Serialize(s.Case.Approvals));
        Assert.Equal(Canonical.Serialize(original.Deadlines), Canonical.Serialize(s.Case.Deadlines));
        Assert.Equal(original.FirstAwarenessAt, s.Case.FirstAwarenessAt);
        Assert.Equal(original.AgentRuns.Length, s.Case.AgentRuns.Length);
        Assert.Equal(mappedVersion, (await s.Service.MapInformationResponse(s.Case.Id, response.Id, map, SqlFixture.Pms, mapKey)).Version);
        Assert.Equal("response_mapped", (await Assert.ThrowsAsync<DomainException>(() => s.Service.MapInformationResponse(s.Case.Id,
            response.Id, Map(s, request), SqlFixture.Medical, Key()))).Code);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => s.Service.ReviewInformationResponse(s.Case.Id,
            response.Id, new(s.Case.Version, Reason), SqlFixture.Pms, Key()))).Status);

        s.Case = await s.Service.ReviewInformationResponse(s.Case.Id, response.Id, new(s.Case.Version, Reason), SqlFixture.Medical, Key());
        response = Assert.Single(s.Case.InformationResponses!);
        Assert.Equal(SqlFixture.Medical.Id, response.Review!.ActorId);
        var reviewed = Assert.Single(s.Case.Evidence, e => e.Id == evidence.Id);
        Assert.True(reviewed.Approved);
        Assert.Equal(2, reviewed.Version);
        Assert.Equal(evidence.Hash, reviewed.Hash);
        Assert.Equal(evidence.ObjectKey, reviewed.ObjectKey);
        Assert.Equal(0, InformationRules.PendingResponses(s.Case));
        var history = await fixture.Store.History<SafetyCase>("case", ["ortho"]);
        Assert.False(history.Single(c => c.Id == s.Case.Id && c.Version == mappedVersion).Evidence.Single(e => e.Id == evidence.Id).Approved);
        Assert.Contains(await fixture.Store.ObjectDependencies(response.OriginalHash, "unrelated-retention"),
            dependency => dependency.Kind == "case" && dependency.Id == s.Case.Id);
        Assert.Empty(s.Case.Submissions);
    }

    [Fact]
    public async Task MissingReferencesStayHeldAndHumanMappingSelectsTheFirstNotTheLatestRequest()
    {
        var s = new CaseScenario(fixture); await s.Create();
        var first = await Delivered(s);
        var second = await Delivered(s);
        var input = Reply(s, first);
        input = input with { RequestId = "", Envelope = input.Envelope! with { CorrelationId = null } };
        s.Case = await s.Service.RespondInformation(s.Case.Id, input, SqlFixture.Pms, Key());
        var response = Assert.Single(s.Case.InformationResponses!);
        Assert.Equal("Held", response.Status);
        Assert.Equal(new[] { first.Id, second.Id }, response.CandidateRequestIds);
        Assert.Equal("response_confirmation", (await Assert.ThrowsAsync<DomainException>(() => s.Service.MapInformationResponse(s.Case.Id,
            response.Id, Map(s, first), SqlFixture.Medical, Key()))).Code);
        s.Case = await s.Service.MapInformationResponse(s.Case.Id, response.Id, Map(s, first, true), SqlFixture.Medical, Key());
        response = Assert.Single(s.Case.InformationResponses!);
        Assert.Equal(first.Id, response.Mapping!.RequestId);
        Assert.True(response.Mapping.MissingReferencesConfirmed);
        Assert.Empty(response.DeclaredRequestId);
        Assert.Null(response.Envelope.CorrelationId);
        Assert.Equal("SimulatedSent", Current(s, second.Id).Status);
        Assert.Equal("SimulatedAnswered", Current(s, first.Id).Status);
    }

    [Theory]
    [InlineData("case")]
    [InlineData("product")]
    [InlineData("request")]
    [InlineData("correlation")]
    [InlineData("sender")]
    public async Task ConflictingOriginalBindingsAreHeldAndCannotBeOverriddenByAMappingFlag(string field)
    {
        var s = new CaseScenario(fixture); await s.Create();
        var request = await Delivered(s);
        var input = Reply(s, request);
        input = field switch
        {
            "case" => input with { Envelope = input.Envelope! with { CaseId = "another-case" } },
            "product" => input with { Envelope = input.Envelope! with { ProductId = "cardio" } },
            "request" => input with { RequestId = "another-request" },
            "correlation" => input with { Envelope = input.Envelope! with { CorrelationId = "another-correlation" } },
            _ => input with { Envelope = input.Envelope! with { Sender = Recipient with { Destination = "another@example.invalid" } } }
        };
        s.Case = await s.Service.RespondInformation(s.Case.Id, input, SqlFixture.Pms, Key());
        var response = Assert.Single(s.Case.InformationResponses!);
        Assert.Equal("Held", response.Status);
        Assert.Empty(response.CandidateRequestIds);
        Assert.Equal("response_binding", (await Assert.ThrowsAsync<DomainException>(() => s.Service.MapInformationResponse(s.Case.Id,
            response.Id, Map(s, request, true), SqlFixture.Medical, Key()))).Code);
        Assert.Null((await s.Service.Get(s.Case.Id, SqlFixture.Pms)).InformationResponses!.Single().Mapping);
    }

    [Fact]
    public async Task RecipientRoleIdentityProductAndExpectedVersionAreEnforcedServerSide()
    {
        var s = new CaseScenario(fixture); await s.Create();
        var draft = Draft(s);
        Assert.Equal("request_recipient", (await Assert.ThrowsAsync<DomainException>(() => s.Service.AddInformationRequest(s.Case.Id,
            draft with { Recipient = null }, SqlFixture.Pms, Key()))).Code);
        Assert.Equal("synthetic_only", (await Assert.ThrowsAsync<DomainException>(() => s.Service.AddInformationRequest(s.Case.Id,
            draft with { Recipient = Recipient with { Synthetic = false } }, SqlFixture.Pms, Key()))).Code);
        var foreign = SqlFixture.Medical with { Id = "cardio-only", Products = ["cardio"] };
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => s.Service.AddInformationRequest(s.Case.Id,
            draft, foreign, Key()))).Status);
        s.Case = await s.Service.AddInformationRequest(s.Case.Id, draft, SqlFixture.Pms, Key());
        var id = s.Case.InformationRequests.Last().Id;
        Assert.Equal("medical_required", (await Assert.ThrowsAsync<DomainException>(() => s.Service.ApproveInformation(s.Case.Id,
            id, new(s.Case.Version, Reason, null), SqlFixture.Ra, Key()))).Code);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => s.Service.ApproveInformation(s.Case.Id,
            id, new(s.Case.Version, Reason, null), SqlFixture.Pms, Key()))).Status);
        var stale = s.Case.Version;
        s.Case = await s.Service.ApproveInformation(s.Case.Id, id, new(stale, Reason, null), SqlFixture.Medical, Key());
        Assert.Equal("request_gate", (await Assert.ThrowsAsync<DomainException>(() => s.Service.ApproveInformation(s.Case.Id, id,
            new(s.Case.Version, Reason, null), SqlFixture.Ra with { Id = SqlFixture.Medical.Id }, Key()))).Code);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => s.Service.ApproveInformation(s.Case.Id,
            id, new(stale, Reason, null), SqlFixture.Ra, Key()))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => s.Service.DispatchInformation(s.Case.Id,
            id, new(s.Case.Version, "Accept", Reason), SqlFixture.Quality, Key()))).Status);
        var request = Current(s, id);
        var changed = request with { Lifecycle = request.Lifecycle! with { Recipient = Recipient with { Contact = "Another contact" } } };
        Assert.Equal("request_scope", Assert.Throws<DomainException>(() => InformationRules.RequireBound(s.Case, changed)).Code);
        var input = Reply(s, request);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => s.Service.RespondInformation(s.Case.Id,
            input, foreign, Key()))).Status);
    }
}
