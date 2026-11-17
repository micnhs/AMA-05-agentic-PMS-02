using Cohors.Agents;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

[Collection("SQL pilot")]
[Trait("Category", "Sql")]
public sealed class AgentIdempotencyTests(SqlFixture fixture)
{
    [Fact]
    public async Task T06_T12_TwoActivitiesReadingTheSameQueuedVersionOnlyExecuteOneAgentSequence()
    {
        var s = new CaseScenario(fixture);
        await s.Create();
        s.Case = await s.Service.RequestAssessment(s.Case.Id, new(s.Case.Version, AgentMode.Replay,
            "Concurrent dispatch regression with the same immutable queued version."), SqlFixture.Pms, CaseScenario.Key());
        var run = s.Case.AgentRuns.Last();
        var runner = new CountingRunner();
        var barrier = new ReadBarrierStore(fixture.Store, s.Case.Id);
        var service = new CaseService(barrier, fixture.Objects, fixture.Policies, runner,
            new KnowledgeSearch(fixture.Store, fixture.Objects, fixture.Store), s.Clock, new MirComposer());
        async Task<string> Process()
        {
            try { await service.ProcessAssessment(s.Case.Id, run.Id); return "completed"; }
            catch (DomainException e) { return e.Code; }
        }
        var results = await Task.WhenAll(Process(), Process());
        Assert.Single(results, r => r == "completed");
        Assert.Single(results, r => r == "run_in_progress");
        Assert.Equal(3, runner.Count);
        var saved = await fixture.Store.Get<SafetyCase>("case", s.Case.Id);
        Assert.Single(saved!.Assessments);
        Assert.Equal("Completed", saved.AgentRuns.First(r => r.Id == run.Id).Status);
    }

    [Fact]
    public async Task T06_ReportRetriesAndStaleVersionsDoNotRepeatAuthoringCalls()
    {
        var s = new CaseScenario(fixture);
        await s.Create(); await s.Assess();
        var runner = new CountingRunner();
        var service = new CaseService(fixture.Store, fixture.Objects, fixture.Policies, runner,
            new KnowledgeSearch(fixture.Store, fixture.Objects, fixture.Store), s.Clock, new MirComposer());
        var input = new PrepareReportCommand(s.Case.Version, "Initial", "EU-IE", "de", true,
            "Klinische und technische Befunde bleiben offen.", "Idempotente wiederholte Entwurfsanfrage.");
        var key = CaseScenario.Key();
        var first = await service.Prepare(s.Case.Id, input, SqlFixture.Pms, key);
        var replay = await service.Prepare(s.Case.Id, input, SqlFixture.Pms, key);
        Assert.Equal(Canonical.Hash(first), Canonical.Hash(replay));
        Assert.Equal("version_conflict", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Prepare(s.Case.Id, input, SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Equal("idempotency_conflict", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Prepare(s.Case.Id, input with { Language = "en" }, SqlFixture.Pms, key))).Code);
        Assert.Equal(1, runner.Count);
    }

    [Fact]
    public async Task PublicReadsUseBoundedKeysAndPersistedReceiptsWithoutCreatingCases()
    {
        var reader = new CountingPublicReader();
        var service = new PublicSourceService(fixture.Store, fixture.Objects, reader, TimeProvider.System, fixture.Policies);
        var key = new string('k', 88) + CaseScenario.Key();
        var before = (await fixture.Store.List<SafetyCase>("case", ["ortho"])).Length;
        var first = await service.Read("fda", new("ortho"), SqlFixture.Pms, key, default);
        var retry = await service.Read("fda", new("ortho"), SqlFixture.Pms, key, default);
        Assert.Equal("ReadOnlyMetadata", first.Status);
        Assert.Equal(Canonical.Hash(first), Canonical.Hash(retry));
        Assert.Equal(1, reader.Count);
        Assert.Equal(before, (await fixture.Store.List<SafetyCase>("case", ["ortho"])).Length);
        var evidence = await service.Approve(first.Id, new(first.Version, "category-1",
            "Nur oeffentliche Kategorie-Metadaten; keine eigene Produktzuordnung und keine Inzidenz oder klinische Volltextaussage."),
            SqlFixture.Medical, CaseScenario.Key(), default);
        Assert.Equal("PublicMetadata", evidence.Kind);
        Assert.False(evidence.Synthetic);
    }

    [Theory]
    [InlineData("fda", "{}", null)]
    [InlineData("fda", "{\"results\":{}}", null)]
    [InlineData("fda", "{\"results\":[{\"term\":\" \",\"count\":1}]}", null)]
    [InlineData("fda", "{\"results\":[{\"term\":\"hip\",\"count\":\"one\"}]}", null)]
    [InlineData("pubmed", "{\"esearchresult\":{\"idlist\":[12]}}", null)]
    [InlineData("pubmed", "{\"esearchresult\":{\"idlist\":[\"12\"]}}", "{}")]
    [InlineData("pubmed", "{\"esearchresult\":{\"idlist\":[\"12\"]}}", "{\"result\":{}}")]
    [InlineData("pubmed", "{\"esearchresult\":{\"idlist\":[\"12\"]}}", "{\"result\":{\"12\":{\"title\":\"\",\"pubdate\":\"2026 Nov\"}}}")]
    public async Task PublicHttp200WithMalformedMetadataPersistsFailureInsteadOfReading(string source, string first, string? second)
    {
        using var handler = new MetadataHandler(first, second);
        using var http = new HttpClient(handler);
        var service = new PublicSourceService(fixture.Store, fixture.Objects, new PublicSources(http), TimeProvider.System, fixture.Policies);
        var key = CaseScenario.Key();
        var result = await service.Read(source, new("ortho"), SqlFixture.Pms, key, default);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("source_contract", result.Error);
        Assert.Empty(result.Items);
        Assert.Equal("Failed", (await fixture.Store.Get<PublicSourceRun>("source", result.Id))!.Status);
        var requests = handler.Requests;
        Assert.Equal("Failed", (await service.Read(source, new("ortho"), SqlFixture.Pms, key, default)).Status);
        Assert.Equal(requests, handler.Requests);
    }

    private sealed class MetadataHandler(string first, string? second) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(Requests == 1 ? first : second!, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CountingRunner : IAgentRunner
    {
        private int count;
        public int Count => count;
        public Task<AgentOutput> Run(AgentInput input, CancellationToken ct = default)
        {
            Interlocked.Increment(ref count);
            return new AgentRunner(new(null, null, null, null)).Run(input, ct);
        }
    }

    private sealed class CountingPublicReader : IPublicSourceReader
    {
        public int Count { get; private set; }
        public Task<PublicSourceItem[]> Read(string source, string productId, CancellationToken ct = default)
        {
            Count++;
            return Task.FromResult<PublicSourceItem[]>([new("category-1", "SYNTHETIC reader fixture",
                "https://open.fda.gov/apis/device/event/", null, "Fixture for reader contract, not an executed live read.",
                Canonical.HashText("category-1"))]);
        }
    }

    private sealed class ReadBarrierStore(IStateStore inner, string id) : IStateStore
    {
        private int reads;
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<T?> Get<T>(string kind, string key, CancellationToken ct = default)
        {
            var value = await inner.Get<T>(kind, key, ct);
            if (kind == "case" && key == id && Interlocked.Increment(ref reads) <= 2)
            {
                if (Volatile.Read(ref reads) == 2) gate.TrySetResult();
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            return value;
        }
        public Task<T?> GetVersion<T>(string kind, string key, int version, string[] products, CancellationToken ct = default) =>
            inner.GetVersion<T>(kind, key, version, products, ct);
        public Task<T[]> List<T>(string kind, string[] products, CancellationToken ct = default) => inner.List<T>(kind, products, ct);
        public Task<T[]> History<T>(string kind, string[] products, CancellationToken ct = default) => inner.History<T>(kind, products, ct);
        public Task<PortfolioState> Capture(string[] products, CancellationToken ct = default) => inner.Capture(products, ct);
        public Task<T?> Receipt<T>(CommandContext c, CancellationToken ct = default) => inner.Receipt<T>(c, ct);
        public Task<T> Execute<T>(CommandContext c, Func<T?, T> change, CancellationToken ct = default) => inner.Execute(c, change, ct);
        public Task<Psur> FreezePsur(CommandContext c, PsurCaptureScope scope, Func<PortfolioState, Psur> create, CancellationToken ct = default) => inner.FreezePsur(c, scope, create, ct);
        public Task<AuditEvent[]> Audit(string key, CancellationToken ct = default) => inner.Audit(key, ct);
        public Task<OutboxEvent[]> PendingEvents(int limit, CancellationToken ct = default) => inner.PendingEvents(limit, ct);
        public Task<OutboxEvent?> ClaimEvent(string eventId, string owner, CancellationToken ct = default) => inner.ClaimEvent(eventId, owner, ct);
        public Task CompleteEvent(string eventId, string consumer, string owner, string? error, CancellationToken ct = default) => inner.CompleteEvent(eventId, consumer, owner, error, ct);
        public Task<OutboxIssue[]> OutboxIssues(string[] products, CancellationToken ct = default) => inner.OutboxIssues(products, ct);
        public Task<bool> HasReceipt(string eventId, string consumer, CancellationToken ct = default) => inner.HasReceipt(eventId, consumer, ct);
    }
}
