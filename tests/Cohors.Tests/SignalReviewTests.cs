using Cohors.Application;
using Cohors.Domain;
using Microsoft.Data.SqlClient;

namespace Cohors.Tests;

[Collection("SQL pilot")]
public sealed class SignalReviewTests(SqlFixture db)
{
    private const string Reason = "SYNTHETISCH: Quellenrelevanz und fehlende Produktzuordnung begruendet pruefen; keine Nichtmeldepflichtentscheidung oder Fristaenderung.";
    private static Actor Reviewer(string role) => role == Roles.Medical ? SqlFixture.Medical : SqlFixture.Ra;
    private SignalReviewService Service(CaseScenario s) => new(db.Store, db.Objects, s.Clock);
    private static SignalDecisionCommand Command(int version, string source) => new(version, source, Reason);

    [Fact]
    public async Task Actual_document_import_binds_original_history_receipt_audit_outbox_and_explicit_medical_promotion()
    {
        var scenario = new CaseScenario(db);
        await scenario.Create();
        var before = scenario.Case;
        var count = (await db.Store.List<SafetyCase>("case", ["ortho"])).Length;
        var sourceService = new PublicSourceService(db.Store, db.Objects, new DocumentNoNetwork(), scenario.Clock, db.Policies);
        var input = SignalDocumentTests.Input("SQL-DOCUMENT-" + Guid.NewGuid().ToString("N")) with
        { FirstKnownAt = scenario.Clock.GetUtcNow().AddDays(-4),
            ComparisonCaseId = before.Id, ComparisonCaseVersion = before.Version, ComparisonReason = Reason };
        var key = CaseScenario.Key();
        var imported = await sourceService.ImportDocument(input, SqlFixture.Pms, key);
        var replay = await sourceService.ImportDocument(input, SqlFixture.Pms, key);
        Assert.True(replay.Reused);
        Assert.Equal(Canonical.Hash(imported.Source), Canonical.Hash(replay.Source));
        Assert.Equal(Convert.FromBase64String(input.ContentBase64),
            (await sourceService.ReadDocument(imported.Source.Id, 1, SqlFixture.Medical)).Bytes);
        Assert.Equal("SignalDocumentImported", Assert.Single(await db.Store.Audit(imported.Source.Id)).Action);
        await using (var connection = await db.Store.Open())
        {
            using var query = new SqlCommand("""
                SELECT a.Json,h.Json,h.Hash,o.Payload,e.PayloadHash,c.Result
                FROM dbo.Aggregates a
                JOIN dbo.AggregateVersions h ON h.Kind=a.Kind AND h.Id=a.Id AND h.Version=a.Version
                JOIN dbo.Outbox o ON o.AggregateKind=a.Kind AND o.AggregateId=a.Id AND o.Version=a.Version
                JOIN dbo.AuditEvents e ON e.AggregateId=a.Id AND e.Version=a.Version
                JOIN dbo.Commands c ON c.Result=a.Json
                WHERE a.Kind='source' AND a.Id=@id
                """, connection);
            query.Parameters.AddWithValue("@id", imported.Source.Id);
            await using var rows = await query.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(Canonical.Serialize(imported.Source), rows.GetString(0));
            Assert.Equal(rows.GetString(0), rows.GetString(1));
            Assert.Equal(Canonical.Hash(imported.Source), rows.GetString(2));
            Assert.Equal(rows.GetString(0), rows.GetString(3));
            Assert.Equal(rows.GetString(2), rows.GetString(4));
            Assert.Equal(rows.GetString(0), rows.GetString(5));
            Assert.False(await rows.ReadAsync());
        }
        var relevance = await Service(scenario).Decide("source", imported.Source.Id, "ConfirmRelevance",
            new(1, input.ExternalId, Reason), SqlFixture.Ra, CaseScenario.Key());
        var evidence = await sourceService.Approve(imported.Source.Id,
            new(relevance.OwnerVersion, input.ExternalId, Reason), SqlFixture.Medical, CaseScenario.Key(), default);
        Assert.Equal(imported.Source.Items[0].Summary, evidence.Content);
        Assert.Equal(imported.Source.Document!.Text.Hash, evidence.Hash);
        Assert.Equal(Canonical.Hash(before), Canonical.Hash(await scenario.Service.Get(before.Id, SqlFixture.Pms)));
        Assert.Equal(count, (await db.Store.List<SafetyCase>("case", ["ortho"])).Length);
        var audit = await db.Store.CaptureProductAudit("ortho", SqlFixture.Pms, scenario.Clock.GetUtcNow());
        Assert.Contains(audit.Aggregates, value => value.Kind == "source" && value.Id == imported.Source.Id);
        Assert.Contains(audit.Aggregates, value => value.Kind == "evidence" && value.Id == evidence.Id);
    }

    private sealed class DocumentNoNetwork : IPublicSourceReader
    {
        public Task<PublicSourceItem[]> Read(string source, string productId, CancellationToken ct) =>
            throw new InvalidOperationException("A local document must not invoke a live source adapter.");
    }

    [Theory]
    [InlineData(Roles.Medical)]
    [InlineData(Roles.Regulatory)]
    public async Task Agent_rejection_reopens_existing_case_immediately_without_new_analysis_or_old_signatures(string role)
    {
        var s = new CaseScenario(db);
        s.Case = await s.Service.Register(s.Input with { Scenario = "signal-triage-replay-reject" }, SqlFixture.Pms, CaseScenario.Key());
        await s.Assess();
        await s.Prepare();
        await s.Approve();
        var before = s.Case;
        var signals = Service(s);
        var rejected = Assert.Single(await signals.List("Rejected", Reviewer(role)), v => v.OwnerId == before.Id);
        var rejection = Assert.Single(rejected.History);
        Assert.Equal("Agent", rejection.Origin);
        Assert.Equal("Replay", rejection.AgentMode);
        var run = Assert.Single(before.AgentRuns, r => r.Id == rejection.AgentRunId);
        Assert.Equal(AgentRole.Ingestion, run.Role);
        Assert.Equal(run.Model, rejection.Model);
        Assert.Equal(run.OutputHash, Canonical.HashText(run.OutputJson!));
        Assert.Equal(rejected.SourceHash, rejection.SourceHash);
        var invocations = (await db.Store.List<AgentInvocation>("agentInvocation", ["ortho"])).Length;
        var caseCount = (await db.Store.List<SafetyCase>("case", ["ortho"])).Length;
        var result = await signals.Decide("case", before.Id, "Reopen", Command(before.Version, rejected.SourceId), Reviewer(role), CaseScenario.Key());
        var current = await s.Service.Get(before.Id, Reviewer(role));
        Assert.Equal(before.Version + 1, current.Version);
        Assert.Equal(CaseStage.AwaitingMedicalReview, current.Stage);
        Assert.Equal(before.Id, result.CaseId);
        Assert.Equal("ReviewRequired", result.Status);
        Assert.Equal(new[] { "Reject", "Reopen" }, result.History.Select(d => d.Action));
        Assert.Equal(Reviewer(role).Id, result.History.Last().ActorId);
        Assert.Equal(role, result.History.Last().Role);
        Assert.Equal(Canonical.Hash(before.Sources), Canonical.Hash(current.Sources));
        Assert.Equal(Canonical.Hash(before.Awareness), Canonical.Hash(current.Awareness));
        Assert.Equal(before.FirstAwarenessAt, current.FirstAwarenessAt);
        Assert.Equal(Canonical.Hash(before.Deadlines), Canonical.Hash(current.Deadlines));
        Assert.Equal(Canonical.Hash(before.FollowUps), Canonical.Hash(current.FollowUps));
        Assert.Equal(Canonical.Hash(before.Assessments), Canonical.Hash(current.Assessments));
        Assert.NotEqual(current.CurrentAssessment!.EvidenceHash, Canonical.Hash(current.Evidence));
        Assert.Equal(ReportState.Superseded, current.CurrentReport!.State);
        Assert.Equal(Canonical.Hash(before.Approvals), Canonical.Hash(current.Approvals));
        Assert.Equal(before.CurrentReport!.FrozenJson, current.CurrentReport.FrozenJson);
        Assert.Equal(Canonical.Hash(before.AgentRuns), Canonical.Hash(current.AgentRuns));
        Assert.Equal(invocations, (await db.Store.List<AgentInvocation>("agentInvocation", ["ortho"])).Length);
        Assert.Equal(caseCount, (await db.Store.List<SafetyCase>("case", ["ortho"])).Length);
        Assert.DoesNotContain(await signals.List("Rejected", Reviewer(role)), v => v.OwnerId == current.Id);
        Assert.Contains(await signals.List("ReviewRequired", Reviewer(role)), v => v.CaseId == current.Id);
        Assert.DoesNotContain((await s.Service.Process(current.Id, Reviewer(role))).Steps,
            step => step.Id is "medical" or "regulatory" && step.State == "completed");
        Assert.Equal(Canonical.Hash(before), Canonical.Hash((await db.Store.History<SafetyCase>("case", ["ortho"]))
            .Single(c => c.Id == before.Id && c.Version == before.Version)));
    }

    [Theory]
    [InlineData(Roles.Medical)]
    [InlineData(Roles.Regulatory)]
    public async Task Reviewer_rejection_of_unassessed_source_reuses_case_and_keeps_unknown_assessment_visible(string role)
    {
        var s = new CaseScenario(db);
        await s.Create();
        var signals = Service(s);
        var rejected = await signals.Decide("case", s.Case.Id, "Reject",
            Command(s.Case.Version, s.Case.Sources[0].Id), Reviewer(role), CaseScenario.Key());
        Assert.Equal("Reviewer", rejected.History.Single().Origin);
        Assert.Equal(Reviewer(role).Id, rejected.History.Single().ActorId);
        Assert.Null(rejected.History.Single().AgentRunId);
        var reopened = await signals.Decide("case", s.Case.Id, "Reopen",
            Command(rejected.OwnerVersion, rejected.SourceId), Reviewer(role), CaseScenario.Key());
        var current = await s.Service.Get(s.Case.Id, Reviewer(role));
        Assert.Equal(s.Case.Id, reopened.CaseId);
        Assert.Equal(CaseStage.AwaitingMedicalReview, current.Stage);
        Assert.Null(current.CurrentAssessment);
        Assert.Empty(current.AgentRuns);
        Assert.Empty(current.Approvals);
        Assert.Empty(current.Reports);
        Assert.Equal(Canonical.Hash(s.Case.Deadlines), Canonical.Hash(current.Deadlines));
        Assert.Equal(Canonical.Hash(s.Case.Sources), Canonical.Hash(current.Sources));
    }

    [Theory]
    [InlineData(Roles.Medical)]
    [InlineData(Roles.Regulatory)]
    public async Task Source_without_case_reopens_as_persistent_review_without_invented_awareness_or_duplicate_case(string role)
    {
        var s = new CaseScenario(db);
        var run = await Source(s);
        var signals = Service(s);
        var rejected = await signals.Decide("source", run.Id, "Reject", Command(run.Version, run.Items[0].ExternalId), Reviewer(role), CaseScenario.Key());
        var beforeCases = Canonical.Hash(await db.Store.List<SafetyCase>("case", ["ortho"]));
        var key = CaseScenario.Key();
        var input = Command(rejected.OwnerVersion, rejected.SourceId);
        var reopened = await signals.Decide("source", run.Id, "Reopen", input, Reviewer(role), key);
        Assert.Null(reopened.CaseId);
        Assert.Null(reopened.AwarenessAt);
        Assert.NotNull(reopened.ReviewReference);
        Assert.Equal(rejected.ReviewReference, reopened.ReviewReference);
        Assert.Contains(await signals.List("ReviewRequired", Reviewer(role)), r => r.ReviewReference == reopened.ReviewReference);
        Assert.Equal(Canonical.Hash(reopened), Canonical.Hash(await signals.Decide("source", run.Id, "Reopen", input, Reviewer(role), key)));
        Assert.Equal("version_conflict", (await Assert.ThrowsAsync<DomainException>(() =>
            signals.Decide("source", run.Id, "Reopen", input, Reviewer(role), CaseScenario.Key()))).Code);
        Assert.Equal("signal_not_rejected", (await Assert.ThrowsAsync<DomainException>(() =>
            signals.Decide("source", run.Id, "Reopen", input with { ExpectedVersion = reopened.OwnerVersion }, Reviewer(role), CaseScenario.Key()))).Code);
        Assert.Equal(beforeCases, Canonical.Hash(await db.Store.List<SafetyCase>("case", ["ortho"])));
        var saved = await db.Store.Get<PublicSourceRun>("source", run.Id);
        Assert.Equal(Canonical.Hash(run.Items), Canonical.Hash(saved!.Items));
        Assert.Equal(run.At, saved.At);
        var audit = await db.Store.Audit(run.Id);
        Assert.Single(audit, a => a.Action == "SignalReopened");
        Assert.Equal(new[] { "Reject", "Reopen" }, saved.SignalReviews!.Single().History.Select(d => d.Action));
        await signals.Decide("source", run.Id, "ConfirmRelevance", Command(saved.Version, run.Items[0].ExternalId), Reviewer(role), CaseScenario.Key());
        Assert.DoesNotContain(await signals.List("ReviewRequired", Reviewer(role)), v => v.OwnerId == run.Id);
        Assert.Equal(3, (await db.Store.Get<PublicSourceRun>("source", run.Id))!.SignalReviews!.Single().History.Length);
    }

    [Theory]
    [InlineData(Roles.Pms, true)]
    [InlineData(Roles.Quality, true)]
    [InlineData(Roles.Auditor, true)]
    [InlineData(Roles.Workflow, false)]
    [InlineData(Roles.Agent, false)]
    [InlineData(Roles.Medical, false)]
    public async Task Other_roles_and_nonhuman_identities_cannot_reopen_either_owner_kind(string role, bool human)
    {
        var s = new CaseScenario(db);
        await s.Create();
        var run = await Source(s);
        var signals = Service(s);
        var actor = new Actor("denied-" + role, "Denied synthetic actor", [role], ["ortho"], human);
        foreach (var (kind, id, version, source) in new[] {
            ("case", s.Case.Id, s.Case.Version, s.Case.Sources[0].Id), ("source", run.Id, run.Version, run.Items[0].ExternalId) })
        {
            var rejected = await signals.Decide(kind, id, "Reject", Command(version, source), SqlFixture.Medical, CaseScenario.Key());
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                signals.Decide(kind, id, "Reopen", Command(rejected.OwnerVersion, source), actor, CaseScenario.Key()));
            Assert.Equal(403, error.Status);
        }
    }

    [Theory]
    [InlineData(Roles.Medical)]
    [InlineData(Roles.Regulatory)]
    public async Task Product_scope_filters_list_and_blocks_actions_on_case_and_public_source(string role)
    {
        var s = new CaseScenario(db);
        await s.Create();
        var run = await Source(s);
        var signals = Service(s);
        var actor = Reviewer(role) with { Products = ["cardio"] };
        foreach (var (kind, id, version, source) in new[] {
            ("case", s.Case.Id, s.Case.Version, s.Case.Sources[0].Id), ("source", run.Id, run.Version, run.Items[0].ExternalId) })
        {
            var rejected = await signals.Decide(kind, id, "Reject", Command(version, source), Reviewer(role), CaseScenario.Key());
            Assert.DoesNotContain(await signals.List("Rejected", actor), v => v.OwnerId == id);
            Assert.Equal("product_scope", (await Assert.ThrowsAsync<DomainException>(() =>
                signals.Decide(kind, id, "Reopen", Command(rejected.OwnerVersion, source), actor, CaseScenario.Key()))).Code);
        }
    }

    [Fact]
    public async Task Concurrent_reopening_commits_one_version_and_one_review_history_audit_and_outbox()
    {
        var s = new CaseScenario(db);
        await s.Create();
        var signals = Service(s);
        var rejected = await signals.Decide("case", s.Case.Id, "Reject", Command(s.Case.Version, s.Case.Sources[0].Id), SqlFixture.Medical, CaseScenario.Key());
        async Task<bool> Attempt(Actor actor)
        {
            try { await signals.Decide("case", s.Case.Id, "Reopen", Command(rejected.OwnerVersion, rejected.SourceId), actor, CaseScenario.Key()); return true; }
            catch (DomainException e) when (e.Code == "version_conflict") { return false; }
        }
        Assert.Equal(1, (await Task.WhenAll(Attempt(SqlFixture.Medical), Attempt(SqlFixture.Ra))).Count(success => success));
        var current = await s.Service.Get(s.Case.Id, SqlFixture.Medical);
        Assert.Equal(rejected.OwnerVersion + 1, current.Version);
        Assert.Equal(2, current.SignalReviews!.Single().History.Length);
        Assert.Single(await db.Store.Audit(current.Id), e => e.Action == "SignalReopened");
        await using var connection = await db.Store.Open();
        using var query = new SqlCommand("""
            SELECT a.Json,h.Json,h.Hash,o.Payload,e.PayloadHash,c.Result
            FROM dbo.Aggregates a
            JOIN dbo.AggregateVersions h ON h.Kind=a.Kind AND h.Id=a.Id AND h.Version=a.Version
            JOIN dbo.Outbox o ON o.AggregateKind=a.Kind AND o.AggregateId=a.Id AND o.Version=a.Version
            JOIN dbo.AuditEvents e ON e.AggregateId=a.Id AND e.Version=a.Version
            JOIN dbo.Commands c ON c.Result=a.Json
            WHERE a.Kind='case' AND a.Id=@id
            """, connection);
        query.Parameters.AddWithValue("@id", current.Id);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(reader.GetString(0), reader.GetString(1));
        Assert.Equal(Canonical.Hash(current), reader.GetString(2));
        Assert.Equal(reader.GetString(0), reader.GetString(3));
        Assert.Equal(reader.GetString(2), reader.GetString(4));
        Assert.Equal(reader.GetString(0), reader.GetString(5));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Queued_analysis_cannot_delay_or_replace_reopened_human_review()
    {
        var s = new CaseScenario(db);
        await s.Create();
        var signals = Service(s);
        var rejected = await signals.Decide("case", s.Case.Id, "Reject", Command(s.Case.Version, s.Case.Sources[0].Id), SqlFixture.Medical, CaseScenario.Key());
        var queued = await s.Service.RequestAssessment(s.Case.Id, new(rejected.OwnerVersion, AgentMode.Failure, Reason), SqlFixture.Pms, CaseScenario.Key());
        await signals.Decide("case", s.Case.Id, "Reopen", Command(queued.Version, rejected.SourceId), SqlFixture.Ra, CaseScenario.Key());
        var current = await s.Service.Get(s.Case.Id, SqlFixture.Medical);
        Assert.Equal(CaseStage.AwaitingMedicalReview, current.Stage);
        Assert.Equal("Abandoned", current.AgentRuns.Single().Status);
        Assert.Equal(Canonical.Hash(current), Canonical.Hash(await s.Service.ProcessAssessment(current.Id, queued.AgentRuns.Single().Id)));
        Assert.Null(current.CurrentAssessment);
        Assert.Null(await db.Store.Get<AgentInvocation>("agentInvocation",
            "agent-" + Canonical.HashText(queued.AgentRuns.Single().Id + "-ingestion")[..32]));
    }

    [Fact]
    public async Task Explicit_human_reopening_cannot_be_undone_by_a_later_agent_triage_proposal()
    {
        var s = new CaseScenario(db);
        s.Case = await s.Service.Register(s.Input with { Scenario = "signal-triage-replay-reject" }, SqlFixture.Pms, CaseScenario.Key());
        await s.Assess();
        await Service(s).Decide("case", s.Case.Id, "Reopen", Command(s.Case.Version, s.Case.Sources[0].Id), SqlFixture.Ra, CaseScenario.Key());
        s.Case = await s.Service.Get(s.Case.Id, SqlFixture.Pms);
        var history = Canonical.Hash(s.Case.SignalReviews);
        await s.Assess();
        Assert.Equal(history, Canonical.Hash(s.Case.SignalReviews));
        Assert.Equal("ReviewRequired", s.Case.SignalReviews!.Single().Status);
    }

    [Fact]
    public async Task Legacy_case_and_source_json_roundtrip_without_new_null_metadata()
    {
        var s = new CaseScenario(db);
        await s.Create();
        var run = await Source(s);
        var caseJson = Canonical.Serialize(s.Case);
        var sourceJson = Canonical.Serialize(run);
        Assert.DoesNotContain("\"signalReviews\"", caseJson);
        Assert.DoesNotContain("\"signalReviews\"", sourceJson);
        Assert.Equal(Canonical.HashText(caseJson), Canonical.Hash(Canonical.Read<SafetyCase>(caseJson)));
        Assert.Equal(Canonical.HashText(sourceJson), Canonical.Hash(Canonical.Read<PublicSourceRun>(sourceJson)));
        Assert.DoesNotContain(await Service(s).List("Rejected", SqlFixture.Medical), v => v.OwnerId == s.Case.Id || v.OwnerId == run.Id);
    }

    [Fact]
    public async Task Simulator_rejection_is_not_a_signal_rejection()
    {
        var s = new CaseScenario(db);
        await s.Create(); await s.Assess(); await s.Prepare(); await s.Approve();
        s.Case = await s.Service.Submit(s.Case.Id, new(s.Case.Version, "Reject", Reason), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal(ReportState.Rejected, s.Case.CurrentReport!.State);
        Assert.DoesNotContain(await Service(s).List("Rejected", SqlFixture.Medical), v => v.OwnerId == s.Case.Id);
    }

    [Fact]
    public async Task Invalid_source_or_stale_version_never_changes_case_history_or_outbox()
    {
        var s = new CaseScenario(db);
        await s.Create();
        var audit = Canonical.Hash(await db.Store.Audit(s.Case.Id));
        var signals = Service(s);
        Assert.Equal("signal_missing", (await Assert.ThrowsAsync<DomainException>(() =>
            signals.Decide("case", s.Case.Id, "Reject", Command(s.Case.Version, "unknown-source"), SqlFixture.Medical, CaseScenario.Key()))).Code);
        Assert.Equal("version_conflict", (await Assert.ThrowsAsync<DomainException>(() =>
            signals.Decide("case", s.Case.Id, "Reject", Command(s.Case.Version - 1, s.Case.Sources[0].Id), SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal(Canonical.Hash(s.Case), Canonical.Hash(await s.Service.Get(s.Case.Id, SqlFixture.Medical)));
        Assert.Equal(audit, Canonical.Hash(await db.Store.Audit(s.Case.Id)));
        Assert.Single(await db.Store.History<SafetyCase>("case", ["ortho"]), c => c.Id == s.Case.Id);
        await using var connection = await db.Store.Open();
        using var query = new SqlCommand("SELECT COUNT(*) FROM dbo.Outbox WHERE AggregateKind='case' AND AggregateId=@id", connection);
        query.Parameters.AddWithValue("@id", s.Case.Id);
        Assert.Equal(1, Convert.ToInt32(await query.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Unknown_historical_rejection_keeps_unknown_origin_and_never_guesses_an_agent()
    {
        var s = new CaseScenario(db);
        var run = await Source(s);
        var item = run.Items[0];
        var legacy = await db.Store.Execute<PublicSourceRun>(new(run.Id, "source", run.ProductId, run.Version,
            CaseScenario.Key(), "SyntheticUnknownHistoryFixture", Reason, SqlFixture.Quality, new { run.Id }, s.Clock.Now),
            current => current! with { Version = current.Version + 1,
                SignalReviews = [new(item.ExternalId, item.ProvenanceHash, "SIG-UNKNOWN-HISTORY", "Rejected", [])] });
        var signal = Assert.Single(await Service(s).List("Rejected", SqlFixture.Medical), v => v.OwnerId == run.Id);
        Assert.Empty(signal.History);
        Assert.Equal("Rejected", signal.Status);
        var reopened = await Service(s).Decide("source", run.Id, "Reopen", Command(legacy.Version, item.ExternalId), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal("Reviewer", Assert.Single(reopened.History).Origin);
        Assert.Equal("Reopen", reopened.History.Single().Action);
    }

    private Task<PublicSourceRun> Source(CaseScenario s) =>
        new PublicSourceService(db.Store, db.Objects, new SyntheticMetadata(), s.Clock, db.Policies)
            .Read("pubmed", new("ortho"), SqlFixture.Pms, CaseScenario.Key(), CancellationToken.None);

    private sealed class SyntheticMetadata : IPublicSourceReader
    {
        public Task<PublicSourceItem[]> Read(string source, string productId, CancellationToken ct = default) =>
            Task.FromResult<PublicSourceItem[]>([new("SYNTHETIC-" + Guid.NewGuid().ToString("N"),
                "Synthetic metadata fixture, not a live publication", "https://example.invalid/synthetic-metadata", null,
                "SYNTHETISCHER PRUEFBELEG: reine Vergleichsmetadaten ohne eigenen Vorfall, klinische Kenntnis oder KI-Analyse.", Canonical.HashText("synthetic-public-metadata"))]);
    }
}
