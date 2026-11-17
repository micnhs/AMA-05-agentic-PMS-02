using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Tests;

internal sealed class HistoricalReadStore(StoreEntry<string>[] current, StoreEntry<string>[] history) : IStateStore
{
    private readonly Dictionary<(string Kind, string Id), StoreEntry<string>> currentRows =
        current.ToDictionary(r => (r.Kind, r.Id));
    private readonly Dictionary<(string Kind, string Id, int Version), string> historicalRows =
        history.ToDictionary(r => (r.Kind, r.Id, r.Version), r => r.Value);
    public List<(string Kind, string Id)> CurrentReads { get; } = [];
    public List<(string Kind, string Id, int Version, string[] Products)> VersionReads { get; } = [];
    public int HistoricalPayloadReads { get; private set; }

    public Task<T?> Get<T>(string kind, string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        CurrentReads.Add((kind, id));
        return Task.FromResult<T?>(currentRows.TryGetValue((kind, id), out var row) ? Canonical.Read<T>(row.Value) : default);
    }

    public Task<T?> GetVersion<T>(string kind, string id, int version, string[] products, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        VersionReads.Add((kind, id, version, [.. products]));
        if (!currentRows.TryGetValue((kind, id), out var owner) || !products.Contains(owner.ProductId) ||
            !historicalRows.TryGetValue((kind, id, version), out var json))
            return Task.FromResult<T?>(default);
        HistoricalPayloadReads++;
        return Task.FromResult<T?>(Canonical.Read<T>(json));
    }

    public Task<T[]> List<T>(string kind, string[] products, CancellationToken ct = default) =>
        throw new InvalidOperationException("The selected read must not enumerate product aggregates.");
    public Task<T[]> History<T>(string kind, string[] products, CancellationToken ct = default) =>
        throw new InvalidOperationException("The selected read must not load full product histories.");
    public Task<PortfolioState> Capture(string[] products, CancellationToken ct = default) =>
        throw new InvalidOperationException("A frozen read must not capture live product data.");
    public Task<T?> Receipt<T>(CommandContext command, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<T> Execute<T>(CommandContext command, Func<T?, T> change, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Psur> FreezePsur(CommandContext command, PsurCaptureScope scope, Func<PortfolioState, Psur> create,
        CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AuditEvent[]> Audit(string id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OutboxEvent[]> PendingEvents(int limit, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OutboxEvent?> ClaimEvent(string eventId, string owner, CancellationToken ct = default) => throw new NotSupportedException();
    public Task CompleteEvent(string eventId, string consumer, string owner, string? error,
        CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OutboxIssue[]> OutboxIssues(string[] products, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> HasReceipt(string eventId, string consumer, CancellationToken ct = default) => throw new NotSupportedException();
}
