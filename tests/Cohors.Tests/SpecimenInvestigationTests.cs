using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Agents;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed class SpecimenInvestigationRuleTests
{
    [Fact]
    public void AlteringSimulationNeedsPriorNoticeInAdditionToInternalApprovals()
    {
        var plan = SpecimenTestData.Approved(SpecimenTestData.Plan());
        Assert.Equal("specimen_prior_notice_required",
            Assert.Throws<DomainException>(() => SpecimenInvestigationRules.RequireReadyToSimulate(plan)).Code);
        var informed = SpecimenTestData.Informed(plan);
        SpecimenInvestigationRules.RequireReadyToSimulate(informed);
        Assert.Contains("keine Behoerdenzustimmung", informed.PriorNotice!.Meaning, StringComparison.Ordinal);
        Assert.Null(informed.Outcome);
    }

    [Fact]
    public void DocumentedNonCompromisingMethodHasNoBlanketAuthorityApprovalRequirement()
    {
        var plan = SpecimenTestData.Approved(SpecimenTestData.Plan(false));
        SpecimenInvestigationRules.RequireReadyToSimulate(plan);
        Assert.Null(plan.PriorNotice);
    }

    [Fact]
    public void InternalApprovalsCannotShareAnIdentity()
    {
        var plan = SpecimenTestData.Approved(SpecimenTestData.Plan());
        plan = plan with { Approvals = [plan.Approvals[0], plan.Approvals[1] with { ActorId = plan.Approvals[0].ActorId }] };
        Assert.Equal("specimen_approval", Assert.Throws<DomainException>(() => SpecimenInvestigationRules.RequireIntegrity(plan)).Code);
    }

    [Fact]
    public void ChangingPreservationMetadataDoesNotRewriteItsOriginalEvidence()
    {
        var plan = SpecimenTestData.Plan();
        plan = plan with { Preservation = plan.Preservation with { OriginDescription = SpecimenTestData.Text + " Abweichende Herkunft." } };
        plan = plan with { ScopeHash = SpecimenInvestigationRules.Scope(plan) };
        Assert.Equal("specimen_preservation", Assert.Throws<DomainException>(() => SpecimenInvestigationRules.RequireIntegrity(plan)).Code);
    }

    [Fact]
    public void ChangedMethodCannotKeepTheOldFrozenPackage()
    {
        var plan = SpecimenTestData.Approved(SpecimenTestData.Plan());
        plan = plan with { Method = SpecimenTestData.Text + " Andere Pruefvariante." };
        var scope = SpecimenInvestigationRules.Scope(plan);
        plan = plan with { ScopeHash = scope, Approvals = plan.Approvals.Select(a => a with { ScopeHash = scope }).ToArray() };
        Assert.Equal("specimen_approval", Assert.Throws<DomainException>(() => SpecimenInvestigationRules.RequireIntegrity(plan)).Code);
    }

    [Fact]
    public void NoticeMustBeASeparateLaterRecordedStepAndRemainOnlyASimulation()
    {
        var plan = SpecimenTestData.Informed(SpecimenTestData.Approved(SpecimenTestData.Plan()));
        var premature = plan with { PriorNotice = plan.PriorNotice! with { CaseVersion = plan.CaseSource.Version } };
        Assert.Equal("specimen_notice_integrity", Assert.Throws<DomainException>(() => SpecimenInvestigationRules.RequireIntegrity(premature)).Code);
        var falsePermission = plan with { PriorNotice = plan.PriorNotice! with { Meaning = "Behoerdlich genehmigt." } };
        Assert.Equal("specimen_notice_integrity", Assert.Throws<DomainException>(() => SpecimenInvestigationRules.RequireIntegrity(falsePermission)).Code);
    }

    [Fact]
    public void NewCaseFactsInvalidateThePlanAndAnOpenPlanCannotLookLikeClosure()
    {
        var plan = SpecimenTestData.Plan();
        var c = SpecimenTestData.Case() with { Version = 2, Evidence = plan.Sources, Investigations = [plan] };
        SpecimenInvestigationRules.RequireCurrent(c, plan);
        Assert.Equal("specimen_stale",
            Assert.Throws<DomainException>(() => SpecimenInvestigationRules.RequireCurrent(c with { Country = "DE" }, plan)).Code);
        var process = CaseProcess.Read(c with { Stage = CaseStage.Closed, ClosedAt = SpecimenTestData.Now });
        Assert.NotEqual("completed", process.Steps.Single(s => s.Id == "followup").State);
        Assert.Contains("1 Untersuchungsauftrag", process.Steps.Single(s => s.Id == "followup").Detail, StringComparison.Ordinal);
    }
}

[Collection("SQL pilot")]
public sealed class SpecimenInvestigationTests(SqlFixture fixture)
{
    [Fact]
    public async Task SeparateNoticePrecedesSimulationWithoutChangingDeadlinesOrOriginals()
    {
        var scenario = new CaseScenario(fixture);
        await scenario.Create();
        var c = scenario.Case;
        var deadlines = Canonical.Serialize(c.Deadlines);
        var sources = c.Evidence;
        var input = SpecimenTestData.Command(c);
        var planKey = CaseScenario.Key();
        c = await scenario.Service.RecordSpecimenPlan(c.Id, input, SqlFixture.Pms, planKey);
        Assert.Equal(c.Version, (await scenario.Service.RecordSpecimenPlan(c.Id, input, SqlFixture.Pms, planKey)).Version);
        var id = Assert.Single(c.Investigations!).Id;
        c = await scenario.Service.ApproveSpecimen(c.Id, id, new(c.Version, SpecimenTestData.Text), SqlFixture.Medical, CaseScenario.Key());
        c = await scenario.Service.ApproveSpecimen(c.Id, id, new(c.Version, SpecimenTestData.Text), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal("specimen_prior_notice_required", (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service.SimulateSpecimen(c.Id, id, SpecimenTestData.Result(c), SqlFixture.Pms, CaseScenario.Key()))).Code);
        c = await scenario.Service.InformSpecimenAuthority(c.Id, id, new(c.Version, SpecimenTestData.Text), SqlFixture.Ra, CaseScenario.Key());
        var informed = Assert.Single(c.Investigations!);
        var result = SpecimenTestData.Result(c);
        var resultKey = CaseScenario.Key();
        c = await scenario.Service.SimulateSpecimen(c.Id, id, result, SqlFixture.Pms, resultKey);
        Assert.Equal(c.Version, (await scenario.Service.SimulateSpecimen(c.Id, id, result, SqlFixture.Pms, resultKey)).Version);
        var completed = Assert.Single(c.Investigations!);
        Assert.Equal("SimulatedExamined", completed.Status);
        Assert.True(completed.PriorNotice!.CaseVersion < completed.Outcome!.CaseVersion);
        Assert.Equal(informed.FrozenJson, completed.FrozenJson);
        Assert.Equal(new[] { Roles.Medical, Roles.Regulatory }, completed.Approvals.Select(a => a.Role).ToArray());
        Assert.Equal(2, completed.Approvals.Select(a => a.ActorId).Distinct().Count());
        Assert.Equal(deadlines, Canonical.Serialize(c.Deadlines));
        Assert.Equal(scenario.Case.FirstAwarenessAt, c.FirstAwarenessAt);
        Assert.Empty(c.Submissions);
        foreach (var original in sources) Assert.Contains(original, c.Evidence);
        Assert.Equal(completed.Preservation.Evidence.Content, await fixture.Objects.Read(completed.Preservation.Evidence.Hash));
        Assert.Equal(completed.Outcome.Evidence.Content, await fixture.Objects.Read(completed.Outcome.Evidence.Hash));
        var manifest = await fixture.Store.Manifest(c.Id);
        Assert.True(manifest.HashChainVerified);
        Assert.True(manifest.CurrentStateVerified);
        Assert.Contains(await fixture.Store.PendingEvents(10000), e => e.AggregateId == c.Id && e.Version == c.Version);
    }

    [Fact]
    public async Task RevisedPlanKeepsOriginalVariantAndUsesTheStoredSourceHash()
    {
        var scenario = new CaseScenario(fixture);
        await scenario.Create();
        var c = scenario.Case;
        var legacy = JsonNode.Parse(Canonical.Serialize(c))!.AsObject();
        legacy["version"] = c.Version + 1;
        legacy.Remove("currentAssessment");
        legacy.Remove("currentReport");
        using var json = JsonDocument.Parse(legacy.ToJsonString());
        await fixture.Store.Execute<JsonElement>(new(c.Id, "case", c.ProductId, c.Version, CaseScenario.Key(),
            "SyntheticLegacyShapePrepared", SpecimenTestData.Text, SqlFixture.Pms, new { c.Id }, scenario.Clock.Now),
            _ => json.RootElement.Clone());
        c = await scenario.Service.Get(c.Id, SqlFixture.Pms);
        var storedSource = (await fixture.Store.Audit(c.Id)).Single(a => a.Version == c.Version).PayloadHash;
        Assert.NotEqual(storedSource, Canonical.Hash(c));
        var input = SpecimenTestData.Command(c);
        c = await scenario.Service.RecordSpecimenPlan(c.Id, input, SqlFixture.Pms, CaseScenario.Key());
        var id = Assert.Single(c.Investigations!).Id;
        Assert.Equal(storedSource, c.Investigations![0].CaseSource.Hash);
        c = await scenario.Service.ApproveSpecimen(c.Id, id, new(c.Version, SpecimenTestData.Text), SqlFixture.Medical, CaseScenario.Key());
        c = await scenario.Service.ApproveSpecimen(c.Id, id, new(c.Version, SpecimenTestData.Text), SqlFixture.Ra, CaseScenario.Key());
        c = await scenario.Service.InformSpecimenAuthority(c.Id, id, new(c.Version, SpecimenTestData.Text), SqlFixture.Ra, CaseScenario.Key());
        var oldVersion = c.Version;
        var original = Assert.Single(c.Investigations!);
        c = await scenario.Service.RecordSpecimenPlan(c.Id, input with
            { ExpectedVersion = c.Version, Method = SpecimenTestData.Text + " Neue Untersuchungsvariante." }, SqlFixture.Pms, CaseScenario.Key());
        var revised = Assert.Single(c.Investigations!);
        Assert.Equal(2, revised.Revision);
        Assert.Equal("Draft", revised.Status);
        Assert.Empty(revised.Approvals);
        Assert.Null(revised.PriorNotice);
        Assert.Null(revised.FrozenJson);
        Assert.NotEqual(original.ScopeHash, revised.ScopeHash);
        var historical = await scenario.Service.ExportSpecimen(c.Id, id, SqlFixture.Ra, oldVersion);
        Assert.Equal(original.ScopeHash, historical.Investigation.ScopeHash);
        Assert.Equal(original.FrozenJson, historical.Investigation.FrozenJson);
        Assert.Equal(original.Preservation.Evidence.Content, await fixture.Objects.Read(original.Preservation.Evidence.Hash));
        Assert.Contains(original.CaseSource, ProductAuditRules.Sources(Canonical.Serialize(historical)));
        ProductAuditRules.Products("case", c.ProductId, Canonical.Serialize(c));
    }

    [Fact]
    public async Task HumanRoleProductAndActualObjectBytesAreRequired()
    {
        var scenario = new CaseScenario(fixture);
        await scenario.Create();
        var c = scenario.Case;
        var input = SpecimenTestData.Command(c);
        Assert.Equal("human_required", (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service.RecordSpecimenPlan(c.Id, input, SqlFixture.Pms with { IsHuman = false }, CaseScenario.Key()))).Code);
        Assert.Equal("product_scope", (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service.RecordSpecimenPlan(c.Id, input, SqlFixture.Pms with { Products = ["cardio"] }, CaseScenario.Key()))).Code);
        c = await scenario.Service.RecordSpecimenPlan(c.Id, input, SqlFixture.Pms, CaseScenario.Key());
        var id = Assert.Single(c.Investigations!).Id;
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service.ApproveSpecimen(c.Id, id, new(c.Version, SpecimenTestData.Text), SqlFixture.Pms, CaseScenario.Key()))).Code);
        c = await scenario.Service.ApproveSpecimen(c.Id, id, new(c.Version, SpecimenTestData.Text), SqlFixture.Medical, CaseScenario.Key());
        Assert.Equal("four_eyes", (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service.ApproveSpecimen(c.Id, id, new(c.Version, SpecimenTestData.Text),
                SqlFixture.Ra with { Id = SqlFixture.Medical.Id }, CaseScenario.Key()))).Code);
        var service = new CaseService(fixture.Store, new WrongObjectBytes(fixture.Objects), fixture.Policies,
            new AgentRunner(), new KnowledgeSearch(fixture.Store, fixture.Objects, fixture.Store),
            scenario.Clock, new MirComposer());
        Assert.Equal("evidence_object_hash", (await Assert.ThrowsAsync<DomainException>(() =>
            service.ApproveSpecimen(c.Id, id, new(c.Version, SpecimenTestData.Text), SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Equal(c.Version, (await scenario.Service.Get(c.Id, SqlFixture.Pms)).Version);
    }

    private sealed class WrongObjectBytes(IObjectStore original) : IObjectStore
    {
        public Task<string> Put(string content, CancellationToken ct = default) => original.Put(content, ct);
        public Task<string> Read(string hash, CancellationToken ct = default) => Task.FromResult("SYNTHETIC WRONG OBJECT BYTES");
        public Task<string> PutBytes(ReadOnlyMemory<byte> content, CancellationToken ct = default) => original.PutBytes(content, ct);
        public Task<byte[]> ReadBytes(string hash, CancellationToken ct = default) => Task.FromResult("SYNTHETIC WRONG OBJECT BYTES"u8.ToArray());
    }
}

internal static class SpecimenTestData
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-16T15:00:00Z");
    internal const string Text = "SYNTHETISCH: Herkunft und Unsicherheit werden nachvollziehbar dokumentiert. Keine reale Materialpruefung, bestaetigte Ursache oder behoerdliche Zustimmung wird daraus abgeleitet.";
    internal static SafetyCase Case() => new("specimen-case", 1, "DEMO-CASE", "ortho", "Synthetischer Asservatenfall",
        Text, null, "DEMO-LOT", "IE", "EU-IE", Seriousness.OtherSerious, Now.AddDays(-2), Now.AddDays(-1), Now,
        null, "specimen-test", true, CaseStage.Investigating, Disposition.Investigate,
        [], [], [], [], [], [], [], [], [], [], [], []);
    internal static RecordSpecimenPlanCommand Command(SafetyCase c) => new(c.Version, "DEMO-INVESTIGATION",
        "DEMO-SPECIMEN", Now.AddHours(-1), Text, Text, "DEMO-CUSTODY", Text, Text, Text, true, Text, Text, "de", Text);
    internal static SimulateSpecimenCommand Result(SafetyCase c) => new(c.Version, Text, Text, Text, Text);

    internal static SpecimenInvestigation Plan(bool compromising = true)
    {
        var c = Case();
        var input = Command(c);
        var text = SpecimenInvestigationRules.PreservationText(input.Reference, 1, SpecimenInvestigationRules.Context(c),
            input.SpecimenReference, input.ReceivedAt, input.OriginDescription, input.ConditionBefore,
            input.CustodyReference, input.PreservationMeasures, input.TraceabilityAssessment, "pms", Now);
        var hash = Canonical.HashText(text);
        var evidence = new Evidence("preservation", 1, c.ProductId, "Synthetische Asservatensicherung", "SpecimenPreservation",
            text, hash, "cohors:synthetic", "Original", "Synthetic", "Product", Now, null, Now, true, true, hash);
        var plan = new SpecimenInvestigation("investigation", 1, input.Reference, new(c.Id, c.Version, Canonical.Hash(c)),
            SpecimenInvestigationRules.Context(c with { Evidence = [evidence] }),
            new("ortho", "Synthetic implant", "Implant", "DEMO-BASIC-UDI", "Synthetic purpose", "DEMO-MODEL", ["EU-IE"]),
            new(input.SpecimenReference, input.ReceivedAt, Text, Text, input.CustodyReference, Text, Text, evidence),
            [evidence], Text, compromising, Text, compromising ? Text : "", "Health Products Regulatory Authority (HPRA)",
            SpecimenInvestigationRules.Recipient(c.Jurisdiction), "de", "pms", Now, "", [], "Draft", null, null, null, null);
        return plan with { ScopeHash = SpecimenInvestigationRules.Scope(plan) };
    }
    internal static SpecimenInvestigation Approved(SpecimenInvestigation plan) => plan with
    {
        Status = "Approved", FrozenJson = Canonical.Serialize(SpecimenInvestigationRules.Package(plan)),
        Approvals =
        [
            new("medical-approval", "medical", "Medical", Roles.Medical, plan.ScopeHash, Now.AddMinutes(1), SpecimenInvestigationRules.ApprovalMeaning, Text, null),
            new("ra-approval", "ra", "Regulatory", Roles.Regulatory, plan.ScopeHash, Now.AddMinutes(2), SpecimenInvestigationRules.ApprovalMeaning, Text, null)
        ]
    };
    internal static SpecimenInvestigation Informed(SpecimenInvestigation plan) => plan with
    {
        Status = "AuthorityInformed", PriorNotice = new("DEMO-SPECIMEN-NOTICE-" + plan.ScopeHash[..20],
            plan.Recipient, plan.ScopeHash, Now.AddMinutes(3), "ra", 5, SpecimenInvestigationRules.NoticeMeaning, true)
    };
}
