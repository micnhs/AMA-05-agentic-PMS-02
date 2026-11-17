using System.IO.Compression;
using System.Text.Json;
using Cohors.Agents;
using Cohors.Api;
using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Tests;

[Collection("SQL pilot")]
public sealed class PsurGroupTests : IAsyncLifetime
{
    private readonly SqlFixture fixture = new() { SeedCases = false };
    private readonly TestClock clock = new(DateTimeOffset.Parse("2026-11-16T15:00:00Z"));
    private const string Reason = "Die vollstaendige synthetische Gruppe wird mit getrennten Basic-UDI-Daten, Quellen und offenen Unsicherheiten geprueft.";
    private AnalyticsService Service() => new(fixture.Store, new AgentRunner(new(null, null, null, null)), fixture.Policies, clock);
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();
    private static RecordPsurGroupCommand Definition(string lead = "ortho") => new(0,
        "DEMO-PSUR-GROUP-" + Guid.NewGuid().ToString("N").ToUpperInvariant(), lead, ["ortho", "cardio"], new(2025, 11, 15),
        "Ausschliesslich synthetische Testgruppierung fuer die getrennte Nachweisfuehrung beider Klasse-III-Familien; keine Aussage zur Eignung realer Produktkombinationen.",
        "Jahresfenster und gesonderte Einreichungsvereinbarung des festen Leitprodukts. Vorhandene Serien und ihre Verpflichtungen werden nicht beendet.");
    private static FreezePsurCommand FreezeInput(PsurGroupDefinition g) =>
        new(g.ProductId, g.ProductId, g.FirstPeriodStart, g.FirstPeriodStart.AddYears(1).AddDays(-1), "Worldwide",
            GroupId: g.Id, GroupVersion: g.Version);
    private async Task<SafetyCase> Case(string product)
    {
        var input = new CaseScenario(fixture).Input with { ProductId = product };
        return await fixture.Service(clock).Register(input, SqlFixture.Pms, CaseScenario.Key());
    }
    private async Task ChangeProfile(string product, Func<ProductGovernance, ProductGovernance> change)
    {
        var current = (await fixture.Store.List<ProductGovernance>("productGovernance", [product])).Single();
        var next = change(current) with { Version = current.Version + 1 };
        await fixture.Store.Execute<ProductGovernance>(new(current.Id, "productGovernance", product, current.Version,
            CaseScenario.Key(), "GroupProfileTest", Reason, SqlFixture.Quality, next, clock.Now), _ => next);
    }

    [Fact]
    public async Task T16_GroupKeepsEachBasicUdiIndependentlyReconstructableThroughReviewAndExport()
    {
        var ortho = await Case("ortho");
        var cardio = new[] { await Case("cardio"), await Case("cardio") };
        var service = Service();
        var definition = await service.RecordGroup(Definition(), SqlFixture.Pms, CaseScenario.Key());
        var p = await service.Freeze(FreezeInput(definition), SqlFixture.Pms, CaseScenario.Key());
        var group = Assert.IsType<PsurGrouping>(p.Snapshot.Group);
        Assert.Equal(2, group.Members.Length);
        Assert.Equal(Canonical.Hash(definition), group.DefinitionSource.Hash);
        Assert.Empty(PsurGroupRules.Check(p.Snapshot));
        Assert.DoesNotContain(p.Findings, f => f.Severity is "Critical" or "Major");
        foreach (var member in group.Members)
        {
            var ids = member.Product.ProductId == "ortho" ? new[] { ortho.Id } : cardio.Select(c => c.Id).ToArray();
            var region = member.Snapshot.Periods.Single(t => t.Label == "N").Regions.Single(r => r.Region == "Worldwide");
            Assert.Equal(ids.Length, region.Events);
            Assert.Equal(ids.Order(StringComparer.Ordinal), region.NumeratorSources!.Select(r => r.Id).Order(StringComparer.Ordinal));
            Assert.All(member.Snapshot.Documents, d => Assert.Equal(member.Product.ProductId, d.ProductId));
            Assert.All(member.Snapshot.Directory!.Entries.Where(e => e.Kind == "case"),
                e => Assert.Contains(e.Source.Id, ids));
            Assert.Contains($"[metric:{member.Product.ProductId}:N:Worldwide:events={ids.Length}]", p.Draft);
            Assert.Contains(member.Product.BasicUdi, p.Draft);
        }
        Assert.DoesNotContain("[metric:N:Worldwide:", p.Draft);
        p = await service.Approve(p.Id, new(p.Version, Reason), SqlFixture.Medical, CaseScenario.Key());
        p = await service.Approve(p.Id, new(p.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        p = await service.Simulate(p.Id, new(p.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal("SimulatedAccepted", p.Status);
        Assert.Equal(2, p.Approvals.Length);
        Assert.All(p.Approvals, a => Assert.Equal(p.ScopeHash, a.ScopeHash));
        var export = await new PsurExportService(fixture.Store, clock).Export(p.Id, p.Version, "zip", SqlFixture.Pms);
        using var archive = new ZipArchive(new MemoryStream(export.Bytes), ZipArchiveMode.Read);
        using var snapshot = JsonDocument.Parse(archive.GetEntry("snapshot.json")!.Open());
        Assert.Equal(2, snapshot.RootElement.GetProperty("group").GetProperty("members").GetArrayLength());
        using var metadata = JsonDocument.Parse(archive.GetEntry("metadata.json")!.Open());
        Assert.Equal(2, metadata.RootElement.GetProperty("group").GetProperty("members").GetArrayLength());
        Assert.Equal(Canonical.Hash(p), Canonical.Hash(await fixture.Store.Get<Psur>("psur", p.Id)));
        var output = Path.Combine(AppContext.BaseDirectory, "ExportEvidence");
        Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(Path.Combine(output, "cohors-group-psur.zip"), export.Bytes);
    }

    [Fact]
    public async Task T16_LeadOnlyPermissionCannotReadEditApproveExportSimulateOrReplayAGroup()
    {
        var service = Service();
        var definition = await service.RecordGroup(Definition(), SqlFixture.Pms, CaseScenario.Key());
        var key = CaseScenario.Key();
        var input = FreezeInput(definition);
        var p = await service.Freeze(input, SqlFixture.Pms, key);
        var limited = SqlFixture.Pms with { Products = ["ortho"] };
        Assert.Empty(await service.ListPsurs(limited));
        Assert.Empty(await service.ListGroups(limited));
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.GetPsur(p.Id, limited))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.Freeze(input, limited, key))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.Freeze(input, limited, CaseScenario.Key()))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.Revise(p.Id,
            new(p.Version, p.Draft + "\nKeine neue klinische Feststellung.", Reason), limited, CaseScenario.Key()))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.Approve(p.Id,
            new(p.Version, Reason), SqlFixture.Medical with { Products = ["ortho"] }, CaseScenario.Key()))).Status);
        foreach (var format in new[] { "json", "pdf", "zip" })
            Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
                new PsurExportService(fixture.Store, clock).Export(p.Id, p.Version, format, limited))).Status);
        p = await service.Approve(p.Id, new(p.Version, Reason), SqlFixture.Medical, CaseScenario.Key());
        p = await service.Approve(p.Id, new(p.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.Simulate(p.Id,
            new(p.Version, Reason), SqlFixture.Ra with { Products = ["ortho"] }, CaseScenario.Key()))).Status);
        var agentInput = new AgentInput(AgentRole.PsurAuthoring, AgentMode.Replay, null, p.Snapshot, null,
            PsurGroupRules.Documents(p.Snapshot), Reason, 0);
        Assert.Equal("agent_product", Assert.Throws<DomainException>(() => AgentBoundary.RequireProducts(agentInput, ["ortho"])).Code);
        AgentBoundary.RequireProducts(agentInput, ["ortho", "cardio"]);
        var invocation = new AgentInvocation("group-invocation-scope", 1, "ortho", Canonical.Hash(agentInput), "synthetic-test-owner",
            "Running", clock.Now.AddMinutes(-11), null, null, null, Canonical.Serialize(agentInput));
        await fixture.Store.Execute<AgentInvocation>(new(invocation.Id, "agentInvocation", "ortho", 0, CaseScenario.Key(),
            "GroupInvocationFixture", Reason, SqlFixture.Quality, invocation, clock.Now), _ => invocation);
        var operations = new OperationsService(fixture.Store, fixture.Store, clock);
        var limitedQuality = SqlFixture.Quality with { Products = ["ortho"] };
        Assert.Empty(await operations.ListInvocations(limitedQuality));
        Assert.Contains(await operations.ListInvocations(SqlFixture.Quality), i => i.Id == invocation.Id);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => operations.AbandonInvocation(invocation.Id,
            new(invocation.Version, Reason), limitedQuality, CaseScenario.Key()))).Status);
    }

    [Theory]
    [InlineData("notifiedBody", "psur_group_notified_body")]
    [InlineData("lowerLead", "psur_group_lead")]
    [InlineData("schedule", "psur_group_schedule")]
    public async Task T16_GroupRejectsDifferentNotifiedBodiesLowerClassLeadAndMisalignedSchedule(string change, string code)
    {
        var input = Definition();
        if (change == "notifiedBody")
            await ChangeProfile("cardio", p => p with { Device = p.Device with { NotifiedBodyNumber = "8888" } });
        if (change == "lowerLead")
            await ChangeProfile("ortho", p => p with { Device = p.Device with { RiskClass = "IIb" } });
        if (change == "schedule") input = input with { FirstPeriodStart = input.FirstPeriodStart.AddDays(1) };
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() =>
            Service().RecordGroup(input, SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Empty(await fixture.Store.List<PsurGroupDefinition>("psurGroup", SqlFixture.Pms.Products));
    }

    [Fact]
    public async Task T16_ProfileOrMembershipChangeRequiresExplicitVersionAndLeavesFrozenReportAndScopeUnchanged()
    {
        var service = Service();
        var command = Definition();
        var definition = await service.RecordGroup(command, SqlFixture.Pms, CaseScenario.Key());
        var p = await service.Freeze(FreezeInput(definition), SqlFixture.Pms, CaseScenario.Key());
        var original = Canonical.Serialize(p);
        await ChangeProfile("cardio", profile => profile with { LifecyclePolicy = profile.LifecyclePolicy + " Kontrollierte synthetische Nachfassung." });
        var refreeze = FreezeInput(definition) with { PreviousReportId = p.Id, PreviousReportVersion = p.Version, RevisionReason = Reason };
        Assert.Equal("psur_group_profile_stale", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Freeze(refreeze, SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.RecordGroup(command with { ExpectedVersion = definition.Version, MemberProductIds = ["ortho"] },
                SqlFixture.Pms with { Products = ["ortho"] }, CaseScenario.Key()))).Status);
        var updated = await service.RecordGroup(command with { ExpectedVersion = definition.Version, MemberProductIds = ["ortho"] },
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal("psur_group_version", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Freeze(refreeze, SqlFixture.Pms, CaseScenario.Key()))).Code);
        var next = await service.Freeze(refreeze with { GroupVersion = updated.Version }, SqlFixture.Pms, CaseScenario.Key());
        Assert.Single(next.Snapshot.Group!.Members);
        Assert.Equal(2, next.Snapshot.Series!.ReportVersion);
        Assert.Equal(original, Canonical.Serialize(await fixture.Store.Get<Psur>("psur", p.Id)));
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            service.GetPsur(p.Id, SqlFixture.Pms with { Products = ["ortho"] }))).Status);
        Assert.Equal(next.Id, (await service.GetPsur(next.Id, SqlFixture.Pms with { Products = ["ortho"] })).Id);
    }

    [Fact]
    public async Task T16_LeadingPeriodIsUniqueButNonLeadingReuseAndOldSeriesRemainIndependent()
    {
        var service = Service();
        var single = await service.Freeze(new("ortho", "ortho", new(2025, 11, 15), new(2026, 11, 14), "Worldwide"),
            SqlFixture.Pms, CaseScenario.Key());
        var original = Canonical.Serialize(single);
        var conflicting = await service.RecordGroup(Definition(), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal("psur_lead_period_conflict", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Freeze(FreezeInput(conflicting), SqlFixture.Pms, CaseScenario.Key()))).Code);
        var command = Definition("cardio");
        var otherLead = await service.RecordGroup(command, SqlFixture.Pms, CaseScenario.Key());
        var other = await service.Freeze(FreezeInput(otherLead), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal("cardio", other.Snapshot.LeadProductId);
        Assert.Equal(2, other.Snapshot.Group!.Members.Length);
        Assert.DoesNotContain(other.Findings, f => f.Severity is "Critical" or "Major");
        Assert.Equal("psur_group_identity", (await Assert.ThrowsAsync<DomainException>(() =>
            service.RecordGroup(command with { ExpectedVersion = otherLead.Version, LeadProductId = "ortho" },
                SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Equal(original, Canonical.Serialize(await fixture.Store.Get<Psur>("psur", single.Id)));
        Assert.Null(single.NotifiedBodyReceipt);
        Assert.Equal(single.Snapshot.Series!.SubmissionDate, (await service.GetPsur(single.Id, SqlFixture.Pms)).Snapshot.Series!.SubmissionDate);
    }

    [Fact]
    public async Task T16_LeadingBasicUdiClaimCannotBeBypassedByAnotherInternalProductIdOrLimitedReadScope()
    {
        var state = await fixture.Store.Capture(["cardio"]);
        var profile = state.Governance!.Single() with { PsurReference = "DEMO-PSUR-HISTORICAL-ALIAS" };
        profile = profile with { Device = profile.Device with { BasicUdi = fixture.Policies.Device("ortho").BasicUdi } };
        var start = new DateOnly(2025, 11, 15);
        var end = new DateOnly(2026, 11, 14);
        var series = PsurSeriesRules.Bind(new("cardio", "cardio", start, end, null, null, null, null, null), profile, []);
        var snapshot = PortfolioRules.Snapshot(new("cardio", "cardio", start, end, "Worldwide", state.Cases, state.Exposures,
            state.Trends, state.Capas, state.Evidence, state.Changes, clock.Now, state.History, state.SourceRuns), series);
        var imported = new Psur("historical-alias-claim", 1, "cardio", snapshot, Canonical.Hash(snapshot),
            "Synthetische historische Identitaetsrelation; keine bestaetigte Quellenfreigabe oder Produktumkodierung.", "", "Draft", [], null, true, [], AgentMode.Replay, null, null);
        imported = imported with { ScopeHash = PortfolioRules.PsurScope(imported) };
        await fixture.Store.Execute<Psur>(new(imported.Id, "psur", "cardio", 0, CaseScenario.Key(), "HistoricalAliasFixture",
            "Ein ungeklaerter historischer Identifierwechsel gibt eine bestehende fuehrende Periodenbindung nicht frei.",
            SqlFixture.Quality, imported, clock.Now), _ => imported);
        var limited = SqlFixture.Pms with { Products = ["ortho"] };
        Assert.Empty(await Service().ListPsurs(limited));
        Assert.Empty((await fixture.Store.Capture(["ortho"])).Psurs!);
        Assert.Equal("psur_lead_period_conflict", (await Assert.ThrowsAsync<DomainException>(() =>
            Service().Freeze(new("ortho", "ortho", start, end, "Worldwide"), limited, CaseScenario.Key()))).Code);
        Assert.Equal(Canonical.Hash(imported), Canonical.Hash(await fixture.Store.Get<Psur>("psur", imported.Id)));
    }

    [Fact]
    public async Task T16_NonLeadingCaseProvenanceIsVerifiedAgainstSqlNotOnlyTheGroupEnvelope()
    {
        await Case("cardio");
        var service = Service();
        var definition = await service.RecordGroup(Definition(), SqlFixture.Pms, CaseScenario.Key());
        var p = await service.Freeze(FreezeInput(definition), SqlFixture.Pms, CaseScenario.Key());
        var group = p.Snapshot.Group!;
        var member = group.Members.Single(m => m.Product.ProductId == "cardio");
        var source = Assert.Single(member.Snapshot.SourceManifest!.Cases);
        var modified = member with { Snapshot = member.Snapshot with { SourceManifest = member.Snapshot.SourceManifest! with {
            Cases = [source with { Reference = source.Reference with { Hash = new string('0', 64) } }] } } };
        var snapshot = p.Snapshot with { Group = group with {
            Members = group.Members.Select(m => m.Product.ProductId == "cardio" ? modified : m).ToArray() } };
        var forged = p with { Version = p.Version + 1, Snapshot = snapshot, SnapshotHash = Canonical.Hash(snapshot) };
        forged = forged with { ScopeHash = PortfolioRules.PsurScope(forged) };
        await fixture.Store.Execute<Psur>(new(p.Id, "psur", p.ProductId, p.Version, CaseScenario.Key(), "NonLeadingSourceTamper",
            "Selbst konsistente Gruppenhashes duerfen keinen falschen Quellfallhash des nichtfuehrenden Mitglieds legitimieren.",
            SqlFixture.Quality, forged, clock.Now), _ => forged);
        Assert.Equal("psur_source_version", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Approve(forged.Id, new(forged.Version, Reason), SqlFixture.Medical, CaseScenario.Key()))).Code);
        Assert.Empty((await service.GetPsur(forged.Id, SqlFixture.Pms)).Approvals);
    }

    [Fact]
    public async Task T16_ValidLongGeneratedGroupDraftRemainsEditableWithoutTruncationOrStaleApprovals()
    {
        for (var i = 0; i < 24; i++) await Case(i % 2 == 0 ? "ortho" : "cardio");
        var service = Service();
        var definition = await service.RecordGroup(Definition(), SqlFixture.Pms, CaseScenario.Key());
        var p = await service.Freeze(FreezeInput(definition), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal("Draft", p.Status);
        Assert.True(p.Draft.Length > 60000, $"Generated length: {p.Draft.Length}; real case/source expansion must cross the former edit limit.");
        Assert.True(p.Draft.Length < AgentBoundary.MaximumNarrativeLength - 200);
        Assert.DoesNotContain(p.Findings, f => f.Severity is "Critical" or "Major");
        var snapshot = Canonical.Serialize(p.Snapshot);
        p = await service.Approve(p.Id, new(p.Version, Reason), SqlFixture.Medical, CaseScenario.Key());
        Assert.Single(p.Approvals);
        var correction = p.Draft + "\nMedizinische Praezisierung: Ein zeitlicher Zusammenhang allein belegt keinen kausalen Produktbeitrag; die gebundenen Gegenhypothesen bleiben offen.";
        var revised = await service.Revise(p.Id, new(p.Version, correction, Reason), SqlFixture.Medical, CaseScenario.Key());
        Assert.Equal(correction, revised.Draft);
        Assert.Empty(revised.Approvals);
        Assert.NotEqual(p.ScopeHash, revised.ScopeHash);
        Assert.Equal(snapshot, Canonical.Serialize(revised.Snapshot));
        Assert.Equal(p.SnapshotHash, revised.SnapshotHash);
        Assert.DoesNotContain(revised.Findings, f => f.Severity is "Critical" or "Major");
        var overLimit = correction + new string('x', AgentBoundary.MaximumNarrativeLength - correction.Length + 1);
        await Assert.ThrowsAsync<DomainException>(() => service.Revise(revised.Id, new(revised.Version, overLimit, Reason),
            SqlFixture.Medical, CaseScenario.Key()));
        var input = new AgentInput(AgentRole.PsurAuthoring, AgentMode.Replay, null, p.Snapshot, null,
            PsurGroupRules.Documents(p.Snapshot), Reason, 0);
        Assert.Throws<DomainException>(() => AgentBoundary.ValidateResult(input,
            new("synthetic-output-boundary", AgentRunner.PromptVersion, null, overLimit, [], [])));
        var atLimit = correction.PadRight(AgentBoundary.MaximumNarrativeLength);
        AgentBoundary.ValidateResult(input, new("synthetic-output-boundary", AgentRunner.PromptVersion, null, atLimit, [], []));
        revised = await service.Revise(revised.Id, new(revised.Version, atLimit, Reason), SqlFixture.Medical, CaseScenario.Key());
        Assert.Equal(atLimit, revised.Draft);
        Assert.Equal(snapshot, Canonical.Serialize(revised.Snapshot));
        var paddedOverLimit = atLimit + "\n";
        var editError = await Record.ExceptionAsync(() => service.Revise(revised.Id,
            new(revised.Version, paddedOverLimit, Reason), SqlFixture.Medical, CaseScenario.Key()));
        var agentError = Record.Exception(() => AgentBoundary.ValidateResult(input,
            new("synthetic-output-boundary", AgentRunner.PromptVersion, null, paddedOverLimit, [], [])));
        Assert.True(editError is DomainException && agentError is DomainException,
            "Both generation and editing must reject the actual over-limit text, including whitespace; no hidden trim or unbounded padding.");
        Assert.Equal(Canonical.Hash(revised), Canonical.Hash(await service.GetPsur(revised.Id, SqlFixture.Pms)));
    }
}
